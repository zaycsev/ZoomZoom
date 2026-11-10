using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using AcAp = Autodesk.AutoCAD.ApplicationServices.Application;

namespace DwGangsta
{
    /// <summary>
    /// Собирает несколько чертежей в один: модели раскладываются рядом, не наслаиваясь,
    /// листы переносятся целиком, а видовые экраны сдвигаются вместе со своей геометрией
    /// и продолжают показывать то же, что и в исходном файле.
    /// </summary>
    public static class DrawingMerger
    {
        /// <summary>
        /// Цвета подписей — по добавленной папке. Обычные цвета AutoCAD, различимые
        /// на тёмном фоне: чертежи одной папки подписаны одинаково, соседняя папка —
        /// уже другим цветом, и сразу видно, что откуда приехало.
        /// </summary>
        static readonly short[] LabelColors = { 3, 4, 2, 6, 5, 1 };

        /// <summary>Цвет подписей чертежа: по той добавленной папке, внутри которой он лежит.</summary>
        static short ColorFor(string file, List<string> roots)
        {
            int found = -1, deepest = -1;
            if (roots != null)
            {
                for (int i = 0; i < roots.Count; i++)
                {
                    string root = roots[i];
                    if (string.IsNullOrEmpty(root)) continue;
                    if (!file.StartsWith(root, StringComparison.OrdinalIgnoreCase)) continue;

                    // Папку могли добавить вместе с вложенной — считаем по ближайшей.
                    if (root.Length > deepest) { deepest = root.Length; found = i; }
                }
            }
            return found < 0 ? LabelColors[0] : LabelColors[found % LabelColors.Length];
        }

        public static MergeResult Merge(MergeOptions options, Action<string> log)
        {
            if (log == null) log = delegate { };
            Trace.Write("сборка: файлов " + options.Files.Count);

            var result = new MergeResult { TargetPath = options.TargetPath };
            var layoutNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // AutoCAD добавляет на каждый новый лист свой видовой экран — он тут лишний.
            object savedCreateViewport = null;
            try
            {
                savedCreateViewport = AcAp.GetSystemVariable("LAYOUTCREATEVIEWPORT");
                AcAp.SetSystemVariable("LAYOUTCREATEVIEWPORT", 0);
            }
            catch (System.Exception) { savedCreateViewport = null; }

            using (var target = new Database(true, true))
            {
                Database previous = HostApplicationServices.WorkingDatabase;
                HostApplicationServices.WorkingDatabase = target;
                bool restored = false;
                try
                {
                    // В новой базе уже есть лист «Лист1». Уводим служебные листы под временные
                    // имена, чтобы исходные названия листов переносились как есть.
                    ParkExistingLayouts(target);

                    // Файлы одной серии идут подряд и встают в модели в один ряд. Набрали
                    // из нескольких папок — сборка повторяет их структуру: папка к папке,
                    // в порядке имён. Всё из одной папки — серии ищутся по названиям файлов.
                    bool byFolders;
                    List<TableCell> cells;
                    List<NameGroup> groups = Plan(options.Files, options.Roots, options.GroupSimilarNames,
                                                  options.Placement, out byFolders, out cells);

                    bool named = options.GroupSimilarNames && groups.Count > 1;
                    string what = byFolders ? "папка" : "серия";
                    var names = new StyleNames();               // разводит однофамильцев
                    var civil = new CivilStyles();              // возвращает стили объектам Civil 3D
                    bool setupTaken = false;                   // устройство чертежа берём у первого файла

                    // Сперва переносим всё как есть, не двигая: раскладка «по осям и номерам»
                    // выравнивает участки друг по другу, а для этого нужны размеры всех сразу.
                    var imported = new List<List<Imported>>();

                    for (int g = 0; g < groups.Count; g++)
                    {
                        NameGroup group = groups[g];
                        var inGroup = new List<Imported>();
                        imported.Add(inGroup);

                        if (named)
                        {
                            log(string.Format("  {0} «{1}»: файлов {2}", what, group.Title, group.Files.Count));
                            Trace.Write(what + " «" + group.Title + "»: файлов " + group.Files.Count);
                        }

                        foreach (string file in group.Files)
                        {
                            var item = new MergeItemResult
                            {
                                FilePath = file,
                                FileName = Path.GetFileNameWithoutExtension(file),
                                Group = group.Title
                            };
                            result.Items.Add(item);

                            try
                            {
                                Imported one = ImportDrawing(target, previous, file, item, options, layoutNames,
                                                             byFolders ? group.Title : "",
                                                             ColorFor(file, options.Roots), names, civil,
                                                             ref setupTaken);
                                if (one != null) inGroup.Add(one);
                                log(string.Format("  {0}: объектов {1}, листов {2}{3}{4}{5}",
                                    item.FileName, item.EntitiesCopied, item.Layouts.Count,
                                    item.EntitiesTrimmed > 0
                                        ? ", вне листов оставлено " + item.EntitiesTrimmed
                                        : "",
                                    item.CivilRenamed > 0
                                        ? ", переименовано поверхностей и стилей Civil 3D: " + item.CivilRenamed
                                        : "",
                                    Spacious(one)));
                            }
                            catch (System.Exception ex)
                            {
                                item.Error = ex.Message;
                                log("  " + item.FileName + " -> ОШИБКА: " + ex.Message);
                                Trace.Write("ошибка на файле " + file + ": " + ex);
                            }
                        }
                    }

                    Spread(imported, cells, options, log);

                    FrameStyle style = MeasureFrames(imported, options);

                    var placed = new List<List<Placed>>();   // рамки участков по группам
                    foreach (List<Imported> inGroup in imported)
                    {
                        var frames = new List<Placed>();
                        placed.Add(frames);
                        foreach (Imported one in inGroup)
                        {
                            Placed spot = PlaceDrawing(target, one, style, options);
                            if (spot != null) frames.Add(spot);
                        }
                    }

                    if (named) DrawGroupTitles(target, groups, placed, options);

                    RemoveServiceLayouts(target, layoutNames);
                    if (options.TabsInListOrder) OrderTabs(target, options.Files, result.Items);
                    ViewOutlines.HideInViewports(target, options.ServiceLayer);
                    ViewOutlines.HideInViewports(target, options.ViewLayer);

                    // Рабочую базу возвращаем до сохранения: пока она подменена, AutoCAD
                    // переносит в её видовые экраны вид активного документа.
                    HostApplicationServices.WorkingDatabase = previous;
                    restored = true;

                    target.SaveAs(options.TargetPath, DwgVersion.Current);
                    Trace.Write("сборка сохранена: " + options.TargetPath);
                }
                finally
                {
                    if (!restored) HostApplicationServices.WorkingDatabase = previous;
                    if (savedCreateViewport != null)
                    {
                        try { AcAp.SetSystemVariable("LAYOUTCREATEVIEWPORT", savedCreateViewport); }
                        catch (System.Exception) { }
                    }
                }
            }
            return result;
        }

        /// <summary>Участок в сборке: его рамка и высота подписи.</summary>
        class Placed
        {
            public Extents3d Frame;
            public double LabelHeight;
            public short Color;
        }

        /// <summary>
        /// Раздаёт участкам место. «Сортировка по осям и номерам» строит таблицу: папка —
        /// колонка сверху вниз, одинаковые по счёту этапы всех папок — на одной высоте.
        /// Всё из одной папки — участки идут сеткой по сериям. Сортировка выключена —
        /// чертежи остаются в своих исходных координатах, как были в файлах.
        /// </summary>
        /// <summary>
        /// Выстраивает файлы каждой папки так же, как они лягут в модели: слева направо
        /// по осям. Вкладки листов создаются в этом же порядке — лист под листом читается
        /// так же, как участок под участком.
        /// </summary>
        static void ByCells(List<NameGroup> groups, List<TableCell> cells)
        {
            var order = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (TableCell cell in cells)
            {
                if (!order.ContainsKey(cell.File)) order[cell.File] = cell.Row * 10000 + cell.Column;
            }

            foreach (NameGroup group in groups)
            {
                group.Files.Sort(delegate(string a, string b)
                {
                    int first, second;
                    bool knownA = order.TryGetValue(a, out first);
                    bool knownB = order.TryGetValue(b, out second);

                    if (knownA && knownB && first != second) return first.CompareTo(second);
                    if (knownA != knownB) return knownA ? -1 : 1;
                    return NameGrouper.ComparePaths(a, b);
                });
            }
        }

        static void Spread(List<List<Imported>> imported, List<TableCell> cells,
                           MergeOptions options, Action<string> log)
        {
            var all = new List<Imported>();
            foreach (List<Imported> inGroup in imported) all.AddRange(inGroup);
            if (all.Count == 0) return;

            if (options.Placement == MergePlacement.AsIs)
            {
                log("  раскладка: исходные координаты");
                return;
            }

            if (options.Placement == MergePlacement.NoOverlap)
            {
                int moved = Unpile(all, options.GapFactor);
                log("  раскладка: исходные координаты, раздвинуто участков — " + moved);
                return;
            }

            var files = new List<string>();
            var sizes = new Dictionary<string, TableLayout.Size>(StringComparer.OrdinalIgnoreCase);
            foreach (Imported one in all)
            {
                if (!one.Bounds.HasValue) continue;
                files.Add(one.Item.FilePath);
                sizes[one.Item.FilePath] = new TableLayout.Size
                {
                    Cell = one.Bounds.Value,
                    Frame = one.Drawing.HasValue ? one.Drawing.Value : one.Bounds.Value
                };
            }

            // Клетки уже посчитаны до переноса — по ним же шли и вкладки листов.
            if (cells == null) cells = TableLayout.Arrange(files, options.Roots, options.GroupSimilarNames);
            Dictionary<string, Vector3d> offsets = TableLayout.Offsets(cells, sizes, options.GapFactor);

            int columns = 0, rows = 0;
            foreach (TableCell cell in cells)
            {
                if (cell.Column + 1 > columns) columns = cell.Column + 1;
                if (cell.Row + 1 > rows) rows = cell.Row + 1;
            }
            log(string.Format("  раскладка: колонок {0}, строк {1}", columns, rows));

            foreach (Imported one in all)
            {
                Vector3d offset;
                if (offsets.TryGetValue(one.Item.FilePath, out offset)) one.Offset = offset;
            }
        }

        /// <summary>
        /// Раздвигает участки, оставшиеся в исходных координатах: чертёж стоит там, где
        /// стоял, пока не наложился на уже поставленный. Наложился — отходит в ближайшую
        /// сторону ровно настолько, чтобы разойтись. Возвращает, сколько пришлось двигать.
        /// </summary>
        static int Unpile(List<Imported> all, double gapFactor)
        {
            var taken = new List<Extents3d>();
            int moved = 0;

            foreach (Imported one in all)
            {
                if (!one.Bounds.HasValue) continue;

                Extents3d box = one.Bounds.Value;
                double gap = Math.Max(Math.Max(box.MaxPoint.X - box.MinPoint.X,
                                               box.MaxPoint.Y - box.MinPoint.Y) * gapFactor, 1.0);

                var offset = new Vector3d(0, 0, 0);
                for (int guard = 0; guard <= taken.Count * 2; guard++)
                {
                    Extents3d here = Move(box, offset);

                    int hit = -1;
                    for (int i = 0; i < taken.Count && hit < 0; i++)
                    {
                        if (Overlap(here, taken[i])) hit = i;
                    }
                    if (hit < 0) break;

                    offset = offset + Aside(here, taken[hit], gap);
                }

                if (!offset.IsZeroLength()) moved++;
                one.Offset = offset;
                taken.Add(Move(box, offset));
            }
            return moved;
        }

        static bool Overlap(Extents3d a, Extents3d b)
        {
            return a.MinPoint.X < b.MaxPoint.X && b.MinPoint.X < a.MaxPoint.X &&
                   a.MinPoint.Y < b.MaxPoint.Y && b.MinPoint.Y < a.MaxPoint.Y;
        }

        /// <summary>В какую сторону отойти от занятого места — в ближайшую из четырёх.</summary>
        static Vector3d Aside(Extents3d moving, Extents3d fixedBox, double gap)
        {
            double right = fixedBox.MaxPoint.X - moving.MinPoint.X + gap;
            double left = moving.MaxPoint.X - fixedBox.MinPoint.X + gap;
            double up = fixedBox.MaxPoint.Y - moving.MinPoint.Y + gap;
            double down = moving.MaxPoint.Y - fixedBox.MinPoint.Y + gap;

            double best = Math.Min(Math.Min(right, left), Math.Min(up, down));
            if (best == right) return new Vector3d(right, 0, 0);
            if (best == left) return new Vector3d(-left, 0, 0);
            if (best == up) return new Vector3d(0, up, 0);
            return new Vector3d(0, -down, 0);
        }

        /// <summary>
        /// Заметка в отчёт, если модель чертежа заметно больше того, что показано на его
        /// листах: рамка участка обведёт всю модель, и такой участок выйдет великоватым.
        /// Обычно это забытая копия плана в стороне.
        /// </summary>
        static string Spacious(Imported one)
        {
            if (one == null || !one.Drawing.HasValue || !one.Sheets.HasValue) return "";

            Extents3d model = one.Drawing.Value, sheets = one.Sheets.Value;
            double wide = Ratio(model.MaxPoint.X - model.MinPoint.X, sheets.MaxPoint.X - sheets.MinPoint.X);
            double tall = Ratio(model.MaxPoint.Y - model.MinPoint.Y, sheets.MaxPoint.Y - sheets.MinPoint.Y);
            double times = Math.Max(wide, tall);

            return times >= 3.0
                ? string.Format(", модель шире листов в {0:F0} раза — рамка по ней", times)
                : "";
        }

        static double Ratio(double model, double sheets)
        {
            return sheets > 1e-9 ? model / sheets : 1.0;
        }

        /// <summary>Перенесённый чертёж: всё, что нужно, чтобы поставить его на место.</summary>
        class Imported
        {
            public MergeItemResult Item;
            public List<ObjectId> Clones = new List<ObjectId>();      // объекты модели
            public List<ObjectId> Viewports = new List<ObjectId>();   // ВЭ его листов
            public List<Point3dCollection> Outlines = new List<Point3dCollection>();

            public Extents3d? Bounds;    // клетка: геометрия вместе с кадрами ВЭ
            public Extents3d? Drawing;   // габариты самой геометрии — по ним рамка
            public Extents3d? Sheets;    // то, что показано на листах
            public double TextHeight;    // типовая высота текста чертежа
            public short Color;
            public Vector3d Offset;
        }

        /// <summary>
        /// Вливает один чертёж: модель, затем его листы. Ничего не сдвигает — место
        /// участкам раздаётся потом, когда известны размеры всех.
        /// </summary>
        static Imported ImportDrawing(Database target, Database home, string file, MergeItemResult item,
                                      MergeOptions options, HashSet<string> layoutNames,
                                      string folder, short color, StyleNames names,
                                      CivilStyles civil, ref bool setupTaken)
        {
            Trace.Write("вливаем " + file);

            using (var source = new Database(false, true))
            {
                // Читаем исходник при родной рабочей базе, а не при подставленной сборке.
                // Иначе Civil 3D заводит стили чтения не там, и поверхности приезжают
                // в сборку без стиля: ссылка пустая, после сохранения их не видно.
                Database working = HostApplicationServices.WorkingDatabase;
                HostApplicationServices.WorkingDatabase = home;
                try
                {
                    source.ReadDwgFile(file, FileOpenMode.OpenForReadAndAllShare, false, null);
                    source.CloseInput(true);
                }
                finally
                {
                    HostApplicationServices.WorkingDatabase = working;
                }
                EntityBounds.Reset();   // номера объектов у каждого чертежа свои
                try { source.ResolveXrefs(true, false); }
                catch (System.Exception ex) { Trace.Write("  внешние ссылки: " + ex.Message); }

                // Пустой чертёж сборки AutoCAD строит по своим умолчаниям — безразмерным
                // и имперским. Перенимаем устройство первого файла: единицы, систему мер,
                // масштаб типов линий. Иначе сборка живёт не по тем правилам, что исходники.
                if (!setupTaken)
                {
                    DrawingSetup.CopyFrom(source, target);
                    setupTaken = true;
                }

                // Одноимённые, но разные стили и блоки развести до переноса: иначе надписи
                // чертежа возьмут чужой размер, а блок — чужой вид.
                int renamed = names.MakeUnique(source, target, item.FileName);
                if (renamed > 0) Trace.Write("  разведено стилей и блоков: " + renamed);

                // То же для Civil 3D: поверхности с занятыми именами и стили, которые под
                // тем же именем в сборке настроены иначе. Стили заодно уезжают в сборку.
                item.CivilRenamed = civil.Prepare(source, target, item.FileName);

                List<ObjectId> clones = CloneModelSpace(source, target, item, civil);
                try
                {
                    return Finish(source, target, item, options, layoutNames, folder, color, clones, civil);
                }
                catch
                {
                    // Модель уже приехала, а до раскладки дело не дошло: не убрать её —
                    // она останется лежать в своих координатах поверх соседних участков.
                    // Сорвался перенос — в сборке от этого файла не должно остаться ничего.
                    Discard(target, clones, item);
                    throw;
                }
            }
        }

        /// <summary>Всё, что после переноса модели: замеры участка и листы.</summary>
        static Imported Finish(Database source, Database target, MergeItemResult item,
                               MergeOptions options, HashSet<string> layoutNames, string folder,
                               short color, List<ObjectId> clones, CivilStyles civil)
        {
            {
                bool unsupported;
                List<ViewRegion> regions = CollectViewRegions(source, out unsupported);

                // Объекты, которых не видно ни в одном листе, в сборку не едут: иначе
                // клетка участка резервируется под них, и чертежи разъезжаются на
                // километры пустоты. Отбор тот же, что и при разборке по листам.
                // Видовой экран с 3D или перспективой обрезать по плоской рамке нельзя —
                // такой чертёж переносится целиком.
                if (options.TrimModel && !unsupported)
                {
                    item.EntitiesTrimmed = TrimToViewports(target, clones, regions);
                    item.EntitiesCopied = clones.Count;
                }

                // Рамка обводит всё, что приехало из этого файла: в модели сборки не должно
                // остаться объекта, про который непонятно, из какого он чертежа.
                Extents3d? drawing = GetExtents(target, clones);

                // Типовая высота текста — по тому, что видно в листах: подпись участка
                // читается наравне с самим чертежом, а не с забытой в стороне пометкой.
                Content content = MeasureContent(target, clones, regions);

                // Клетка шире рамки: в неё обязан влезть ещё и кадр видовых экранов,
                // иначе в лист одного чертежа попадёт соседний.
                Extents3d? bounds = Combine(drawing, RegionsBounds(regions));
                TraceSizes(item.FileName, content.Bounds, drawing, RegionsBounds(regions));

                var imported = new Imported
                {
                    Item = item,
                    Clones = clones,
                    Bounds = bounds,
                    Drawing = drawing,
                    Sheets = content.Bounds,
                    TextHeight = content.TextHeight,
                    Color = color
                };

                CopyLayouts(source, target, item, layoutNames, folder, imported, civil);
                return imported;
            }
        }

        /// <summary>
        /// Убирает из сборки следы чертежа, который перенести не удалось: его модель
        /// и уже заведённые листы. Иначе он лёг бы неразложенным поверх соседей.
        /// </summary>
        static void Discard(Database target, List<ObjectId> clones, MergeItemResult item)
        {
            try
            {
                using (Transaction tr = target.TransactionManager.StartTransaction())
                {
                    foreach (ObjectId id in clones)
                    {
                        if (id.IsNull || id.IsErased) continue;
                        var ent = tr.GetObject(id, OpenMode.ForWrite, false, true) as Entity;
                        if (ent != null && !ent.IsErased) ent.Erase();
                    }
                    tr.Commit();
                }
            }
            catch (System.Exception ex)
            {
                Trace.Write("  модель несостоявшегося чертежа не убрана: " + ex.Message);
            }

            foreach (string name in item.Layouts)
            {
                try { LayoutManager.Current.DeleteLayout(name); }
                catch (System.Exception ex) { Trace.Write("  лист «" + name + "» не убран: " + ex.Message); }
            }
            item.Layouts.Clear();
        }

        /// <summary>
        /// Вид рамок, общий для всей сборки: отступ от чертежа и полоска под подпись.
        /// Общий, чтобы рамки соседних участков вставали ровно друг под другом, — иначе
        /// каждая отступала бы по-своему и ряд бы «поехал».
        /// </summary>
        class FrameStyle
        {
            public double Padding;
            public double Band;
        }

        static FrameStyle MeasureFrames(List<List<Imported>> imported, MergeOptions options)
        {
            var spans = new List<double>();
            var labels = new List<double>();

            foreach (List<Imported> inGroup in imported)
            {
                foreach (Imported one in inGroup)
                {
                    if (!one.Drawing.HasValue) continue;

                    Extents3d box = one.Drawing.Value;
                    double width = box.MaxPoint.X - box.MinPoint.X;
                    double height = box.MaxPoint.Y - box.MinPoint.Y;
                    double span = Math.Max(width, height);
                    spans.Add(span);

                    labels.Add(LabelHeight(one.Item.FileName, width, span, one.TextHeight));
                }
            }
            if (spans.Count == 0) return new FrameStyle { Padding = 1.0, Band = 0.0 };

            // Медиана, а не самый крупный: один чертёж-гигант не должен раздуть все рамки.
            double padding = Math.Max(Median(spans) * 0.02, 1.0);
            double band = options.DrawLabels ? Median(labels) * 1.8 : 0.0;
            return new FrameStyle { Padding = padding, Band = band };
        }

        /// <summary>Ставит перенесённый чертёж на отведённое ему место и обводит рамкой.</summary>
        static Placed PlaceDrawing(Database target, Imported one, FrameStyle style, MergeOptions options)
        {
            Vector3d offset = one.Offset;
            if (!offset.IsZeroLength())
            {
                MoveEntities(target, one.Clones, offset);
                ShiftViewports(target, one.Viewports, offset);
            }

            Extents3d? frame = one.Drawing.HasValue ? one.Drawing : one.Bounds;
            if (!frame.HasValue) return null;

            Extents3d moved = Move(frame.Value, offset);
            double labelHeight = 0.0;
            if (options.DrawFrames || options.DrawLabels)
                labelHeight = DrawFrame(target, moved, one.Item.FileName, one.TextHeight,
                                        one.Color, style, options);

            if (options.DrawViewOutlines)
                ViewOutlines.Draw(target, one.Outlines, offset, options.ViewLayer, one.Color);

            return new Placed { Frame = moved, LabelHeight = labelHeight, Color = one.Color };
        }


        /// <summary>
        /// Подписывает ряд именем папки (или серии), из которой в нём чертежи. Подпись
        /// садится в пустоту над рядом — ту самую, что раскладка под неё и оставила, —
        /// и живёт на служебном слое, то есть в листы не попадает.
        /// </summary>
        static void DrawGroupTitles(Database target, List<NameGroup> groups,
                                    List<List<Placed>> placed, MergeOptions options)
        {
            if (!options.DrawLabels) return;

            var all = new List<Extents3d>();
            foreach (List<Placed> spots in placed)
            {
                foreach (Placed spot in spots) all.Add(spot.Frame);
            }

            for (int g = 0; g < groups.Count && g < placed.Count; g++)
            {
                if (placed[g].Count == 0 || string.IsNullOrEmpty(groups[g].Title)) continue;

                Extents3d? row = null;
                double height = 0.0;
                short color = placed[g][0].Color;
                foreach (Placed spot in placed[g])
                {
                    row = Combine(row, spot.Frame);
                    if (spot.LabelHeight > height) height = spot.LabelHeight;   // как подпись файла
                }
                if (!row.HasValue || height <= 0.0) continue;

                DrawTitle(target, row.Value, FreeSpaceAbove(row.Value, all),
                          groups[g].Title, height, color, options);
            }
        }

        /// <summary>Сколько пустоты над рядом до ближайшего участка сверху.</summary>
        static double FreeSpaceAbove(Extents3d row, List<Extents3d> all)
        {
            double free = double.MaxValue;
            foreach (Extents3d frame in all)
            {
                if (frame.MinPoint.Y <= row.MaxPoint.Y) continue;                     // не выше ряда
                if (frame.MaxPoint.X < row.MinPoint.X) continue;                      // не над рядом
                if (frame.MinPoint.X > row.MaxPoint.X) continue;

                double distance = frame.MinPoint.Y - row.MaxPoint.Y;
                if (distance < free) free = distance;
            }
            // Над верхним рядом никого нет — места сколько угодно.
            if (free == double.MaxValue) free = (row.MaxPoint.Y - row.MinPoint.Y) * 0.5;
            return Math.Max(free, 0.0);
        }

        static void DrawTitle(Database target, Extents3d row, double free, string title,
                              double labelHeight, short color, MergeOptions options)
        {
            double width = row.MaxPoint.X - row.MinPoint.X;

            // Подпись ряда читается наравне с подписями участков под ней — тем же ростом.
            int chars = Math.Max(title.Length, 1);
            double height = Math.Min(labelHeight, width / (chars * 0.75));
            if (free > 0.0) height = Math.Min(height, free * 0.5);
            if (height <= 1e-9) return;

            ObjectId layerId = ViewOutlines.EnsureLayer(target, options.ServiceLayer, 8);

            using (Transaction tr = target.TransactionManager.StartTransaction())
            {
                var ms = (BlockTableRecord)tr.GetObject(
                    SymbolUtilityServices.GetBlockModelSpaceId(target), OpenMode.ForWrite);

                var text = new DBText
                {
                    TextString = title,
                    Height = height,
                    Position = new Point3d(row.MinPoint.X, row.MaxPoint.Y + height * 0.35, 0),
                    Color = Color.FromColorIndex(ColorMethod.ByAci, color)   // как и подписи чертежей
                };
                if (!layerId.IsNull) text.LayerId = layerId;

                ms.AppendEntity(text);
                tr.AddNewlyCreatedDBObject(text, true);
                tr.Commit();
            }
        }

        static List<ObjectId> CloneModelSpace(Database source, Database target, MergeItemResult item,
                                              CivilStyles civil)
        {
            var sourceIds = new ObjectIdCollection();
            using (Transaction tr = source.TransactionManager.StartTransaction())
            {
                var ms = (BlockTableRecord)tr.GetObject(
                    SymbolUtilityServices.GetBlockModelSpaceId(source), OpenMode.ForRead);
                foreach (ObjectId id in ms) sourceIds.Add(id);
                tr.Commit();
            }

            var clones = new List<ObjectId>();
            if (sourceIds.Count == 0) return clones;

            ObjectId targetModelSpace = SymbolUtilityServices.GetBlockModelSpaceId(target);
            Dictionary<ObjectId, string> styled = civil.Remember(source, sourceIds);
            using (var mapping = new IdMapping())
            {
                // Ignore: одноимённые слои и блоки берутся из уже собранного, а не переписываются.
                source.WblockCloneObjects(sourceIds, targetModelSpace, mapping,
                                          DuplicateRecordCloning.Ignore, false);

                int restyled = civil.Restore(target, mapping, styled);
                if (restyled > 0) Trace.Write("  объектам Civil 3D возвращён стиль: " + restyled);

                foreach (ObjectId id in sourceIds)
                {
                    if (!mapping.Contains(id)) continue;
                    IdPair pair = mapping[id];
                    if (pair.IsCloned && !pair.Value.IsNull) clones.Add(pair.Value);
                }
            }

            item.EntitiesCopied = clones.Count;
            return clones;
        }

        /// <summary>Переносит листы исходника вместе с их содержимым.</summary>
        static void CopyLayouts(Database source, Database target, MergeItemResult item,
                                HashSet<string> layoutNames, string folder, Imported imported,
                                CivilStyles civil)
        {
            List<string> sourceNames = LayoutSplitter.GetLayoutNames(source);

            foreach (string sourceName in sourceNames)
            {
                string name = LayoutName(folder, item.FileName, sourceName,
                                         sourceNames.Count > 1, layoutNames);

                ObjectId newLayoutId = CreateLayout(name, layoutNames, out name);
                layoutNames.Add(name);
                item.Layouts.Add(name);

                ObjectId sourceLayoutId = ObjectId.Null;
                ObjectId paperSpaceId = ObjectId.Null;      // видовой экран самого листа
                var sourceIds = new ObjectIdCollection();

                using (Transaction tr = source.TransactionManager.StartTransaction())
                {
                    var dict = (DBDictionary)tr.GetObject(source.LayoutDictionaryId, OpenMode.ForRead);
                    foreach (DBDictionaryEntry entry in dict)
                    {
                        var lay = tr.GetObject(entry.Value, OpenMode.ForRead) as Layout;
                        if (lay == null || lay.ModelType) continue;
                        if (!string.Equals(lay.LayoutName, sourceName, StringComparison.OrdinalIgnoreCase)) continue;

                        sourceLayoutId = entry.Value;
                        var btr = (BlockTableRecord)tr.GetObject(lay.BlockTableRecordId, OpenMode.ForRead);

                        // Переносим содержимое листа целиком, включая экран самого листа.
                        // В фоновой базе новый лист создаётся без него, и без переноса
                        // окно в модель заняло бы его место.
                        var ids = new List<ObjectId>();
                        var viewports = new List<Viewport>();
                        var viewportIds = new List<ObjectId>();
                        foreach (ObjectId id in btr)
                        {
                            ids.Add(id);

                            var vp = tr.GetObject(id, OpenMode.ForRead) as Viewport;
                            if (vp == null) continue;
                            viewports.Add(vp);
                            viewportIds.Add(id);
                        }

                        // Сам лист — не обязательно первый ВЭ в блоке: опознаём его по виду.
                        int paperIndex = ViewRegion.PaperSpaceIndex(viewports);
                        if (paperIndex >= 0 && paperIndex < viewportIds.Count)
                            paperSpaceId = viewportIds[paperIndex];

                        // Экран самого листа переносим первым. AutoCAD отводит эту роль
                        // первому видовому экрану блока — и если первым окажется настоящее
                        // окно, лист заберёт его себе: содержимое окна с листа пропадёт,
                        // а бывший экран листа развернётся пустой рамкой во весь лист.
                        if (!paperSpaceId.IsNull) sourceIds.Add(paperSpaceId);
                        foreach (ObjectId id in ids)
                        {
                            if (id != paperSpaceId) sourceIds.Add(id);
                        }
                        break;
                    }
                    tr.Commit();
                }

                ObjectId newBtrId;
                using (Transaction tr = target.TransactionManager.StartTransaction())
                {
                    var newLayout = (Layout)tr.GetObject(newLayoutId, OpenMode.ForRead);
                    newBtrId = newLayout.BlockTableRecordId;
                    tr.Commit();
                }

                // Экрана самого листа в чертеже может не быть — лист ни разу не открывали.
                // Тогда заводим его сами, и обязательно до переноса: он должен оказаться
                // первым в блоке, иначе листом станет настоящее окно.
                ObjectId ownPaperId = paperSpaceId.IsNull
                    ? AddPaperViewport(target, newBtrId)
                    : ObjectId.Null;

                var movable = new List<ObjectId>();
                if (sourceIds.Count > 0)
                {
                    Dictionary<ObjectId, string> styled = civil.Remember(source, sourceIds);
                    using (var mapping = new IdMapping())
                    {
                        source.WblockCloneObjects(sourceIds, newBtrId, mapping,
                                                  DuplicateRecordCloning.Ignore, false);
                        civil.Restore(target, mapping, styled);

                        foreach (ObjectId id in sourceIds)
                        {
                            // Видовой экран самого листа живёт в координатах бумаги —
                            // его сдвигать вслед за моделью нельзя.
                            if (id == paperSpaceId) continue;
                            if (!mapping.Contains(id)) continue;

                            IdPair pair = mapping[id];
                            if (pair.IsCloned && !pair.Value.IsNull) movable.Add(pair.Value);
                        }
                    }
                }

                if (!ownPaperId.IsNull) FitPaperViewport(target, newBtrId, ownPaperId);

                // Модель поедет на своё место — видовые экраны должны поехать за ней.
                imported.Viewports.AddRange(movable);
                CollectOutlines(source, sourceLayoutId, imported);

                CopyPlotSettings(source, sourceLayoutId, target, newLayoutId);
            }
        }

        /// <summary>
        /// Заводит на листе экран самого листа — тот, что AutoCAD держит первым в блоке
        /// и окном в модель не показывает. Нужен, когда в исходном чертеже его не было.
        /// </summary>
        static ObjectId AddPaperViewport(Database target, ObjectId btrId)
        {
            try
            {
                using (Transaction tr = target.TransactionManager.StartTransaction())
                {
                    var btr = (BlockTableRecord)tr.GetObject(btrId, OpenMode.ForWrite);

                    var vp = new Viewport();
                    btr.AppendEntity(vp);
                    tr.AddNewlyCreatedDBObject(vp, true);
                    tr.Commit();
                    return vp.ObjectId;
                }
            }
            catch (System.Exception ex)
            {
                Trace.Write("  экран листа не заведён: " + ex.Message);
                return ObjectId.Null;
            }
        }

        /// <summary>
        /// Подгоняет заведённый экран листа под сам лист: он обязан показывать себя
        /// один к одному, иначе его примут за окно в модель.
        /// </summary>
        static void FitPaperViewport(Database target, ObjectId btrId, ObjectId viewportId)
        {
            try
            {
                using (Transaction tr = target.TransactionManager.StartTransaction())
                {
                    var btr = (BlockTableRecord)tr.GetObject(btrId, OpenMode.ForRead);

                    Extents3d? sheet = null;
                    foreach (ObjectId id in btr)
                    {
                        if (id == viewportId) continue;

                        var ent = tr.GetObject(id, OpenMode.ForRead) as Entity;
                        if (ent == null) continue;

                        Extents3d ext;
                        if (!EntityBounds.TryGet(ent, out ext)) continue;
                        if (sheet.HasValue) { Extents3d t = sheet.Value; t.AddExtents(ext); sheet = t; }
                        else sheet = ext;
                    }

                    // Пустой лист — берём A3 в миллиметрах: размер роли не играет,
                    // важно лишь, чтобы экран был на месте и показывал сам себя.
                    double x0 = 0.0, y0 = 0.0, x1 = 420.0, y1 = 297.0;
                    if (sheet.HasValue)
                    {
                        x0 = sheet.Value.MinPoint.X; y0 = sheet.Value.MinPoint.Y;
                        x1 = sheet.Value.MaxPoint.X; y1 = sheet.Value.MaxPoint.Y;
                    }
                    double width = Math.Max(x1 - x0, 1.0);
                    double height = Math.Max(y1 - y0, 1.0);
                    var center = new Point3d((x0 + x1) / 2.0, (y0 + y1) / 2.0, 0.0);

                    var vp = (Viewport)tr.GetObject(viewportId, OpenMode.ForWrite);
                    vp.CenterPoint = center;
                    vp.Width = width;
                    vp.Height = height;
                    vp.ViewTarget = Point3d.Origin;
                    vp.ViewDirection = Vector3d.ZAxis;
                    vp.ViewCenter = new Point2d(center.X, center.Y);
                    vp.ViewHeight = height;
                    tr.Commit();
                }
            }
            catch (System.Exception ex)
            {
                Trace.Write("  экран листа не подогнан: " + ex.Message);
            }
        }

        /// <summary>
        /// Запоминает контуры видовых экранов листа в координатах модели. Сам лист
        /// (первый ВЭ блока) окном в модель не является и контура не даёт.
        /// </summary>
        static void CollectOutlines(Database source, ObjectId layoutId, Imported imported)
        {
            if (layoutId.IsNull) return;
            try
            {
                using (Transaction tr = source.TransactionManager.StartTransaction())
                {
                    var layout = (Layout)tr.GetObject(layoutId, OpenMode.ForRead);
                    imported.Outlines.AddRange(ViewOutlines.Collect(tr, layout));
                    tr.Commit();
                }
            }
            catch (System.Exception ex)
            {
                Trace.Write("  контуры видовых экранов не собраны: " + ex.Message);
            }
        }

        /// <summary>Сдвигает точку взгляда видовых экранов вслед за сдвинутой моделью.</summary>
        static void ShiftViewports(Database target, List<ObjectId> ids, Vector3d offset)
        {
            using (Transaction tr = target.TransactionManager.StartTransaction())
            {
                foreach (ObjectId id in ids)
                {
                    var vp = tr.GetObject(id, OpenMode.ForRead) as Viewport;
                    if (vp == null) continue;

                    // Точка цели задана в мировых координатах, поэтому сдвиг переносится
                    // на неё как есть — независимо от поворота и масштаба вида.
                    vp.UpgradeOpen();
                    vp.ViewTarget = vp.ViewTarget + offset;
                }
                tr.Commit();
            }
        }

        static void CopyPlotSettings(Database source, ObjectId sourceLayoutId,
                                     Database target, ObjectId targetLayoutId)
        {
            if (sourceLayoutId.IsNull) return;
            try
            {
                var settings = new PlotSettings(false);
                using (Transaction tr = source.TransactionManager.StartTransaction())
                {
                    var sourceLayout = (Layout)tr.GetObject(sourceLayoutId, OpenMode.ForRead);
                    settings.CopyFrom(sourceLayout);
                    tr.Commit();
                }

                using (Transaction tr = target.TransactionManager.StartTransaction())
                {
                    var targetLayout = (Layout)tr.GetObject(targetLayoutId, OpenMode.ForWrite);
                    targetLayout.CopyFrom(settings);
                    tr.Commit();
                }
            }
            catch (System.Exception ex)
            {
                // Настройки печати не критичны: лист останется с параметрами по умолчанию.
                Trace.Write("  параметры печати не перенесены: " + ex.Message);
            }
        }

        static void MoveEntities(Database target, List<ObjectId> ids, Vector3d offset)
        {
            Matrix3d displacement = Matrix3d.Displacement(offset);

            // Размер, который нарисован не по своему стилю, при сдвиге перерисуется по стилю —
            // и станет другим. Поэтому его картинку запоминаем до переноса и возвращаем после.
            List<DimPicture> pictures = RememberDimensions(target, ids);

            using (Transaction tr = target.TransactionManager.StartTransaction())
            {
                int stayed = 0;
                foreach (ObjectId id in ids)
                {
                    Entity ent = null;
                    try { ent = tr.GetObject(id, OpenMode.ForWrite, false, true) as Entity; }
                    catch (System.Exception) { stayed++; continue; }
                    if (ent == null || ent.IsErased) continue;

                    try { ent.TransformBy(displacement); }
                    catch (System.Exception) { stayed++; }   // объект не поддаётся переносу
                }
                if (stayed > 0) Trace.Write("    не переехало объектов: " + stayed);
                tr.Commit();
            }

            RestoreDimensions(target, pictures, displacement);
        }

        /// <summary>Картинка размера: то, чем он нарисован на самом деле.</summary>
        class DimPicture
        {
            public ObjectId Dimension;
            public List<Entity> Pieces = new List<Entity>();
        }

        /// <summary>
        /// Запоминает, чем размеры нарисованы сейчас. AutoCAD рисует размер блоком; пока
        /// размер не трогают, блок лежит как есть, даже если стиль с тех пор изменили.
        /// Сдвиг же считается изменением — блок пересобирается по стилю, и в чертежах,
        /// где стиль картинке не соответствует, у размеров «вырастает» текст.
        /// </summary>
        static List<DimPicture> RememberDimensions(Database target, List<ObjectId> ids)
        {
            var pictures = new List<DimPicture>();
            try
            {
                using (Transaction tr = target.TransactionManager.StartTransaction())
                {
                    foreach (ObjectId id in ids)
                    {
                        var dim = tr.GetObject(id, OpenMode.ForRead) as Dimension;
                        if (dim == null || dim.DimBlockId.IsNull) continue;

                        var block = tr.GetObject(dim.DimBlockId, OpenMode.ForRead) as BlockTableRecord;
                        if (block == null) continue;

                        var picture = new DimPicture { Dimension = id };
                        foreach (ObjectId pieceId in block)
                        {
                            var piece = tr.GetObject(pieceId, OpenMode.ForRead) as Entity;
                            if (piece == null) continue;

                            try { picture.Pieces.Add((Entity)piece.Clone()); }
                            catch (System.Exception) { }
                        }
                        if (picture.Pieces.Count > 0) pictures.Add(picture);
                    }
                    tr.Commit();
                }
            }
            catch (System.Exception ex)
            {
                Trace.Write("    картинки размеров не запомнены: " + ex.Message);
            }
            return pictures;
        }

        /// <summary>На сколько размер должен измениться, чтобы считать это поломкой (1.2 = на пятую).</summary>
        const double DimensionTolerance = 1.2;

        /// <summary>
        /// Стал ли размер после переноса другим. Сверяем по надписи внутри блока — именно
        /// она и разъезжается: у размера, нарисованного не по своему стилю, пересборка
        /// поднимает высоту текста в разы. Надписи нет ни там, ни там — сверяем габариты.
        /// </summary>
        static bool TooDifferent(DimPicture picture, Transaction tr, BlockTableRecord block)
        {
            double wasText = 0.0, nowText = 0.0;
            Extents3d? was = null, now = null;

            foreach (Entity piece in picture.Pieces) Measure(piece, ref wasText, ref was);
            foreach (ObjectId id in block)
            {
                var piece = tr.GetObject(id, OpenMode.ForRead) as Entity;
                if (piece != null) Measure(piece, ref nowText, ref now);
            }

            // Надпись появилась или пропала — это уже другой размер.
            if (wasText > 1e-9 != nowText > 1e-9) return true;
            if (wasText > 1e-9) return Apart(wasText, nowText);

            return Apart(Height(was), Height(now));
        }

        static void Measure(Entity piece, ref double textHeight, ref Extents3d? bounds)
        {
            double height = TextHeightOf(piece);
            if (height > textHeight) textHeight = height;

            Extents3d ext;
            if (EntityBounds.TryGet(piece, out ext)) bounds = Combine(bounds, ext);
        }

        static double Height(Extents3d? ext)
        {
            return ext.HasValue ? ext.Value.MaxPoint.Y - ext.Value.MinPoint.Y : 0.0;
        }

        static bool Apart(double was, double now)
        {
            if (was <= 1e-9 || now <= 1e-9) return false;
            return now > was * DimensionTolerance || was > now * DimensionTolerance;
        }

        /// <summary>Возвращает размерам их картинку, сдвинутую вместе с чертежом.</summary>
        static void RestoreDimensions(Database target, List<DimPicture> pictures, Matrix3d displacement)
        {
            if (pictures.Count == 0) return;

            int restored = 0;
            try
            {
                using (Transaction tr = target.TransactionManager.StartTransaction())
                {
                    foreach (DimPicture picture in pictures)
                    {
                        var dim = tr.GetObject(picture.Dimension, OpenMode.ForRead) as Dimension;
                        if (dim == null || dim.IsErased || dim.DimBlockId.IsNull) continue;

                        var block = tr.GetObject(dim.DimBlockId, OpenMode.ForRead) as BlockTableRecord;
                        if (block == null) continue;

                        // Пересобранный блок обычно совпадает с прежним — тогда его и оставляем:
                        // трогать то, что и так приехало верно, незачем. Возвращаем картинку
                        // только там, где размер после переноса стал заметно другим.
                        if (!TooDifferent(picture, tr, block)) continue;

                        block.UpgradeOpen();
                        var old = new List<ObjectId>();
                        foreach (ObjectId pieceId in block) old.Add(pieceId);
                        foreach (ObjectId pieceId in old)
                        {
                            var piece = tr.GetObject(pieceId, OpenMode.ForWrite, false, true) as Entity;
                            if (piece != null && !piece.IsErased) piece.Erase();
                        }

                        foreach (Entity saved in picture.Pieces)
                        {
                            var piece = (Entity)saved.Clone();
                            piece.TransformBy(displacement);
                            block.AppendEntity(piece);
                            tr.AddNewlyCreatedDBObject(piece, true);
                        }
                        restored++;
                    }
                    tr.Commit();
                }
                Trace.Write("    размеров с сохранённой картинкой: " + restored);
            }
            catch (System.Exception ex)
            {
                Trace.Write("    картинки размеров не возвращены: " + ex.Message);
            }
            finally
            {
                foreach (DimPicture picture in pictures)
                {
                    foreach (Entity saved in picture.Pieces)
                    {
                        try { saved.Dispose(); }
                        catch (System.Exception) { }
                    }
                }
            }
        }

        static Extents3d? GetExtents(Database db, List<ObjectId> ids)
        {
            Extents3d? total = null;
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                foreach (ObjectId id in ids)
                {
                    var ent = tr.GetObject(id, OpenMode.ForRead) as Entity;
                    if (ent == null) continue;

                    Extents3d ext;
                    if (!EntityBounds.TryGet(ent, out ext)) continue;   // не влияет на раскладку

                    if (total.HasValue) { Extents3d t = total.Value; t.AddExtents(ext); total = t; }
                    else total = ext;
                }
                tr.Commit();
            }
            return total;
        }

        /// <summary>В журнал: из-за чего клетка участка получилась такой, какая есть.</summary>
        static void TraceSizes(string name, Extents3d? content, Extents3d? model, Extents3d? regions)
        {
            if (!Trace.Enabled) return;
            Trace.Write("  " + name + ": чертёж " + Size(content) +
                        ", вся модель " + Size(model) + ", кадры ВЭ " + Size(regions));
        }

        static string Size(Extents3d? ext)
        {
            if (!ext.HasValue) return "—";
            Extents3d e = ext.Value;
            return string.Format("{0:F0}x{1:F0}", e.MaxPoint.X - e.MinPoint.X, e.MaxPoint.Y - e.MinPoint.Y);
        }

        /// <summary>Кадры всех видовых экранов чертежа — то, что показывают его листы.</summary>
        /// <param name="unsupported">true, если хоть один ВЭ показывает 3D или перспективу.</param>
        static List<ViewRegion> CollectViewRegions(Database source, out bool unsupported)
        {
            var regions = new List<ViewRegion>();
            var options = new SplitOptions();
            unsupported = false;

            using (Transaction tr = source.TransactionManager.StartTransaction())
            {
                var dict = (DBDictionary)tr.GetObject(source.LayoutDictionaryId, OpenMode.ForRead);
                foreach (DBDictionaryEntry entry in dict)
                {
                    var layout = tr.GetObject(entry.Value, OpenMode.ForRead) as Layout;
                    if (layout == null || layout.ModelType) continue;

                    bool flat;
                    regions.AddRange(ViewRegion.Collect(tr, layout, options, out flat));
                    if (flat) unsupported = true;
                }
                tr.Commit();
            }
            return regions;
        }

        /// <summary>
        /// Убирает из перенесённой модели всё, что не попадает ни в один кадр видового
        /// экрана этого же чертежа. На листах от этого ничего не меняется — там таких
        /// объектов и не было видно, — зато участок занимает ровно столько места,
        /// сколько занимает сам чертёж. Возвращает, сколько объектов убрано.
        /// </summary>
        static int TrimToViewports(Database target, List<ObjectId> clones, List<ViewRegion> regions)
        {
            if (regions.Count == 0) return 0;   // обрезать не по чему — оставляем всё

            var toErase = new List<ObjectId>();
            using (Transaction tr = target.TransactionManager.StartTransaction())
            {
                foreach (ObjectId id in clones)
                {
                    var ent = tr.GetObject(id, OpenMode.ForRead) as Entity;
                    if (ent == null) continue;

                    // Незагруженная внешняя ссылка не знает своих габаритов и выглядела бы
                    // пустой точкой в начале координат — такую подложку удалять нельзя.
                    if (LayoutSplitter.IsUnresolvedXref(ent, tr)) continue;

                    Extents3d ext;
                    if (!LayoutSplitter.TryGetExtents(ent, out ext)) continue;

                    bool visible = false;
                    for (int i = 0; i < regions.Count && !visible; i++) visible = regions[i].Intersects(ext);
                    if (!visible) toErase.Add(id);
                }
                tr.Commit();
            }
            if (toErase.Count == 0) return 0;

            var erased = new HashSet<ObjectId>();
            using (Transaction tr = target.TransactionManager.StartTransaction())
            {
                foreach (ObjectId id in toErase)
                {
                    try
                    {
                        var ent = (Entity)tr.GetObject(id, OpenMode.ForWrite, false, true);
                        if (ent.IsErased) continue;
                        ent.Erase();
                        erased.Add(id);
                    }
                    catch (System.Exception)
                    {
                        // объект удалить нельзя (взаимные ссылки, блокировка) — оставляем
                    }
                }
                tr.Commit();
            }

            clones.RemoveAll(delegate(ObjectId id) { return erased.Contains(id); });
            return erased.Count;
        }

        /// <summary>Область модели, которую охватывают кадры видовых экранов.</summary>
        static Extents3d? RegionsBounds(List<ViewRegion> regions)
        {
            Extents3d? total = null;
            foreach (ViewRegion region in regions) total = Combine(total, region.Bounds);
            return total;
        }

        /// <summary>Содержимое участка: его габариты и типовая высота текста чертежа.</summary>
        class Content
        {
            public Extents3d? Bounds;
            public double TextHeight;   // 0, если текста в чертеже нет
        }

        /// <summary>
        /// Меряет то, ради чего чертёж и собирали: объекты модели, попавшие хотя бы в один
        /// видовой экран, — так же, как их отбирает разборка по листам. Габариты объекта
        /// обрезаются по кадру: длинная трасса, уходящая за край, не растягивает участок
        /// на всю свою длину. Листов с видовыми экранами нет — берётся модель целиком.
        /// </summary>
        static Content MeasureContent(Database target, List<ObjectId> ids, List<ViewRegion> regions)
        {
            var content = new Content();
            var heights = new List<double>();

            using (Transaction tr = target.TransactionManager.StartTransaction())
            {
                foreach (ObjectId id in ids)
                {
                    var ent = tr.GetObject(id, OpenMode.ForRead) as Entity;
                    if (ent == null) continue;

                    Extents3d ext;
                    if (!EntityBounds.TryGet(ent, out ext)) continue;   // габаритов нет — не в счёт

                    Extents3d? visible = ClipToRegions(ext, regions);
                    if (!visible.HasValue) continue;

                    content.Bounds = Combine(content.Bounds, visible);

                    double h = TextHeightOf(ent);
                    if (h > 1e-9) heights.Add(h);
                }
                tr.Commit();
            }

            content.TextHeight = Median(heights);
            return content;
        }

        /// <summary>Часть габаритов объекта, попавшая в кадры видовых экранов.</summary>
        static Extents3d? ClipToRegions(Extents3d ext, List<ViewRegion> regions)
        {
            if (regions.Count == 0) return ext;

            Extents3d? visible = null;
            foreach (ViewRegion region in regions)
            {
                if (!region.Intersects(ext)) continue;
                visible = Combine(visible, Intersect(ext, region.Bounds));
            }
            return visible;
        }

        static Extents3d? Intersect(Extents3d a, Extents3d b)
        {
            double x0 = Math.Max(a.MinPoint.X, b.MinPoint.X), x1 = Math.Min(a.MaxPoint.X, b.MaxPoint.X);
            double y0 = Math.Max(a.MinPoint.Y, b.MinPoint.Y), y1 = Math.Min(a.MaxPoint.Y, b.MaxPoint.Y);
            if (x1 < x0 || y1 < y0) return null;

            double z0 = Math.Min(a.MinPoint.Z, a.MaxPoint.Z), z1 = Math.Max(a.MinPoint.Z, a.MaxPoint.Z);
            return new Extents3d(new Point3d(x0, y0, z0), new Point3d(x1, y1, z1));
        }

        static double TextHeightOf(Entity ent)
        {
            var text = ent as DBText;                 // сюда же попадают атрибуты блоков
            if (text != null) return text.Height;

            var mtext = ent as MText;
            if (mtext != null) return mtext.TextHeight;

            return 0.0;
        }

        /// <summary>Медиана: одиночный заголовок и мелкие пометки не перетягивают её на себя.</summary>
        static double Median(List<double> values)
        {
            if (values.Count == 0) return 0.0;
            values.Sort();
            return values[values.Count / 2];
        }

        static Extents3d? Combine(Extents3d? first, Extents3d? second)
        {
            if (!first.HasValue) return second;
            if (!second.HasValue) return first;

            Extents3d result = first.Value;
            result.AddExtents(second.Value);
            return result;
        }

        static Extents3d Move(Extents3d ext, Vector3d offset)
        {
            return new Extents3d(ext.MinPoint + offset, ext.MaxPoint + offset);
        }

        /// <summary>Рамка вокруг участка и подпись с именем исходного файла. Отдаёт высоту подписи.</summary>
        static double DrawFrame(Database target, Extents3d bounds, string label,
                                double drawingTextHeight, short color, FrameStyle style,
                                MergeOptions options)
        {
            double width = bounds.MaxPoint.X - bounds.MinPoint.X;
            double height = bounds.MaxPoint.Y - bounds.MinPoint.Y;
            double span = Math.Max(width, height);
            double padding = style.Padding;

            // Подпись живёт в полоске под верхней стороной рамки: не торчит наружу
            // и не ложится на чертёж. Полоска общая для всей сборки — рамки встают ровно
            // друг под другом, — поэтому подпись в неё и вписывается.
            double band = options.DrawLabels ? style.Band : 0.0;

            double textHeight = LabelHeight(label, width + padding * 2.0, span, drawingTextHeight);
            if (band > 0.0) textHeight = Math.Min(textHeight, band / 1.8);

            double x0 = bounds.MinPoint.X - padding, x1 = bounds.MaxPoint.X + padding;
            double y0 = bounds.MinPoint.Y - padding, y1 = bounds.MaxPoint.Y + padding + band;

            ObjectId layerId = ViewOutlines.EnsureLayer(target, options.ServiceLayer, 8);

            using (Transaction tr = target.TransactionManager.StartTransaction())
            {
                var ms = (BlockTableRecord)tr.GetObject(
                    SymbolUtilityServices.GetBlockModelSpaceId(target), OpenMode.ForWrite);

                if (options.DrawFrames)
                {
                    var frame = new Polyline();
                    frame.AddVertexAt(0, new Point2d(x0, y0), 0, 0, 0);
                    frame.AddVertexAt(1, new Point2d(x1, y0), 0, 0, 0);
                    frame.AddVertexAt(2, new Point2d(x1, y1), 0, 0, 0);
                    frame.AddVertexAt(3, new Point2d(x0, y1), 0, 0, 0);
                    frame.Closed = true;
                    if (!layerId.IsNull) frame.LayerId = layerId;

                    ms.AppendEntity(frame);
                    tr.AddNewlyCreatedDBObject(frame, true);
                }

                if (options.DrawLabels)
                {
                    var text = new DBText
                    {
                        TextString = label,
                        Height = textHeight,
                        Position = new Point3d(x0 + textHeight * 0.5, y1 - band + textHeight * 0.4, 0),
                        Color = Color.FromColorIndex(ColorMethod.ByAci, color)
                    };
                    if (!layerId.IsNull) text.LayerId = layerId;

                    ms.AppendEntity(text);
                    tr.AddNewlyCreatedDBObject(text, true);
                }
                tr.Commit();
            }
            return textHeight;
        }

        /// <summary>
        /// Высота подписи. За основу берётся типовой текст самого чертежа — подпись читается
        /// наравне с ним. Дальше два ограничения: не мельче, чем видно на участке целиком,
        /// и не шире рамки, чтобы имя файла не вылезло за неё и не налезло на соседний участок.
        /// </summary>
        static double LabelHeight(string label, double frameWidth, double span, double drawingTextHeight)
        {
            double result = drawingTextHeight > 1e-9 ? drawingTextHeight : span * 0.01;
            if (result <= 1e-9) result = frameWidth * 0.05;   // участок в одну точку

            if (span > 1e-9)
            {
                double smallest = span * 0.002;   // мельче — не разглядеть на общем виде
                double largest = span * 0.03;     // крупнее — подпись спорит с чертежом
                if (result < smallest) result = smallest;
                if (result > largest) result = largest;
            }

            // Строка с отступом от левой стороны должна уместиться в рамку по ширине.
            int chars = Math.Max(label == null ? 0 : label.Length, 1);
            double byWidth = frameWidth / (chars * 0.75 + 1.0);   // ширина знака примерно 0.75 высоты
            if (result > byWidth) result = byWidth;

            return Math.Max(result, 1e-6);
        }

        /// <summary>
        /// Переименовывает листы, с которыми родилась новая база, в служебные имена.
        /// Иначе исходный «Лист1» не удастся перенести под своим именем.
        /// </summary>
        static void ParkExistingLayouts(Database db)
        {
            int index = 0;
            foreach (string name in LayoutSplitter.GetLayoutNames(db))
            {
                index++;
                try { LayoutManager.Current.RenameLayout(name, "__DwGangsta_" + index); }
                catch (System.Exception ex) { Trace.Write("лист «" + name + "» не переименован: " + ex.Message); }
            }
        }

        /// <summary>Убирает служебные листы, оставшиеся от пустой базы.</summary>
        /// <summary>
        /// Серии и порядок файлов, в котором сборка их переносит. Клетки считаются по одним
        /// именам файлов, до всякого переноса: по ним и раскладка в модели, и порядок
        /// вкладок — папка за папкой, а внутри папки по осям, чтобы листы читались так же,
        /// как участки в модели. Одна папка считается так же: строка на серию, в строке —
        /// по осям. Чертежи не открываются, поэтому окно сборки может заранее показать
        /// тот же порядок.
        /// </summary>
        public static List<NameGroup> Plan(IList<string> files, IList<string> roots, bool groupSimilar,
                                           MergePlacement placement, out bool byFolders,
                                           out List<TableCell> cells)
        {
            byFolders = groupSimilar && NameGrouper.FromSeveralFolders(files);
            List<NameGroup> groups;
            if (!groupSimilar) groups = NameGrouper.Single(files);
            else if (byFolders) groups = NameGrouper.ByFolders(files);
            else groups = NameGrouper.Group(files);

            cells = null;
            if (placement == MergePlacement.ByName)
            {
                cells = TableLayout.Arrange(files, roots, groupSimilar);
                ByCells(groups, cells);
            }
            return groups;
        }

        /// <summary>Файлы подряд в том порядке, в каком сборка создаст их листы.</summary>
        public static List<string> PlannedOrder(IList<string> files, IList<string> roots,
                                                bool groupSimilar, MergePlacement placement)
        {
            bool byFolders;
            List<TableCell> cells;
            var order = new List<string>();
            foreach (NameGroup group in Plan(files, roots, groupSimilar, placement, out byFolders, out cells))
                order.AddRange(group.Files);
            return order;
        }

        /// <summary>
        /// Вкладки листов — в порядке списка файлов из окна сборки: его расставляют кнопками
        /// «Влево» / «Вправо», ровно как листы встанут в диспетчере листов. Раскладка
        /// участков в модели от этого не меняется.
        /// </summary>
        static void OrderTabs(Database db, IList<string> files, List<MergeItemResult> items)
        {
            var wanted = new List<string>();
            foreach (string file in files)
                foreach (MergeItemResult item in items)
                    if (string.Equals(item.FilePath, file, StringComparison.OrdinalIgnoreCase))
                        wanted.AddRange(item.Layouts);

            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                var dict = (DBDictionary)tr.GetObject(db.LayoutDictionaryId, OpenMode.ForRead);
                var rest = new List<Layout>();
                var ordered = new List<Layout>();
                foreach (string name in wanted)
                    if (dict.Contains(name))
                        ordered.Add((Layout)tr.GetObject(dict.GetAt(name), OpenMode.ForWrite));
                foreach (DBDictionaryEntry entry in dict)
                {
                    var layout = (Layout)tr.GetObject(entry.Value, OpenMode.ForRead);
                    if (!layout.ModelType && !ordered.Contains(layout)) rest.Add(layout);
                }
                rest.Sort((a, b) => a.TabOrder.CompareTo(b.TabOrder));

                int tab = 1;                     // 0 — вкладка «Модель»
                foreach (Layout layout in ordered) layout.TabOrder = tab++;
                foreach (Layout layout in rest) { layout.UpgradeOpen(); layout.TabOrder = tab++; }
                tr.Commit();
            }
        }

        static void RemoveServiceLayouts(Database db, HashSet<string> created)
        {
            foreach (string name in LayoutSplitter.GetLayoutNames(db))
            {
                if (created.Contains(name)) continue;
                try { LayoutManager.Current.DeleteLayout(name); }
                catch (System.Exception) { /* последний лист удалить нельзя — не страшно */ }
            }
        }

        /// <summary>
        /// Имя листа сохраняется как в исходном чертеже. Два листа с одним именем в одном
        /// чертеже невозможны, поэтому при совпадении добавляем имя файла — так видно,
        /// откуда лист пришёл.
        /// </summary>
        /// <summary>
        /// Имя вкладки в сборке: папка и файл, из которых лист приехал. По исходному
        /// имени листа («Лист1» в каждом втором файле) найти нужную вкладку нельзя,
        /// поэтому оно добавляется только к файлам, где листов больше одного.
        /// </summary>
        static string LayoutName(string folder, string fileName, string layoutName,
                                 bool severalLayouts, HashSet<string> used)
        {
            string basis = fileName;
            if (!string.IsNullOrEmpty(folder)) basis = folder + " — " + basis;
            if (severalLayouts) basis = basis + " — " + layoutName;

            string name = SanitizeLayoutName(basis);
            if (!used.Contains(name)) return name;

            // Номер дописывается к уже обрезанному имени: иначе обрезка по длине
            // отрезала бы и его, и все «(2)», «(3)» слились бы в одно и то же имя.
            for (int index = 2; ; index++)
            {
                string suffix = " (" + index + ")";
                string head = name.Length + suffix.Length > MaxLayoutName
                    ? name.Substring(0, MaxLayoutName - suffix.Length).TrimEnd()
                    : name;
                string candidate = head + suffix;
                if (!used.Contains(candidate)) return candidate;
            }
        }

        /// <summary>Длина имени вкладки с запасом: у AutoCAD предел 255 знаков.</summary>
        const int MaxLayoutName = 200;

        static string SanitizeLayoutName(string name)
        {
            const string forbidden = "<>/\\\":;?*|,=`";
            var sb = new StringBuilder(name.Length);
            foreach (char c in name) sb.Append(forbidden.IndexOf(c) >= 0 || char.IsControl(c) ? '_' : c);

            // Сперва режем по длине, потом подравниваем края. Наоборот нельзя: обрезка
            // запросто приходится на пробел, а имя с пробелом на конце AutoCAD не берёт.
            // Так и падала сборка: стоило добавить к имени папку, длинное имя сдвигалось,
            // резалось ровно по пробелу — и лист не создавался.
            string result = sb.ToString();
            if (result.Length > MaxLayoutName) result = result.Substring(0, MaxLayoutName);
            result = result.Trim();
            if (result.Length == 0) result = "Лист";
            return result;
        }

        /// <summary>
        /// Заводит вкладку. Если AutoCAD имя всё-таки не принял, берётся простое запасное
        /// «Лист N»: одно неудачное имя не должно ронять перенос целого чертежа.
        /// </summary>
        static ObjectId CreateLayout(string wanted, HashSet<string> used, out string name)
        {
            name = wanted;
            try
            {
                return LayoutManager.Current.CreateLayout(wanted);
            }
            catch (System.Exception ex)
            {
                Trace.Write("  лист «" + wanted + "» не создан (" + ex.Message + "), беру запасное имя");
            }

            for (int index = 1; ; index++)
            {
                string spare = "Лист " + index;
                if (used.Contains(spare)) continue;

                try
                {
                    name = spare;
                    return LayoutManager.Current.CreateLayout(spare);
                }
                catch (System.Exception)
                {
                    used.Add(spare);   // занято в самом чертеже — пробуем следующее
                }
            }
        }

    }
}
