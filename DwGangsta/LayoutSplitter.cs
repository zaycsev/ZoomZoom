using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

namespace DwGangsta
{
    /// <summary>
    /// Разделяет чертёж на отдельные файлы: один файл — один лист плюс та часть модели,
    /// которая видна через видовые экраны этого листа.
    /// </summary>
    public static class LayoutSplitter
    {
        /// <summary>Имена листов чертежа в порядке вкладок (без пространства модели).</summary>
        public static List<string> GetLayoutNames(Database db)
        {
            var pairs = new List<KeyValuePair<int, string>>();
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                var dict = (DBDictionary)tr.GetObject(db.LayoutDictionaryId, OpenMode.ForRead);
                foreach (DBDictionaryEntry entry in dict)
                {
                    var layout = tr.GetObject(entry.Value, OpenMode.ForRead) as Layout;
                    if (layout == null || layout.ModelType) continue;
                    pairs.Add(new KeyValuePair<int, string>(layout.TabOrder, layout.LayoutName));
                }
                tr.Commit();
            }
            pairs.Sort((a, b) => a.Key.CompareTo(b.Key));

            var names = new List<string>(pairs.Count);
            foreach (var p in pairs) names.Add(p.Value);
            return names;
        }

        /// <summary>
        /// Выполняет разделение. <paramref name="sourceDb"/> — база открытого чертежа;
        /// её текущее состояние снимается во временный файл, исходник на диске не изменяется.
        /// </summary>
        public static List<SplitResult> Split(Database sourceDb, string sourcePath,
                                              SplitOptions options, Action<string> log)
        {
            if (log == null) log = delegate { };
            var results = new List<SplitResult>();

            string dir = string.IsNullOrWhiteSpace(options.OutputFolder)
                ? Path.GetDirectoryName(sourcePath)
                : options.OutputFolder;
            string baseName = Path.GetFileNameWithoutExtension(sourcePath);
            string outDir = options.UseSubFolder ? Path.Combine(dir, baseName + ZoomZoom.Lang.T("_Листы", "_Layouts")) : dir;
            if (!Directory.Exists(outDir)) Directory.CreateDirectory(outDir);

            List<string> layouts = options.Layouts.Count > 0
                ? options.Layouts
                : GetLayoutNames(sourceDb);

            string snapshot = Path.Combine(Path.GetTempPath(),
                "DwGangsta_" + Guid.NewGuid().ToString("N") + ".dwg");

            log("Снимок текущего состояния чертежа...");
            Trace.Write("snapshot -> " + snapshot);
            sourceDb.SaveAs(snapshot, DwgVersion.Current);
            Trace.Write("snapshot saved");

            var usedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (string layoutName in layouts)
                {
                    var result = new SplitResult { LayoutName = layoutName };
                    results.Add(result);
                    try
                    {
                        string target = BuildTargetPath(outDir, baseName, layoutName, options, usedPaths);
                        result.FilePath = target;

                        Trace.Write("layout '" + layoutName + "' -> " + target);
                        File.Copy(snapshot, target, true);
                        File.SetAttributes(target, FileAttributes.Normal);

                        ProcessFile(target, layoutName, options, result);
                        log(string.Format(CultureInfo.CurrentCulture,
                            "  {0} -> {1}{2}", layoutName, Path.GetFileName(target),
                            result.Warning != null ? "  [" + result.Warning + "]" : ""));
                    }
                    catch (System.Exception ex)
                    {
                        result.Error = ex.Message;
                        log("  " + layoutName + " -> ОШИБКА: " + ex.Message);
                    }
                }
            }
            finally
            {
                try { if (File.Exists(snapshot)) File.Delete(snapshot); }
                catch (IOException) { /* временный файл удалит система */ }
            }
            return results;
        }

        static void ProcessFile(string path, string layoutName, SplitOptions options, SplitResult result)
        {
            using (var db = new Database(false, true))
            {
                Trace.Write("  read");
                db.ReadDwgFile(path, FileOpenMode.OpenForReadAndWriteNoShare, false, null);
                db.CloseInput(true);
                EntityBounds.Reset();   // номера объектов у каждого чертежа свои
                Trace.Write("  read ok");

                // Без этого внешние ссылки остаются незагруженными и не знают своих габаритов,
                // а значит по ним нельзя судить, попадают ли они в видовой экран.
                try { db.ResolveXrefs(true, false); }
                catch (System.Exception ex) { Trace.Write("  внешние ссылки не разрешены: " + ex.Message); }

                Database previous = HostApplicationServices.WorkingDatabase;
                HostApplicationServices.WorkingDatabase = db;
                bool restored = false;
                try
                {
                    List<ViewRegion> regions;
                    bool unsupported;

                    using (Transaction tr = db.TransactionManager.StartTransaction())
                    {
                        ObjectId layoutId = FindLayout(tr, db, layoutName);
                        if (layoutId.IsNull)
                            throw new InvalidOperationException("Лист «" + layoutName + "» не найден в чертеже.");

                        var layout = (Layout)tr.GetObject(layoutId, OpenMode.ForRead);
                        regions = ViewRegion.Collect(tr, layout, options, out unsupported);
                        Trace.Write("  regions=" + regions.Count + " unsupported=" + unsupported);
                        result.ViewportCount = regions.Count;
                        tr.Commit();
                    }

                    DeleteOtherLayouts(db, layoutName);
                    Trace.Write("  лишние листы удалены");

                    // Дальше подменённая рабочая база не нужна, а держать её опасно: AutoCAD
                    // считает её своей текущей и при сохранении сверяет с видом редактора.
                    HostApplicationServices.WorkingDatabase = previous;
                    restored = true;

                    if (!options.TrimModel)
                        result.Warning = "модель оставлена целиком (чистка отключена)";
                    else if (unsupported)
                        result.Warning = "есть 3D/перспективный ВЭ — модель оставлена целиком";
                    else if (regions.Count == 0)
                        result.Warning = "видовых экранов нет — модель оставлена целиком";
                    else
                    {
                        TrimModelSpace(db, regions, options, result);
                        Trace.Write("  trimmed: kept=" + result.EntitiesKept + " removed=" + result.EntitiesRemoved);
                    }

                    if (options.PurgeUnused) Purge(db);
                    Trace.Write("  purged");

                    // Разметка рисуется после чистки: она обводит то, что осталось,
                    // и сама под чистку попадать не должна.
                    if (options.DrawViewOutlines) DrawOutlines(db, layoutName, result);

                    TraceViewports(db, layoutName, "перед сохранением");
                    db.SaveAs(path, DwgVersion.Current);
                    Trace.Write("  saved");
                }
                finally
                {
                    if (!restored) HostApplicationServices.WorkingDatabase = previous;
                }
            }
        }

        /// <summary>
        /// Обводит в модели то, что показывает каждый видовой экран листа. Не переключаясь
        /// на лист, видно, что из модели попадает на чертёж.
        /// </summary>
        static void DrawOutlines(Database db, string layoutName, SplitResult result)
        {
            try
            {
                List<Point3dCollection> outlines;
                using (Transaction tr = db.TransactionManager.StartTransaction())
                {
                    ObjectId layoutId = FindLayout(tr, db, layoutName);
                    if (layoutId.IsNull) { tr.Commit(); return; }

                    var layout = (Layout)tr.GetObject(layoutId, OpenMode.ForRead);
                    outlines = ViewOutlines.Collect(tr, layout);
                    tr.Commit();
                }

                result.ViewOutlines = ViewOutlines.Draw(db, outlines, new Vector3d(0, 0, 0),
                                                        MergeOptions.ViewLayerName,
                                                        Commands.NextHighlightColor());
                ViewOutlines.HideInViewports(db, MergeOptions.ViewLayerName);
                Trace.Write("  контуров видовых экранов: " + result.ViewOutlines);
            }
            catch (System.Exception ex)
            {
                Trace.Write("  контуры видовых экранов не нарисованы: " + ex.Message);
            }
        }

        /// <summary>Диагностика: печатает параметры видовых экранов листа на текущем шаге.</summary>
        static void TraceViewports(Database db, string layoutName, string stage)
        {
            if (!Trace.Enabled) return;
            try
            {
                using (Transaction tr = db.TransactionManager.StartTransaction())
                {
                    ObjectId layoutId = FindLayout(tr, db, layoutName);
                    if (layoutId.IsNull) { Trace.Write("    [" + stage + "] лист не найден"); tr.Commit(); return; }

                    var layout = (Layout)tr.GetObject(layoutId, OpenMode.ForRead);
                    var btr = (BlockTableRecord)tr.GetObject(layout.BlockTableRecordId, OpenMode.ForRead);
                    foreach (ObjectId id in btr)
                    {
                        var vp = tr.GetObject(id, OpenMode.ForRead) as Viewport;
                        if (vp == null) continue;
                        Trace.Write("    [" + stage + "] ВЭ №" + vp.Number +
                                    " view=" + vp.ViewCenter + " h=" + vp.ViewHeight +
                                    " twist=" + vp.TwistAngle + " size=" + vp.Width + "x" + vp.Height);
                    }
                    tr.Commit();
                }
            }
            catch (System.Exception ex) { Trace.Write("    [" + stage + "] " + ex.Message); }
        }

        static ObjectId FindLayout(Transaction tr, Database db, string layoutName)
        {
            var dict = (DBDictionary)tr.GetObject(db.LayoutDictionaryId, OpenMode.ForRead);
            foreach (DBDictionaryEntry entry in dict)
            {
                var layout = tr.GetObject(entry.Value, OpenMode.ForRead) as Layout;
                if (layout == null || layout.ModelType) continue;
                if (string.Equals(layout.LayoutName, layoutName, StringComparison.OrdinalIgnoreCase))
                    return entry.Value;
            }
            return ObjectId.Null;
        }

        /// <summary>
        /// Оставляет в чертеже единственный лист. Работаем напрямую с базой, а не через
        /// LayoutManager: тот обслуживает активный документ, и на фоновой базе он либо
        /// отказывает, либо сбивает состояние видовых экранов.
        /// </summary>
        static void DeleteOtherLayouts(Database db, string keepName)
        {
            // Удаляем штатным механизмом: он убирает и лист, и его блок, и связи между ними.
            // А вот переключать активный лист (LayoutManager.CurrentLayout) на фоновой базе
            // нельзя — менеджер обслуживает активный документ, а не её.
            LayoutManager lm = LayoutManager.Current;
            foreach (string name in GetLayoutNames(db))
            {
                if (string.Equals(name, keepName, StringComparison.OrdinalIgnoreCase)) continue;
                try { lm.DeleteLayout(name); }
                catch (System.Exception ex) { Trace.Write("  лист «" + name + "» не удалён: " + ex.Message); }
            }

            // Оставшийся лист становится первой вкладкой. Активной её не помечаем:
            // у активного листа AutoCAD синхронизирует видовой экран с текущим видом базы.
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                ObjectId layoutId = FindLayout(tr, db, keepName);
                if (!layoutId.IsNull)
                {
                    var layout = (Layout)tr.GetObject(layoutId, OpenMode.ForWrite);
                    layout.TabOrder = 1;
                }
                tr.Commit();
            }
        }

        /// <summary>Удаляет из модели всё, что не попадает ни в один видовой экран листа.</summary>
        static void TrimModelSpace(Database db, List<ViewRegion> regions, SplitOptions options, SplitResult result)
        {
            var toErase = new List<ObjectId>();

            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                var ms = (BlockTableRecord)tr.GetObject(
                    SymbolUtilityServices.GetBlockModelSpaceId(db), OpenMode.ForRead);

                foreach (ObjectId id in ms)
                {
                    var ent = tr.GetObject(id, OpenMode.ForRead) as Entity;
                    if (ent == null) continue;

                    // Рамки и подписи сборки — разметка, а не чертёж: при разборке обратно
                    // на файлы они не нужны.
                    if (ent.Layer == MergeOptions.ServiceLayerName ||
                        ent.Layer == MergeOptions.ViewLayerName) { toErase.Add(id); continue; }

                    if (options.KeepAecObjects && IsAecObject(ent)) { result.EntitiesKept++; continue; }

                    // Незагруженная внешняя ссылка не знает своих габаритов и выглядела бы
                    // пустой точкой в начале координат — такую подложку удалять нельзя.
                    if (IsUnresolvedXref(ent, tr)) { result.EntitiesKept++; continue; }

                    Extents3d ext;
                    if (!TryGetExtents(ent, out ext)) { result.EntitiesKept++; continue; }

                    bool visible = false;
                    for (int i = 0; i < regions.Count && !visible; i++)
                        visible = regions[i].Intersects(ext);

                    if (visible) result.EntitiesKept++;
                    else toErase.Add(id);
                }
                tr.Commit();
            }

            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                foreach (ObjectId id in toErase)
                {
                    try
                    {
                        var ent = (Entity)tr.GetObject(id, OpenMode.ForWrite, false, true);
                        if (ent.IsErased) continue;
                        ent.Erase();
                        result.EntitiesRemoved++;
                    }
                    catch (System.Exception)
                    {
                        // объект удалить нельзя (взаимные ссылки, блокировка) — оставляем
                        result.EntitiesKept++;
                    }
                }
                tr.Commit();
            }
        }

        internal static bool TryGetExtents(Entity ent, out Extents3d ext)
        {
            return EntityBounds.TryGet(ent, out ext);
        }

        internal static bool IsUnresolvedXref(Entity ent, Transaction tr)
        {
            var reference = ent as BlockReference;
            if (reference == null) return false;

            var btr = tr.GetObject(reference.BlockTableRecord, OpenMode.ForRead) as BlockTableRecord;
            if (btr == null || !btr.IsFromExternalReference) return false;

            return btr.IsUnloaded || btr.XrefStatus != XrefStatus.Resolved;
        }

        static bool IsAecObject(Entity ent)
        {
            string name = ent.GetRXClass().Name;
            return name.StartsWith("Aec", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Очистка неиспользуемых слоёв, блоков, типов линий и стилей.</summary>
        static void Purge(Database db)
        {
            for (int pass = 0; pass < 4; pass++)
            {
                var ids = new ObjectIdCollection();
                using (Transaction tr = db.TransactionManager.StartTransaction())
                {
                    CollectSymbolTable(tr, db.BlockTableId, ids);
                    CollectSymbolTable(tr, db.LayerTableId, ids);
                    CollectSymbolTable(tr, db.LinetypeTableId, ids);
                    CollectSymbolTable(tr, db.TextStyleTableId, ids);
                    CollectSymbolTable(tr, db.DimStyleTableId, ids);
                    CollectSymbolTable(tr, db.UcsTableId, ids);
                    tr.Commit();
                }
                if (ids.Count == 0) break;

                db.Purge(ids);          // в коллекции остаётся только то, что действительно можно удалить
                if (ids.Count == 0) break;

                using (Transaction tr = db.TransactionManager.StartTransaction())
                {
                    foreach (ObjectId id in ids)
                    {
                        try
                        {
                            var obj = tr.GetObject(id, OpenMode.ForWrite, false, true);
                            if (!obj.IsErased) obj.Erase();
                        }
                        catch (System.Exception) { /* пропускаем */ }
                    }
                    tr.Commit();
                }
            }
        }

        static void CollectSymbolTable(Transaction tr, ObjectId tableId, ObjectIdCollection ids)
        {
            var table = tr.GetObject(tableId, OpenMode.ForRead) as SymbolTable;
            if (table == null) return;
            foreach (ObjectId id in table) ids.Add(id);
        }

        static string BuildTargetPath(string dir, string baseName, string layoutName,
                                      SplitOptions options, HashSet<string> used)
        {
            string safe = SanitizeFileName(layoutName);
            string path = Path.Combine(dir, baseName + "_" + safe + ".dwg");

            int index = 2;
            while (used.Contains(path) || (!options.Overwrite && File.Exists(path)))
            {
                path = Path.Combine(dir, baseName + "_" + safe + "_" + index + ".dwg");
                index++;
            }
            used.Add(path);
            return path;
        }

        static string SanitizeFileName(string name)
        {
            var invalid = new HashSet<char>(Path.GetInvalidFileNameChars());
            var sb = new StringBuilder(name.Length);
            foreach (char c in name) sb.Append(invalid.Contains(c) ? '_' : c);

            string result = sb.ToString().Trim().TrimEnd('.');
            return result.Length == 0 ? "Лист" : result;
        }
    }
}
