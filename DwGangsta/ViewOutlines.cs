using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

namespace DwGangsta
{
    /// <summary>
    /// Обводит в модели то, что показывают видовые экраны листов, — по настоящей форме
    /// экрана, вместе с подрезкой `_VPCLIP`. Не переключаясь между листами, видно, что
    /// из модели попадает на чертёж, а что осталось за кадром.
    ///
    /// Контуры живут на своём слое: он заморожен во всех видовых экранах и не печатается,
    /// поэтому на самих листах их нет — разметка только для работы в модели.
    /// </summary>
    public static class ViewOutlines
    {
        /// <summary>Цвет слоя разметки — голубой, заметен и не спорит с чертежом.</summary>
        public const short LayerColor = 4;

        /// <summary>
        /// Контуры видовых экранов листа в координатах модели. Сам лист (первый ВЭ блока)
        /// окном в модель не является и контура не даёт.
        /// </summary>
        public static List<Point3dCollection> Collect(Transaction tr, Layout layout)
        {
            var outlines = new List<Point3dCollection>();
            var btr = (BlockTableRecord)tr.GetObject(layout.BlockTableRecordId, OpenMode.ForRead);

            var viewports = new List<Viewport>();
            foreach (ObjectId id in btr)
            {
                var vp = tr.GetObject(id, OpenMode.ForRead) as Viewport;
                if (vp != null) viewports.Add(vp);
            }

            int paperIndex = ViewRegion.PaperSpaceIndex(viewports);

            for (int i = 0; i < viewports.Count; i++)
            {
                if (i == paperIndex) continue;

                Point3dCollection outline = ViewRegion.OutlineInModel(viewports[i], tr);
                if (outline != null && outline.Count >= 3) outlines.Add(outline);
            }
            return outlines;
        }

        /// <summary>
        /// Обводит в модели всё, что показывают видовые экраны каждого листа чертежа, —
        /// чертёж при этом не разбирается и не меняется, появляется только разметка.
        /// Прежняя разметка стирается, поэтому вызывать можно сколько угодно: рамки
        /// не копятся, а пересчитываются заново — удобно после правки экранов.
        /// </summary>
        /// <param name="layoutCount">Сколько листов просмотрено.</param>
        /// <param name="color">Цвет рамок по ACI; 0 — по слою.</param>
        /// <returns>Сколько рамок нарисовано.</returns>
        public static int DrawAllLayouts(Database db, string layerName, out int layoutCount,
                                         short color = 0)
        {
            layoutCount = 0;
            var outlines = new List<Point3dCollection>();

            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                // По порядку вкладок: так и рамки в модели идут лист за листом.
                var layouts = new List<KeyValuePair<int, ObjectId>>();
                var dict = (DBDictionary)tr.GetObject(db.LayoutDictionaryId, OpenMode.ForRead);
                foreach (DBDictionaryEntry entry in dict)
                {
                    var layout = tr.GetObject(entry.Value, OpenMode.ForRead) as Layout;
                    if (layout == null || layout.ModelType) continue;
                    layouts.Add(new KeyValuePair<int, ObjectId>(layout.TabOrder, entry.Value));
                }
                layouts.Sort(delegate(KeyValuePair<int, ObjectId> a, KeyValuePair<int, ObjectId> b)
                {
                    return a.Key.CompareTo(b.Key);
                });

                foreach (KeyValuePair<int, ObjectId> pair in layouts)
                {
                    var layout = (Layout)tr.GetObject(pair.Value, OpenMode.ForRead);
                    outlines.AddRange(Collect(tr, layout));
                    layoutCount++;
                }
                tr.Commit();
            }

            Erase(db, layerName);
            int drawn = Draw(db, outlines, new Vector3d(0.0, 0.0, 0.0), layerName, color);
            HideInViewports(db, layerName);

            Trace.Write("обведено экранов: " + drawn + " с листов: " + layoutCount);
            return drawn;
        }

        /// <summary>Стирает прежнюю разметку слоя из модели. Возвращает, сколько стёрто.</summary>
        public static int Erase(Database db, string layerName)
        {
            if (string.IsNullOrEmpty(layerName)) return 0;

            int erased = 0;
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                var ms = (BlockTableRecord)tr.GetObject(
                    SymbolUtilityServices.GetBlockModelSpaceId(db), OpenMode.ForRead);

                foreach (ObjectId id in ms)
                {
                    var ent = tr.GetObject(id, OpenMode.ForRead) as Entity;
                    if (ent == null || ent.Layer != layerName) continue;

                    ent.UpgradeOpen();
                    ent.Erase();
                    erased++;
                }
                tr.Commit();
            }
            return erased;
        }

        /// <summary>Рисует собранные контуры в модели. Цвет 0 — по слою.</summary>
        public static int Draw(Database db, List<Point3dCollection> outlines, Vector3d offset,
                               string layerName, short color)
        {
            if (outlines == null || outlines.Count == 0) return 0;

            ObjectId layerId = EnsureLayer(db, layerName, LayerColor);
            int drawn = 0;

            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                var ms = (BlockTableRecord)tr.GetObject(
                    SymbolUtilityServices.GetBlockModelSpaceId(db), OpenMode.ForWrite);

                foreach (Point3dCollection outline in outlines)
                {
                    if (outline.Count < 3) continue;

                    var shape = new Polyline(outline.Count);
                    for (int i = 0; i < outline.Count; i++)
                    {
                        Point3d point = outline[i] + offset;
                        shape.AddVertexAt(i, new Point2d(point.X, point.Y), 0, 0, 0);
                    }
                    shape.Closed = true;
                    if (!layerId.IsNull) shape.LayerId = layerId;
                    if (color > 0) shape.Color = Color.FromColorIndex(ColorMethod.ByAci, color);

                    ms.AppendEntity(shape);
                    tr.AddNewlyCreatedDBObject(shape, true);
                    drawn++;
                }
                tr.Commit();
            }
            return drawn;
        }

        /// <summary>Слой разметки: создаётся при первой надобности и никогда не печатается.</summary>
        public static ObjectId EnsureLayer(Database db, string name, short colorIndex)
        {
            if (string.IsNullOrEmpty(name)) return ObjectId.Null;
            try
            {
                using (Transaction tr = db.TransactionManager.StartTransaction())
                {
                    var table = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
                    if (table.Has(name)) { ObjectId id = table[name]; tr.Commit(); return id; }

                    table.UpgradeOpen();
                    var layer = new LayerTableRecord
                    {
                        Name = name,
                        Color = Color.FromColorIndex(ColorMethod.ByAci, colorIndex),
                        IsPlottable = false                    // разметка, а не чертёж
                    };
                    ObjectId layerId = table.Add(layer);
                    tr.AddNewlyCreatedDBObject(layer, true);
                    tr.Commit();
                    return layerId;
                }
            }
            catch (System.Exception ex)
            {
                Trace.Write("слой «" + name + "» не создан: " + ex.Message);
                return ObjectId.Null;
            }
        }

        /// <summary>
        /// Прячет слой разметки из всех видовых экранов чертежа: он замораживается в каждом
        /// ВЭ и не выводится на печать. Разметка проходит впритык к чертежу, а значит попадает
        /// в кадр — без этого она вылезала бы на лист. В модели слой при этом виден как обычно.
        /// </summary>
        public static void HideInViewports(Database db, string layerName)
        {
            if (string.IsNullOrEmpty(layerName)) return;
            try
            {
                using (Transaction tr = db.TransactionManager.StartTransaction())
                {
                    var table = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
                    if (!table.Has(layerName)) { tr.Commit(); return; }

                    ObjectId layerId = table[layerName];
                    var layer = (LayerTableRecord)tr.GetObject(layerId, OpenMode.ForWrite);
                    layer.IsPlottable = false;
                    // Нарежут на листе ещё один ВЭ — разметка не появится и в нём.
                    layer.ViewportVisibilityDefault = true;

                    var ids = new ObjectId[] { layerId };
                    int frozen = 0;

                    var dict = (DBDictionary)tr.GetObject(db.LayoutDictionaryId, OpenMode.ForRead);
                    foreach (DBDictionaryEntry entry in dict)
                    {
                        var layout = tr.GetObject(entry.Value, OpenMode.ForRead) as Layout;
                        if (layout == null || layout.ModelType) continue;

                        var btr = (BlockTableRecord)tr.GetObject(layout.BlockTableRecordId, OpenMode.ForRead);
                        foreach (ObjectId id in btr)
                        {
                            var vp = tr.GetObject(id, OpenMode.ForRead) as Viewport;
                            if (vp == null) continue;

                            vp.UpgradeOpen();
                            vp.FreezeLayersInViewport(ids.GetEnumerator());
                            frozen++;
                        }
                    }
                    tr.Commit();
                    Trace.Write("слой «" + layerName + "» скрыт в видовых экранах: " + frozen);
                }
            }
            catch (System.Exception ex)
            {
                Trace.Write("слой «" + layerName + "» скрыть в видовых экранах не удалось: " + ex.Message);
            }
        }
    }
}
