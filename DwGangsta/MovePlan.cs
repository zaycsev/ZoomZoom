using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.DatabaseServices;

namespace DwGangsta
{
    /// <summary>
    /// Что изображено в видовых экранах листов — на один перенос. Состояние слоёв и
    /// заморозки в экранах читаются заново на каждый вызов: их меняют постоянно,
    /// а стоит это копейки (слоёв десятки, экранов — десятки).
    ///
    /// Различаются два вопроса. «Лежит в кадре» — по одной геометрии: объект попадает
    /// в кадр, даже если его слой сейчас заморожен. «Виден» — лежит в кадре и слой
    /// показан. Для целостности листа важно первое: в следующем месяце слой разморозят,
    /// и объект обязан оказаться на своём месте.
    /// </summary>
    public sealed class MoveScene : IDisposable
    {
        public sealed class View
        {
            public ObjectId Id;
            public string Layout;
            public int LayoutOrder;
            public ViewShape Shape;
            /// <summary>Слои, замороженные в этом экране, — например, месяцы, которых тут не видно.</summary>
            public HashSet<ObjectId> Frozen;
        }

        /// <summary>Объекта в кадре нет / лежит, но скрыт / виден.</summary>
        public enum Seen { No, Hidden, Visible }

        struct LayerState
        {
            public bool Off, Frozen, Locked;
            public string Name;
        }

        readonly Transaction _tr;
        readonly ModelIndex _index;
        readonly Dictionary<ObjectId, LayerState> _layers = new Dictionary<ObjectId, LayerState>();
        // Разметка DwGangsta (рамки подсветки и участков) — не часть чертежа: не тянет
        // за собой экраны и сама не переносится.
        readonly HashSet<ObjectId> _service = new HashSet<ObjectId>();

        public readonly List<View> Views = new List<View>();
        /// <summary>Экраны с 3D-видом или перспективой: что в них изображено, не считается.</summary>
        public int Skipped3d;

        /// <summary>Размер клетки сетки индекса — четверть типичного экрана.</summary>
        public double CellHint = 1;

        public ModelIndex Index => _index;

        MoveScene(Transaction tr, ModelIndex index)
        {
            _tr = tr;
            _index = index;
        }

        public static MoveScene Open(Transaction tr, Database db, ModelIndex index)
        {
            var scene = new MoveScene(tr, index);

            var table = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
            foreach (ObjectId id in table)
            {
                var layer = (LayerTableRecord)tr.GetObject(id, OpenMode.ForRead);
                scene._layers[id] = new LayerState
                {
                    Off = layer.IsOff, Frozen = layer.IsFrozen, Locked = layer.IsLocked, Name = layer.Name
                };
                if (layer.Name == MergeOptions.ViewLayerName || layer.Name == MergeOptions.ServiceLayerName)
                    scene._service.Add(id);
            }

            var dict = (DBDictionary)tr.GetObject(db.LayoutDictionaryId, OpenMode.ForRead);
            foreach (DBDictionaryEntry entry in dict)
            {
                var layout = tr.GetObject(entry.Value, OpenMode.ForRead) as Layout;
                if (layout == null || layout.ModelType) continue;

                var btr = (BlockTableRecord)tr.GetObject(layout.BlockTableRecordId, OpenMode.ForRead);
                var list = new List<Viewport>();
                foreach (ObjectId id in btr)
                {
                    var vp = tr.GetObject(id, OpenMode.ForRead) as Viewport;
                    if (vp != null) list.Add(vp);
                }

                int paper = ViewRegion.PaperSpaceIndex(list);
                for (int i = 0; i < list.Count; i++)
                {
                    if (i == paper) continue;
                    ViewShape shape = ViewShape.FromViewport(list[i], tr);
                    if (shape == null) { scene.Skipped3d++; continue; }

                    var frozen = new HashSet<ObjectId>();
                    ObjectIdCollection ids = list[i].GetFrozenLayers();
                    if (ids != null) foreach (ObjectId id in ids) frozen.Add(id);

                    scene.Views.Add(new View
                    {
                        Id = list[i].ObjectId, Layout = layout.LayoutName, LayoutOrder = layout.TabOrder,
                        Shape = shape, Frozen = frozen
                    });
                }
            }
            scene.Views.Sort((a, b) => a.LayoutOrder.CompareTo(b.LayoutOrder));

            var sizes = new List<double>();
            foreach (View v in scene.Views)
                sizes.Add(Math.Max(v.Shape.MaxX - v.Shape.MinX, v.Shape.MaxY - v.Shape.MinY));
            if (sizes.Count > 0)
            {
                sizes.Sort();
                scene.CellHint = sizes[sizes.Count / 2] / 4;
            }
            return scene;
        }

        /// <summary>Слой виден в экране: включён, не заморожен ни везде, ни в этом экране.</summary>
        bool Shows(ObjectId layer, View v)
        {
            LayerState st;
            if (!_anyGlobal && _layers.TryGetValue(layer, out st) && (st.Off || st.Frozen)) return false;
            return !v.Frozen.Contains(layer);
        }

        /// <summary>Вентиль — слой вставки по дороге к части блока: заморожен — части не видно, выключен — не мешает.</summary>
        bool Open(ObjectId[] gates, View v)
        {
            foreach (ObjectId g in gates)
            {
                LayerState st;
                if (!_anyGlobal && _layers.TryGetValue(g, out st) && st.Frozen) return false;
                if (v.Frozen.Contains(g)) return false;
            }
            return true;
        }

        /// <summary>Лежит ли объект в кадре экрана — и виден ли там. Геометрия считается один раз.</summary>
        public Seen Classify(int i, View v) => Classify(i, v, v.Shape);

        /// <summary>
        /// То же для кадра на другом месте модели (слои — те же, что у экрана): так видно,
        /// был ли объект в кадре до переноса, не запоминая всего заранее.
        /// </summary>
        public Seen Classify(int i, View v, ViewShape shape)
        {
            ref readonly ModelIndex.Item it = ref _index[i];
            if (it.Kind == ModelIndex.Kind.None) return Seen.No;
            if (!shape.Overlaps(it.MinX, it.MinY, it.MaxX, it.MaxY)) return Seen.No;

            if (it.Kind == ModelIndex.Kind.Block)
            {
                List<ModelIndex.Part> parts = _index.PartsOf(_tr, i);
                if (parts.Count > 0)
                {
                    bool inside = false;
                    foreach (ModelIndex.Part p in parts)
                    {
                        if (!p.Touches(shape)) continue;
                        inside = true;
                        if ((_anyGlobal || !it.Hidden) && Shows(p.Layer, v) && Open(p.Gates, v)) return Seen.Visible;
                    }
                    return inside ? Seen.Hidden : Seen.No;
                }
                // Содержимого не прочитать — судим по собственной рамке и слою вставки.
                if (!shape.TouchesBox(it.MinX, it.MinY, it.MaxX, it.MaxY)) return Seen.No;
                return (_anyGlobal || !it.Hidden) && Shows(it.Layer, v) ? Seen.Visible : Seen.Hidden;
            }

            bool hit;
            switch (it.Kind)
            {
                case ModelIndex.Kind.Segment:
                    hit = it.Rising
                        ? shape.SeesSegment(it.MinX, it.MinY, it.MaxX, it.MaxY)
                        : shape.SeesSegment(it.MinX, it.MaxY, it.MaxX, it.MinY);
                    break;
                case ModelIndex.Kind.Infinite:
                    hit = shape.SeesInfinite(_index.InfiniteOf(i));
                    break;
                case ModelIndex.Kind.Curve:
                    if (!shape.TouchesBox(it.MinX, it.MinY, it.MaxX, it.MaxY)) hit = false;
                    else if (shape.BoxInside(it.MinX, it.MinY, it.MaxX, it.MaxY)) hit = true;
                    else
                    {
                        var curve = _tr.GetObject(it.Id, OpenMode.ForRead) as Curve;
                        hit = curve == null || shape.SeesCurve(curve, it.MinX, it.MinY, it.MaxX, it.MaxY);
                    }
                    break;
                default:
                    hit = shape.TouchesBox(it.MinX, it.MinY, it.MaxX, it.MaxY);
                    // Поверхность Civil 3D — по самой поверхности, а не по её рамке: траншея
                    // в 230×247 м — узкая полоса, и рамкой она накрывает кадры, куда не заходит.
                    // Прочие объекты Civil — по линиям, из которых они нарисованы.
                    if (hit && it.Civil && !shape.BoxInside(it.MinX, it.MinY, it.MaxX, it.MaxY))
                        hit = SurfaceIn(i, shape);
                    // Таблица, маска, растр, текст — по своему контуру: картограмма повёрнута
                    // вместе с чертежом, и её рамка накрывала соседний кадр, а сама лежала мимо.
                    else if (hit && !shape.BoxInside(it.MinX, it.MinY, it.MaxX, it.MaxY) && Outlines.Has(it.Id))
                        hit = OutlineIn(i, shape);
                    // Штриховка — по контурам: повёрнутая полоса рамкой накрывает соседние кадры.
                    else if (hit && !shape.BoxInside(it.MinX, it.MinY, it.MaxX, it.MaxY) &&
                             it.Id.ObjectClass.IsDerivedFrom(HatchClass))
                        hit = HatchIn(i, shape);
                    break;
            }
            if (!hit) return Seen.No;
            return (_anyGlobal || !it.Hidden) && Shows(it.Layer, v) ? Seen.Visible : Seen.Hidden;
        }

        static readonly Autodesk.AutoCAD.Runtime.RXClass HatchClass =
            Autodesk.AutoCAD.Runtime.RXObject.GetClass(typeof(Hatch));

        /// <summary>
        /// Штриховка — по контурам: каждый контур — многоугольник (дуги и сплайны раскладываются
        /// на точки). Задевает кадр хоть один контур — штриховка в кадре.
        /// </summary>
        bool HatchIn(int i, ViewShape shape)
        {
            try
            {
                var hatch = (Hatch)_tr.GetObject(_index[i].Id, OpenMode.ForRead);
                if (Math.Abs(hatch.Normal.Z) < 0.9999) return true;      // не в плане — судим по рамке
                for (int k = 0; k < hatch.NumberOfLoops; k++)
                {
                    HatchLoop loop = hatch.GetLoopAt(k);
                    var poly = new List<Autodesk.AutoCAD.Geometry.Point2d>();
                    if (loop.IsPolyline)
                        foreach (BulgeVertex vertex in loop.Polyline) poly.Add(vertex.Vertex);
                    else
                        foreach (Autodesk.AutoCAD.Geometry.Curve2d curve in loop.Curves)
                        {
                            var line = curve as Autodesk.AutoCAD.Geometry.LineSegment2d;
                            if (line != null) { poly.Add(line.StartPoint); continue; }
                            foreach (Autodesk.AutoCAD.Geometry.Point2d p in curve.GetSamplePoints(12)) poly.Add(p);
                        }
                    if (poly.Count >= 3 && shape.TouchesPolygon(poly)) return true;
                }
                return false;
            }
            catch (System.Exception)
            {
                return true;      // контур не прочитать — судим по рамке, а она кадр задела
            }
        }

        bool OutlineIn(int i, ViewShape shape)
        {
            Autodesk.AutoCAD.Geometry.Point2d[] poly = Outlines.Of((Entity)_tr.GetObject(_index[i].Id, OpenMode.ForRead));
            return poly == null || shape.TouchesPolygon(poly);      // не прочитать — по рамке, а она кадр задела
        }

        // Режим «как если бы глобально погашенных слоёв не было»: виден ли объект хоть где-то.
        bool _anyGlobal;
        readonly Dictionary<int, bool> _shownSomewhere = new Dictionary<int, bool>();

        /// <summary>
        /// Показан ли объект хоть в одном экране на каком-нибудь листе — если не считать
        /// слоёв, погашенных во всём чертеже (их гасят на время, на листах они есть).
        /// Не показан нигде — значит, это не часть листов: подложка из PDF для сверки,
        /// служебные пометки. Такое «Всё в видовом» не переносит и цепочку через него не тянет.
        /// </summary>
        public bool ShownSomewhere(int i)
        {
            bool shown;
            if (_shownSomewhere.TryGetValue(i, out shown)) return shown;
            _anyGlobal = true;
            try
            {
                shown = false;
                foreach (View v in Views)
                    if (Classify(i, v) == Seen.Visible) { shown = true; break; }
            }
            finally { _anyGlobal = false; }
            _shownSomewhere[i] = shown;
            return shown;
        }

        // Ответы «поверхность в кадре» — на кадр: одна и та же пара спрашивается много раз.
        readonly Dictionary<ViewShape, Dictionary<int, bool>> _surfaceIn = new Dictionary<ViewShape, Dictionary<int, bool>>();
        const int SurfaceSamples = 32;

        /// <summary>
        /// Заходит ли поверхность Civil 3D в кадр: в узлах сетки 32×32 на общей части её
        /// рамки и кадра у поверхности спрашивается отметка — вне поверхности Civil
        /// отвечает отказом. Не поверхность (нет такого вопроса) — судим по рамке.
        /// </summary>
        bool SurfaceIn(int i, ViewShape shape)
        {
            Dictionary<int, bool> known;
            if (!_surfaceIn.TryGetValue(shape, out known)) _surfaceIn[shape] = known = new Dictionary<int, bool>();
            bool answer;
            if (known.TryGetValue(i, out answer)) return answer;

            answer = true;
            DBObject o = _tr.GetObject(_index[i].Id, OpenMode.ForRead);
            System.Reflection.MethodInfo elevation = o.GetType().GetMethod("FindElevationAtXY", new[] { typeof(double), typeof(double) });
            if (elevation != null)
            {
                ref readonly ModelIndex.Item it = ref _index[i];
                double x0 = Math.Max(it.MinX, shape.MinX), x1 = Math.Min(it.MaxX, shape.MaxX);
                double y0 = Math.Max(it.MinY, shape.MinY), y1 = Math.Min(it.MaxY, shape.MaxY);
                answer = false;
                var args = new object[2];
                for (int a = 0; a <= SurfaceSamples && !answer; a++)
                    for (int b = 0; b <= SurfaceSamples && !answer; b++)
                    {
                        double x = x0 + (x1 - x0) * a / SurfaceSamples, y = y0 + (y1 - y0) * b / SurfaceSamples;
                        if (!shape.ContainsPoint(x, y)) continue;
                        args[0] = x; args[1] = y;
                        try { elevation.Invoke(o, args); answer = true; }
                        catch (System.Reflection.TargetInvocationException) { /* точка вне поверхности */ }
                    }
            }
            // Не поверхность — характерная линия, профилирование, участок: по тому, из чего
            // объект нарисован. Повёрнутая характерная линия рамкой задевает соседний кадр,
            // а сама проходит мимо.
            else if (o is Entity e) answer = ExplodedIn(e, shape, 0);
            known[i] = answer;
            return answer;
        }

        /// <summary>
        /// Заходит ли в кадр хоть один примитив, на который раскладывается объект (в памяти —
        /// чертёж не меняется). Не раскладывается — судим по рамке, а она кадр задела.
        /// </summary>
        static bool ExplodedIn(Entity e, ViewShape shape, int depth)
        {
            var pieces = new DBObjectCollection();
            try { e.Explode(pieces); }
            catch (System.Exception) { return true; }
            try
            {
                foreach (DBObject piece in pieces)
                {
                    var pe = piece as Entity;
                    if (pe == null || !pe.Visible) continue;
                    if (pe is BlockReference && depth < 3)
                    {
                        if (ExplodedIn(pe, shape, depth + 1)) return true;
                        continue;
                    }
                    Extents3d ext;
                    if (!EntityBounds.TryGet(pe, out ext)) continue;
                    if (shape.Sees(pe, ext)) return true;
                }
                return false;
            }
            finally
            {
                foreach (DBObject piece in pieces) piece.Dispose();
            }
        }

        /// <summary>Виден ли объект индекса i в экране v — по слоям и по самой геометрии.</summary>
        public bool Visible(int i, View v) => Classify(i, v) == Seen.Visible;

        /// <summary>Лежит ли объект в кадре — видимый или скрытый слоями.</summary>
        public bool Present(int i, View v) => Classify(i, v) != Seen.No;

        /// <summary>Объект на заблокированном слое — его не переместить.</summary>
        public bool Locked(int i)
        {
            LayerState st;
            return _layers.TryGetValue(_index[i].Layer, out st) && st.Locked;
        }

        /// <summary>Разметка DwGangsta: не часть чертежа.</summary>
        public bool Service(int i) => _service.Contains(_index[i].Layer);

        public string LayerName(int i)
        {
            LayerState st;
            return _layers.TryGetValue(_index[i].Layer, out st) ? st.Name : "?";
        }

        public void Dispose()
        {
            foreach (View v in Views) v.Shape.Dispose();
        }
    }

    /// <summary>
    /// Разбор переноса до того, как он сделан: какие экраны покажут выбранное, что ещё
    /// изображено в них рядом с ним и во что выльется «переместить целиком».
    /// </summary>
    public sealed class MovePlan
    {
        /// <summary>Невыбранное в кадре экрана: видимое и скрытое слоями.</summary>
        public sealed class Others
        {
            public readonly List<int> Visible = new List<int>();
            public readonly List<int> Hidden = new List<int>();
            public int Count => Visible.Count + Hidden.Count;
        }

        /// <summary>Экраны, где выбранное видно: «Выбранное» сдвигает именно их.</summary>
        public readonly List<MoveScene.View> Shown = new List<MoveScene.View>();

        /// <summary>Экраны, где выбранное лежит в кадре — видимое или скрытое.</summary>
        public readonly List<MoveScene.View> Holding = new List<MoveScene.View>();

        /// <summary>Из них — экраны, где в кадре есть и невыбранное, с этим невыбранным.</summary>
        public readonly Dictionary<MoveScene.View, Others> Around = new Dictionary<MoveScene.View, Others>();

        /// <summary>«Всё в видовом»: что ещё переместится — по цепочке, пока экраны не замкнутся.</summary>
        public readonly List<int> Extra = new List<int>();

        /// <summary>«Всё в видовом»: из добавленного — не видное ни в одном из сдвигаемых экранов.</summary>
        public int ExtraHidden;

        /// <summary>«Всё в видовом»: невыбранное на заблокированных слоях — его не переместить.</summary>
        public readonly List<int> Stuck = new List<int>();

        /// <summary>«Всё в видовом»: все экраны, которые сдвинутся.</summary>
        public readonly List<MoveScene.View> Whole = new List<MoveScene.View>();

        /// <summary>«Всё в видовом»: экран цепочки — и объект, из-за которого он в неё попал.</summary>
        public readonly Dictionary<MoveScene.View, int> PulledBy = new Dictionary<MoveScene.View, int>();

        /// <summary>«Всё в видовом»: что из добавленного лежит в кадрах каждого экрана — для отчёта по листам.</summary>
        public readonly Dictionary<MoveScene.View, List<int>> ExtraIn = new Dictionary<MoveScene.View, List<int>>();

        public bool Clean => Around.Count == 0;

        /// <summary>Сколько занял каждый этап — для замеров.</summary>
        public string Phases = "";

        /// <summary>null — прервано по Esc.</summary>
        public static MovePlan Build(MoveScene scene, IList<int> selected, Func<bool> cancelled)
        {
            var plan = new MovePlan();
            int n = scene.Index.Count;
            var moving = new bool[n];
            foreach (int i in selected) moving[i] = true;

            foreach (MoveScene.View v in scene.Views)
            {
                bool visible = false;
                foreach (int i in selected)
                    if (scene.Classify(i, v) == MoveScene.Seen.Visible) { visible = true; break; }
                // Экран, где выбранное лишь лежит под замороженным слоем (сентябрьский блок
                // в июльском экране), листа не меняет: ни он, ни его содержимое не едут.
                if (visible) { plan.Holding.Add(v); plan.Shown.Add(v); }
            }

            var watch = System.Diagnostics.Stopwatch.StartNew();
            foreach (MoveScene.View v in plan.Holding)
            {
                Others others = Scan(scene, v, moving, cancelled);
                if (others == null) return null;
                if (others.Count > 0) plan.Around[v] = others;
            }

            plan.Whole.AddRange(plan.Holding);
            plan.Phases = "экраны выбранного и соседи " + watch.ElapsedMilliseconds;
            watch.Restart();
            if (plan.Clean) return plan;

            // Цепочка для «Всё в видовом»: всё, что лежит в кадрах задетых экранов, едет тоже,
            // а оно само лежит в кадрах других экранов — те тоже сдвинутся, и в них своё.
            var taken = (bool[])moving.Clone();
            var stuck = new bool[n];
            var seenViews = new HashSet<MoveScene.View>(plan.Holding);
            var queue = new Queue<MoveScene.View>(plan.Holding);

            while (queue.Count > 0)
            {
                MoveScene.View v = queue.Dequeue();
                Others others;
                if (!plan.Around.TryGetValue(v, out others))
                {
                    others = Scan(scene, v, taken, cancelled);
                    if (others == null) return null;
                }

                var added = new List<int>();
                var here = new List<int>();
                foreach (List<int> list in new[] { others.Visible, others.Hidden })
                    foreach (int i in list)
                    {
                        if (moving[i]) continue;
                        here.Add(i);
                        if (taken[i]) continue;
                        if (scene.Locked(i))
                        {
                            if (!stuck[i]) { stuck[i] = true; plan.Stuck.Add(i); }
                            continue;
                        }
                        taken[i] = true;
                        plan.Extra.Add(i);
                        added.Add(i);
                    }
                plan.ExtraIn[v] = here;

                foreach (MoveScene.View other in scene.Views)
                {
                    if (seenViews.Contains(other)) continue;
                    foreach (int i in added)
                    {
                        // Тянет только то, что в соседнем экране видно: скрытое там слоем
                        // уедет из его кадра, а лист останется прежним.
                        if (!scene.Visible(i, other)) continue;
                        seenViews.Add(other);
                        plan.PulledBy[other] = i;
                        queue.Enqueue(other);
                        plan.Whole.Add(other);
                        break;
                    }
                }
            }

            plan.Phases += ", цепочка " + watch.ElapsedMilliseconds;
            watch.Restart();
            foreach (int i in plan.Extra)
            {
                bool visible = false;
                foreach (MoveScene.View v in plan.Whole)
                    if (scene.Visible(i, v)) { visible = true; break; }
                if (!visible) plan.ExtraHidden++;
            }
            plan.Phases += ", скрытые " + watch.ElapsedMilliseconds;
            return plan;
        }

        /// <summary>Всё, что видно в экране. null — прервано по Esc.</summary>
        public static List<int> VisibleIn(MoveScene scene, MoveScene.View v, Func<bool> cancelled)
        {
            Others all = Scan(scene, v, new bool[scene.Index.Count], cancelled, false);
            return all == null ? null : all.Visible;
        }

        /// <summary>
        /// Всё неотмеченное в кадре экрана, кроме разметки DwGangsta. Перебираются только
        /// объекты из клеток сетки под экраном; точная геометрия — только у задевших кадр.
        /// </summary>
        static Others Scan(MoveScene scene, MoveScene.View v, bool[] marked, Func<bool> cancelled,
                           bool withHidden = true)
        {
            var found = new Others();
            ModelIndex index = scene.Index;
            ViewShape shape = v.Shape;
            List<int> candidates = index.Candidates(shape.MinX, shape.MinY, shape.MaxX, shape.MaxY, scene.CellHint);
            for (int k = 0; k < candidates.Count; k++)
            {
                if ((k & 16383) == 0 && cancelled != null && cancelled()) return null;
                int i = candidates[k];
                if (marked[i]) continue;
                ref readonly ModelIndex.Item it = ref index[i];
                if (!shape.Overlaps(it.MinX, it.MinY, it.MaxX, it.MaxY)) continue;
                if (scene.Service(i)) continue;
                MoveScene.Seen seen = scene.Classify(i, v);
                if (seen == MoveScene.Seen.Visible) found.Visible.Add(i);
                // Скрытое слоями в кадре — тоже часть экрана: пометки на слоях, замороженных
                // во всех экранах, едут вместе с ним. Подложка вне кадров сюда не попадает:
                // блоки проверяются по своим линиям, а не по рамке клетки.
                else if (seen == MoveScene.Seen.Hidden && withHidden) found.Hidden.Add(i);
            }
            return found;
        }
    }
}
