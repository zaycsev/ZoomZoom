using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

namespace ZoomZoom
{
    /// <summary>
    /// Видовые экраны на листах: после сжатия модели они должны показывать
    /// ровно то же, что и до него.
    ///
    /// Экран — это окно в модель: точка взгляда (ViewTarget, мировые
    /// координаты), центр вида относительно неё (ViewCenter, координаты вида)
    /// и высота видимой части модели (ViewHeight). Масштаб на листе — это
    /// высота экрана на бумаге, делённая на ViewHeight. Модель сжата матрицей
    /// масштабирования вокруг начала координат, поэтому для той же картинки
    /// достаточно прогнать через ту же матрицу точку взгляда, а центр, высоту
    /// и секущие плоскости умножить на коэффициент. Масштаб экрана при этом
    /// сам вырастет во столько же раз: 1:100 станет 10:1.
    ///
    /// Сам экран — рамка, подрезка _VPCLIP любой формы, замороженные в нём
    /// слои — живёт на листе и не трогается: лист не масштабируется.
    ///
    /// При работе с выбором рамкой экран меняется, только если в нём видно
    /// выбранное и не видно ничего другого. Экран, где видно и то и другое,
    /// сохранить нельзя — половина его содержимого сжалась, половина нет, —
    /// он остаётся как был и попадает в отчёт.
    /// </summary>
    internal sealed class ViewportRescale
    {
        public int Total, Scaled, Locked, Clipped, Untouched;
        public readonly List<string> Mixed = new List<string>();
        public readonly List<string> Failed = new List<string>();

        /// <summary>Листы с PSLTSCALE=1, где поменялся хоть один экран.</summary>
        public readonly List<string> PaperLinetypeLayouts = new List<string>();

        /// <summary>Листы с PSLTSCALE=0 (или непрочитанным), где поменялся хоть один экран:
        /// там штрих в экране — в единицах модели, его держит LTSCALE.</summary>
        public readonly List<string> ModelLinetypeLayouts = new List<string>();

        private readonly bool _wholeSpace;
        private readonly List<Box> _selected = new List<Box>();
        private readonly List<Box> _others = new List<Box>();

        /// <summary>Габариты объекта в мировых координатах и его слой —
        /// снимается ДО масштабирования, пока выбранное ещё на своём месте.</summary>
        private struct Box
        {
            public Point3d Min, Max;
            public ObjectId Layer;
            public ObjectId Id;
        }

        private ViewportRescale(bool wholeSpace)
        {
            _wholeSpace = wholeSpace;
        }

        /// <summary>
        /// Снимок до масштабирования. Для «всего чертежа» снимать нечего: все
        /// экраны смотрят в модель, а она сжимается целиком.
        /// </summary>
        public static ViewportRescale Prepare(Database db, ObjectId[] ids, bool wholeSpace)
        {
            var vr = new ViewportRescale(wholeSpace);
            if (wholeSpace)
                return vr;

            var chosen = new HashSet<ObjectId>(ids);
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                var ms = (BlockTableRecord)tr.GetObject(
                    SymbolUtilityServices.GetBlockModelSpaceId(db), OpenMode.ForRead);
                foreach (ObjectId id in ms)
                {
                    var ent = tr.GetObject(id, OpenMode.ForRead, false, true) as Entity;
                    Box box;
                    if (ent == null || !TryBox(ent, out box))
                        continue;
                    (chosen.Contains(id) ? vr._selected : vr._others).Add(box);
                }
                vr.PreClassify(tr, db);
                tr.Commit();
            }
            return vr;
        }

        // Решение по экранам, принятое до масштабирования по настоящему кадру
        // экрана (ViewShape: контур _VPCLIP, поворот вида). Экраны, которых тут
        // нет (3D, перспектива, сборка без DwGangsta), Apply разбирает по-старому.
        private readonly Dictionary<ObjectId, Visibility> _pre = new Dictionary<ObjectId, Visibility>();

        /// <summary>Видовые экраны листов, кроме экранов самих листов, и имя листа.</summary>
        private static List<KeyValuePair<Viewport, string>> LayoutViews(Transaction tr, Database db)
        {
            var views = new List<KeyValuePair<Viewport, string>>();
            var layouts = (DBDictionary)tr.GetObject(db.LayoutDictionaryId, OpenMode.ForRead);
            foreach (DBDictionaryEntry entry in layouts)
            {
                var layout = (Layout)tr.GetObject(entry.Value, OpenMode.ForRead);
                if (layout.ModelType)
                    continue;
                var btr = (BlockTableRecord)tr.GetObject(layout.BlockTableRecordId, OpenMode.ForRead);
                bool first = true;
                foreach (ObjectId id in btr)
                {
                    if (id.ObjectClass.Name != "AcDbViewport")
                        continue;
                    var vp = (Viewport)tr.GetObject(id, OpenMode.ForRead);
                    bool isPaper = IsNumberOne(vp) || (first && LooksLikePaper(vp));
                    first = false;
                    if (!isPaper)
                        views.Add(new KeyValuePair<Viewport, string>(vp, layout.LayoutName));
                }
            }
            return views;
        }

        /// <summary>
        /// Кадр экрана в модели — для проверки «виден ли объект». Для вида
        /// сверху — настоящий контур (DwGangsta.ViewShape: прямоугольник или
        /// подрезка _VPCLIP, с поворотом вида); иначе (3D-вид, перспектива) —
        /// прямоугольник вида и проверка кривых по нему.
        /// </summary>
        private sealed class Frame : IDisposable
        {
#if GANGSTA
            private readonly DwGangsta.ViewShape _shape;
#endif
            private readonly Matrix3d _toView;
            private readonly double _x0, _x1, _y0, _y1;
            private readonly Window _window;

            public Frame(Transaction tr, Viewport vp)
            {
#if GANGSTA
                try { _shape = DwGangsta.ViewShape.FromViewport(vp, tr); }
                catch (System.Exception) { _shape = null; }
#endif
                _toView = ViewToWorld(vp).Inverse();
                double h = vp.ViewHeight;
                double w = vp.Height > 0 ? h * vp.Width / vp.Height : h;
                Point2d c = vp.ViewCenter;
                _x0 = c.X - w / 2; _x1 = c.X + w / 2; _y0 = c.Y - h / 2; _y1 = c.Y + h / 2;
                _window = new Window(vp, _x0, _x1, _y0, _y1);
            }

            /// <summary>Настоящий кадр (с подрезкой) построен.</summary>
            public bool Exact
            {
#if GANGSTA
                get { return _shape != null; }
#else
                get { return false; }
#endif
            }

            public bool Sees(Transaction tr, Box b)
            {
#if GANGSTA
                if (_shape != null)
                {
                    if (!_shape.Overlaps(b.Min.X, b.Min.Y, b.Max.X, b.Max.Y))
                        return false;
                    Entity ent;
                    try { ent = tr.GetObject(b.Id, OpenMode.ForRead, false, true) as Entity; }
                    catch (System.Exception) { return true; }
                    return ent != null && _shape.Sees(ent, new Extents3d(b.Min, b.Max));
                }
#endif
                return Hits(b, _toView, _x0, _x1, _y0, _y1) && _window.Touches(tr, b.Id, Matrix3d.Identity);
            }

            public void Dispose()
            {
#if GANGSTA
                if (_shape != null) _shape.Dispose();
#endif
            }
        }

        /// <summary>
        /// Рамка: какие экраны показывают выбранное — до масштабирования, по
        /// исходной геометрии и настоящему кадру экрана. Только вид сверху с
        /// построенным кадром; остальные Apply разберёт по-старому.
        /// </summary>
        private void PreClassify(Transaction tr, Database db)
        {
            HashSet<ObjectId> hidden = HiddenLayers(tr, db);
            foreach (var pair in LayoutViews(tr, db))
            {
                Viewport vp = pair.Key;
                using (var frame = new Frame(tr, vp))
                {
                    if (!frame.Exact)
                        continue;
                    var frozenHere = FrozenIn(vp);
                    bool any = false;
                    foreach (Box b in _selected)
                        if (Visible(b, hidden, frozenHere) && frame.Sees(tr, b)) { any = true; break; }
                    if (!any) { _pre[vp.ObjectId] = Visibility.None; continue; }
                    Visibility v = Visibility.OnlySelected;
                    foreach (Box b in _others)
                        if (Visible(b, hidden, frozenHere) && frame.Sees(tr, b)) { v = Visibility.Mixed; break; }
                    _pre[vp.ObjectId] = v;
                }
            }
        }

        private static HashSet<ObjectId> FrozenIn(Viewport vp)
        {
            var set = new HashSet<ObjectId>();
            try { foreach (ObjectId id in vp.GetFrozenLayers()) set.Add(id); }
            catch (System.Exception) { }
            return set;
        }

        private static bool TryBox(Entity ent, out Box box)
        {
            box = new Box();
            // Бесконечные линии видны в любом экране и сделали бы «смешанным»
            // каждый — это вспомогательные построения, не содержимое.
            if (ent is Xline || ent is Ray)
                return false;
            try
            {
                Extents3d ext = ent.GeometricExtents;
                box.Min = ext.MinPoint;
                box.Max = ext.MaxPoint;
                box.Layer = ent.LayerId;
                box.Id = ent.ObjectId;
                return true;
            }
            catch (System.Exception)
            {
                return false;          // пустой объект, габаритов нет
            }
        }

        /// <summary>Пересчёт экранов на всех листах.</summary>
        public void Apply(Database db, Matrix3d mat, double factor, AnnoRescale anno = null)
        {
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                HashSet<ObjectId> hiddenLayers = _wholeSpace ? null : HiddenLayers(tr, db);

                var layouts = (DBDictionary)tr.GetObject(db.LayoutDictionaryId, OpenMode.ForRead);
                foreach (DBDictionaryEntry entry in layouts)
                {
                    var layout = (Layout)tr.GetObject(entry.Value, OpenMode.ForRead);
                    if (layout.ModelType)
                        continue;

                    int changedHere = 0;
                    var btr = (BlockTableRecord)tr.GetObject(layout.BlockTableRecordId, OpenMode.ForRead);

                    bool first = true;
                    foreach (ObjectId id in btr)
                    {
                        if (id.ObjectClass.Name != "AcDbViewport")
                            continue;

                        var vp = (Viewport)tr.GetObject(id, OpenMode.ForRead);

                        // Экран самого листа (пространство бумаги) в модель не
                        // смотрит, его трогать нельзя. По соглашению он первый
                        // в листе — но только если лист хоть раз открывали: у
                        // неинициализированного листа (и у листов из выгрузок
                        // BIM) экрана бумаги нет, и первым оказывается
                        // настоящий. Поэтому первый пропускаем, только если он
                        // и выглядит как лист: масштаб 1:1, вид по центру рамки.
                        // Номер 1 AutoCAD даёт именно экрану листа — по нему
                        // узнаём и лист, вид которого сдвигали и приближали
                        // (масштаб уже не 1:1, центр вида не в центре рамки).
                        bool isPaper = IsNumberOne(vp) || (first && LooksLikePaper(vp));
                        first = false;
                        if (isPaper)
                            continue;

                        Total++;

                        if (!_wholeSpace)
                        {
                            Visibility vis;
                            if (!_pre.TryGetValue(vp.ObjectId, out vis))
                                vis = Classify(tr, vp, hiddenLayers, mat);
                            if (vis == Visibility.None) { Untouched++; continue; }
                            if (vis == Visibility.Mixed)
                            {
                                Mixed.Add(string.Format("лист «{0}», экран {1}", layout.LayoutName, vp.Handle));
                                continue;
                            }
                        }

                        try
                        {
                            Rescale(vp, mat, factor);
                            if (anno != null)
                                anno.ApplyToViewport(vp);
                            Scaled++;
                            changedHere++;
                        }
                        catch (System.Exception ex)
                        {
                            Failed.Add(string.Format("лист «{0}», экран {1}: {2}", layout.LayoutName, vp.Handle, ex.Message));
                        }
                    }

                    if (changedHere > 0)
                        (LayoutPsltscale(entry.Value) == true ? PaperLinetypeLayouts : ModelLinetypeLayouts)
                            .Add(layout.LayoutName);
                }
                tr.Commit();
            }
        }

        /// <summary>
        /// Номер экрана 1 — экран самого листа. Viewport.Number у листа,
        /// который сейчас не открыт (Зум-зум работает в модели), не читается,
        /// поэтому берём записанный номер — DXF-код 69.
        /// </summary>
        private static bool IsNumberOne(Viewport vp)
        {
            try
            {
                if (vp.Number == 1)
                    return true;
                long[] ename = new long[2];
                if (!GetAdsName(ename, vp.ObjectId))
                    return false;
                IntPtr rb = acdbEntGet(ename);
                if (rb == IntPtr.Zero)
                    return false;
                using (ResultBuffer buf = ResultBuffer.Create(rb, true))
                    foreach (TypedValue tv in buf)
                        if (tv.TypeCode == 69)
                            return Convert.ToInt32(tv.Value) == 1;
            }
            catch (System.Exception) { }
            return false;
        }

        private static bool LooksLikePaper(Viewport vp)
        {
            try
            {
                double tol = 1e-6 * Math.Max(1.0, vp.Height);
                return Math.Abs(vp.CustomScale - 1.0) < 1e-9 &&
                       Math.Abs(vp.ViewCenter.X - vp.CenterPoint.X) < tol &&
                       Math.Abs(vp.ViewCenter.Y - vp.CenterPoint.Y) < tol;
            }
            catch (System.Exception)
            {
                return true;      // не прочитался — безопаснее не трогать
            }
        }

        private void Rescale(Viewport vp, Matrix3d mat, double factor)
        {
            Point3d target = vp.ViewTarget;
            Point2d center = vp.ViewCenter;
            double height = vp.ViewHeight;

            vp.UpgradeOpen();

            // Блокировка экрана запрещает менять вид — снимаем на время.
            bool locked = vp.Locked;
            if (locked) { vp.Locked = false; Locked++; }
            if (vp.NonRectClipOn) Clipped++;

            vp.ViewTarget = target.TransformBy(mat);
            vp.ViewCenter = new Point2d(center.X * factor, center.Y * factor);
            vp.ViewHeight = height * factor;

            // Секущие плоскости отсчитываются от точки взгляда вдоль
            // направления — это длины в модели.
            vp.FrontClipDistance = vp.FrontClipDistance * factor;
            vp.BackClipDistance = vp.BackClipDistance * factor;

            // В перспективе длина вектора направления — расстояние до камеры.
            // В параллельной проекции на вид она не влияет, но экранная графика
            // AutoCAD ставит по ней камеру: у выгрузок Revit она 2.4 млн, и после
            // одного-двух Зум-зумов камера оказывалась в 10^7..10^10 раз дальше
            // самого чертежа — видеокарта теряла точность по глубине, и в
            // экране листа пропадала часть объектов (печать при этом верна).
            // Поэтому в параллельной проекции длину приводим к 1, как у плана.
            if (vp.PerspectiveOn)
                vp.ViewDirection = vp.ViewDirection * factor;
            else
            {
                Vector3d dir = vp.ViewDirection;
                double len = dir.Length;
                if (len > 0 && Math.Abs(len - 1.0) > 1e-9)
                {
                    double twist = vp.TwistAngle;
                    vp.ViewDirection = dir / len;
                    vp.TwistAngle = twist;
                }
            }

            // ПСК экрана: начало — точка модели, оси — направления.
            // В API 2015–2018 GetUcs объявлен с параметрами по значению и ничего
            // не возвращает — ПСК там не прочитать, остаётся как была. На вид
            // листа это не влияет: двигается только значок ПСК в экране.
#if !NET45
            try
            {
                Point3d origin = Point3d.Origin;
                Vector3d xAxis = Vector3d.XAxis, yAxis = Vector3d.YAxis;
                vp.GetUcs(ref origin, ref xAxis, ref yAxis);
                Point3d moved = origin.TransformBy(mat);
                if (!moved.IsEqualTo(origin))
                    vp.SetUcs(moved, xAxis, yAxis);
            }
            catch (System.Exception) { }
#endif
            try { vp.Elevation = vp.Elevation * factor; }
            catch (System.Exception) { }

            if (locked) vp.Locked = true;
        }

        // ------------------------------------------------- что видно в экране

        private enum Visibility { None, OnlySelected, Mixed }

        /// <summary>
        /// Что видно в экране: ничего из выбранного, только выбранное или
        /// вперемешку. Учитываются только объекты, которые в этом экране
        /// реально видны: слой не выключен, не заморожен ни везде, ни в самом
        /// экране.
        /// </summary>
        private Visibility Classify(Transaction tr, Viewport vp, HashSet<ObjectId> hiddenLayers, Matrix3d mat)
        {
            var frozenHere = new HashSet<ObjectId>();
            try
            {
                foreach (ObjectId id in vp.GetFrozenLayers())
                    frozenHere.Add(id);
            }
            catch (System.Exception) { }

            Matrix3d toView = ViewToWorld(vp).Inverse();
            double h = vp.ViewHeight;
            double w = vp.Height > 0 ? h * vp.Width / vp.Height : h;
            Point2d c = vp.ViewCenter;
            double x0 = c.X - w / 2, x1 = c.X + w / 2, y0 = c.Y - h / 2, y1 = c.Y + h / 2;

            var window = new Window(vp, x0, x1, y0, y1);

            bool any = false;
            foreach (Box b in _selected)
                if (Visible(b, hiddenLayers, frozenHere) && Hits(b, toView, x0, x1, y0, y1) &&
                    window.Touches(tr, b.Id, mat))
                { any = true; break; }
            if (!any)
                return Visibility.None;

            foreach (Box b in _others)
                if (Visible(b, hiddenLayers, frozenHere) && Hits(b, toView, x0, x1, y0, y1) &&
                    window.Touches(tr, b.Id, Matrix3d.Identity))
                    return Visibility.Mixed;

            return Visibility.OnlySelected;
        }

        /// <summary>
        /// Окно экрана для точной проверки кривых.
        ///
        /// Габарит — проверка грубая: рамка-полилиния вокруг всего чертежа
        /// своим габаритом накрывает любой экран, хотя ни одной её линии в нём
        /// не видно. Для отрезков, дуг и полилиний в видах сверху смотрим саму
        /// кривую: лежит ли её точка внутри окна или пересекает ли она границу
        /// окна (в проекции на план). Остальное — блоки, штриховки, текст, виды
        /// не сверху — по-прежнему по габариту: лучше лишний раз не тронуть
        /// экран и сказать об этом, чем испортить его.
        ///
        /// moved — матрица, которой объект уже сдвинут (у выбранного — сжатие,
        /// у остального — единичная): окно прогоняется через неё же.
        /// </summary>
        private sealed class Window
        {
            private readonly bool _plan;
            private readonly Matrix3d _viewToWorld;
            private readonly double _x0, _x1, _y0, _y1;

            public Window(Viewport vp, double x0, double x1, double y0, double y1)
            {
                Vector3d d = vp.ViewDirection;
                _plan = Math.Abs(d.X) < 1e-12 * d.Length && Math.Abs(d.Y) < 1e-12 * d.Length && d.Z > 0;
                _viewToWorld = ViewToWorld(vp);
                _x0 = x0; _x1 = x1; _y0 = y0; _y1 = y1;
            }

            public bool Touches(Transaction tr, ObjectId id, Matrix3d moved)
            {
                if (!_plan)
                    return true;
                Curve curve;
                try { curve = tr.GetObject(id, OpenMode.ForRead, false, true) as Curve; }
                catch (System.Exception) { return true; }
                if (curve == null)
                    return true;

                try
                {
                    Matrix3d toWorld = moved * _viewToWorld;
                    Matrix3d toView = toWorld.Inverse();

                    Point3d s = curve.StartPoint.TransformBy(toView);
                    if (s.X >= _x0 && s.X <= _x1 && s.Y >= _y0 && s.Y <= _y1)
                        return true;

                    using (var rect = new Polyline())
                    {
                        int i = 0;
                        foreach (Point2d v in new[] { new Point2d(_x0, _y0), new Point2d(_x1, _y0),
                                                      new Point2d(_x1, _y1), new Point2d(_x0, _y1) })
                        {
                            Point3d wv = new Point3d(v.X, v.Y, 0).TransformBy(toWorld);
                            rect.AddVertexAt(i++, new Point2d(wv.X, wv.Y), 0, 0, 0);
                        }
                        rect.Closed = true;

                        var pts = new Point3dCollection();
                        curve.IntersectWith(rect, Intersect.OnBothOperands,
                            new Plane(Point3d.Origin, Vector3d.ZAxis), pts, IntPtr.Zero, IntPtr.Zero);
                        return pts.Count > 0;
                    }
                }
                catch (System.Exception)
                {
                    return true;
                }
            }
        }

        private static bool Visible(Box b, HashSet<ObjectId> hidden, HashSet<ObjectId> frozenHere)
        {
            return !hidden.Contains(b.Layer) && !frozenHere.Contains(b.Layer);
        }

        /// <summary>Пересекается ли габарит объекта с окном экрана — в
        /// координатах вида, то есть с учётом поворота и направления взгляда.</summary>
        private static bool Hits(Box b, Matrix3d toView, double x0, double x1, double y0, double y1)
        {
            double minX = double.MaxValue, minY = double.MaxValue;
            double maxX = double.MinValue, maxY = double.MinValue;
            for (int i = 0; i < 8; i++)
            {
                var p = new Point3d(
                    (i & 1) == 0 ? b.Min.X : b.Max.X,
                    (i & 2) == 0 ? b.Min.Y : b.Max.Y,
                    (i & 4) == 0 ? b.Min.Z : b.Max.Z).TransformBy(toView);
                if (p.X < minX) minX = p.X;
                if (p.X > maxX) maxX = p.X;
                if (p.Y < minY) minY = p.Y;
                if (p.Y > maxY) maxY = p.Y;
            }
            return maxX >= x0 && minX <= x1 && maxY >= y0 && minY <= y1;
        }

        /// <summary>Координаты вида экрана -> мировые.</summary>
        private static Matrix3d ViewToWorld(Viewport vp)
        {
            return Matrix3d.Rotation(-vp.TwistAngle, vp.ViewDirection, vp.ViewTarget)
                 * Matrix3d.Displacement(vp.ViewTarget - Point3d.Origin)
                 * Matrix3d.PlaneToWorld(vp.ViewDirection);
        }

        /// <summary>Слои, выключенные или замороженные во всём чертеже.</summary>
        private static HashSet<ObjectId> HiddenLayers(Transaction tr, Database db)
        {
            var set = new HashSet<ObjectId>();
            var lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
            foreach (ObjectId id in lt)
            {
                var ltr = (LayerTableRecord)tr.GetObject(id, OpenMode.ForRead);
                if (ltr.IsOff || ltr.IsFrozen)
                    set.Add(id);
            }
            return set;
        }

        // ------------------------------------- рамка: что ещё видно в экранах

        /// <summary>
        /// Разбор рамки до масштабирования (см. FindMixed). Поля названы и
        /// устроены как у DwGangsta.MoveCommands.Analysis (перенос с видовыми
        /// экранами): при слиянии программ разбор можно будет брать оттуда —
        /// там он на индексе модели и с формой подрезанных экранов.
        /// </summary>
        public sealed class MixedInfo
        {
            /// <summary>Что ещё масштабировать для «Всё в видовом» (как Analysis.Extra).</summary>
            public readonly List<ObjectId> Extra = new List<ObjectId>();
            /// <summary>Из Extra — на выключенных и замороженных слоях (как MovePlan.ExtraHidden).</summary>
            public int ExtraHidden;
            /// <summary>Экраны, где выбранное видно вместе с чем-то ещё (как Analysis.ViewsWhole).</summary>
            public readonly List<ObjectId> ViewsWhole = new List<ObjectId>();
            /// <summary>Лист каждого экрана (как Analysis.Sheet).</summary>
            public readonly Dictionary<ObjectId, string> Sheet = new Dictionary<ObjectId, string>();
            /// <summary>Отчёт по листам — для вопроса пользователю (как Analysis.Report).</summary>
            public readonly List<string> Report = new List<string>();
            /// <summary>Ничего, кроме выбранного, в экранах не видно — спрашивать незачем (как Analysis.Clean).</summary>
            public bool Clean { get { return Extra.Count == 0; } }
        }

        /// <summary>
        /// Рамка: экраны листов, где выбранное видно вместе с чем-то ещё. Если
        /// масштабировать только выбранное, такой экран сохранить нельзя — он
        /// останется как был, а выбранное в нём сожмётся. «Всё в видовом» — взять и
        /// всё, что видно в области таких экранов (и скрытое слоями — чтобы
        /// не оказалось не на месте, когда слои включат). Добранное само может
        /// быть видно в других экранах вместе с чем-то ещё — разбор идёт по
        /// цепочке, пока набор не перестанет расти (как «Всё в видовом» в DwGangsta).
        /// </summary>
        public MixedInfo FindMixed(Database db)
        {
            var info = new MixedInfo();
            if (_wholeSpace || _selected.Count == 0)
                return info;

            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                HashSet<ObjectId> hidden = HiddenLayers(tr, db);
                var views = LayoutViews(tr, db);
                var frames = new List<Frame>();
                var frozen = new List<HashSet<ObjectId>>();
                foreach (var pair in views)
                {
                    frames.Add(new Frame(tr, pair.Key));
                    frozen.Add(FrozenIn(pair.Key));
                }

                var chosen = new List<Box>(_selected);
                var taken = new bool[_others.Count];
                var mixedViews = new HashSet<ObjectId>();
                var visibleBySheet = new Dictionary<string, HashSet<int>>();
                var hiddenBySheet = new Dictionary<string, HashSet<int>>();
                var viewsBySheet = new Dictionary<string, HashSet<ObjectId>>();

                for (int round = 0; round < 50; round++)
                {
                    int before = info.Extra.Count;
                    for (int vi = 0; vi < views.Count; vi++)
                    {
                        var pair = views[vi];
                        Viewport vp = pair.Key;
                        Frame frame = frames[vi];
                        HashSet<ObjectId> frozenHere = frozen[vi];

                        bool shows = false;
                        foreach (Box b in chosen)
                            if (Visible(b, hidden, frozenHere) && frame.Sees(tr, b))
                            { shows = true; break; }
                        if (!shows)
                            continue;

                        var vis = new List<int>();
                        var hid = new List<int>();
                        for (int i = 0; i < _others.Count; i++)
                        {
                            if (taken[i]) continue;
                            Box b = _others[i];
                            if (!frame.Sees(tr, b))
                                continue;
                            (Visible(b, hidden, frozenHere) ? vis : hid).Add(i);
                        }
                        if (vis.Count == 0)
                            continue;                       // видно только выбранное — экран и так пересчитается

                        mixedViews.Add(vp.ObjectId);
                        string sheet = pair.Value;
                        info.Sheet[vp.ObjectId] = sheet;
                        if (!visibleBySheet.ContainsKey(sheet))
                        {
                            visibleBySheet[sheet] = new HashSet<int>();
                            hiddenBySheet[sheet] = new HashSet<int>();
                            viewsBySheet[sheet] = new HashSet<ObjectId>();
                        }
                        viewsBySheet[sheet].Add(vp.ObjectId);
                        foreach (int i in vis) { visibleBySheet[sheet].Add(i); Take(i); }
                        foreach (int i in hid) { hiddenBySheet[sheet].Add(i); Take(i); info.ExtraHidden++; }
                    }
                    if (info.Extra.Count == before)
                        break;
                }

                foreach (ObjectId v in mixedViews) info.ViewsWhole.Add(v);
                foreach (var sheet in visibleBySheet.Keys)
                {
                    var hid = new HashSet<int>(hiddenBySheet[sheet]);
                    hid.ExceptWith(visibleBySheet[sheet]);
                    string line = "  лист «" + sheet + "», экранов " + viewsBySheet[sheet].Count +
                                  ": видно ещё " + visibleBySheet[sheet].Count + " (" + Layers(tr, visibleBySheet[sheet]) + ")";
                    if (hid.Count > 0)
                        line += "; скрыто слоями " + hid.Count + " (" + Layers(tr, hid) + ")";
                    info.Report.Add(line + ".");
                }
                foreach (Frame fr in frames) fr.Dispose();
                tr.Commit();

                void Take(int i)
                {
                    if (taken[i]) return;
                    taken[i] = true;
                    chosen.Add(_others[i]);
                    info.Extra.Add(_others[i].Id);
                }
            }
            return info;
        }

        /// <summary>Слои объектов — до трёх самых частых, «и ещё N».</summary>
        private string Layers(Transaction tr, IEnumerable<int> items)
        {
            var count = new Dictionary<ObjectId, int>();
            foreach (int i in items)
            {
                ObjectId l = _others[i].Layer;
                int n;
                count[l] = count.TryGetValue(l, out n) ? n + 1 : 1;
            }
            var list = new List<KeyValuePair<ObjectId, int>>(count);
            list.Sort((a, b) => b.Value.CompareTo(a.Value));
            var names = new List<string>();
            for (int k = 0; k < list.Count && k < 3; k++)
            {
                try { names.Add("«" + ((LayerTableRecord)tr.GetObject(list[k].Key, OpenMode.ForRead)).Name + "»"); }
                catch (System.Exception) { }
            }
            string s = "слои " + string.Join(", ", names);
            if (list.Count > 3) s += " и ещё " + (list.Count - 3);
            return s;
        }

        // ------------------------------------------------- PSLTSCALE листа

        /// <summary>
        /// PSLTSCALE конкретного листа: флаг 1 в DXF-коде 70 объекта листа.
        ///
        /// В API листа этого флага нет, а Database.Psltscale отдаёт значение
        /// только текущего листа. Поэтому читаем DXF-код напрямую через
        /// acdbEntGet — тот же entget, что в LISP. null — прочитать не удалось.
        ///
        /// Имя объекта (ads_name) — два числа, и второе не ноль: собранное
        /// вручную из OldIdPtr и нуля acdbEntGet не принимает (возвращал
        /// пусто, флаг не читался ни разу). Настоящее даёт acdbGetAdsName, а
        /// она лежит в acdbNN.dll с номером версии AutoCAD (acdb24 — 2021–2024).
        /// </summary>
        private static bool? LayoutPsltscale(ObjectId layoutId)
        {
            try
            {
                long[] ename = new long[2];
                if (!GetAdsName(ename, layoutId))
                    return null;
                IntPtr rb = acdbEntGet(ename);
                if (rb == IntPtr.Zero)
                    return null;
                using (ResultBuffer buf = ResultBuffer.Create(rb, true))
                {
                    // Код 70 у листа встречается дважды: сначала флаги печати
                    // (подкласс AcDbPlotSettings), потом флаги самого листа —
                    // после маркера (100 . "AcDbLayout"). Нужен второй.
                    bool inLayout = false;
                    foreach (TypedValue tv in buf)
                    {
                        if (tv.TypeCode == 100)
                            inLayout = (tv.Value as string) == "AcDbLayout";
                        else if (inLayout && tv.TypeCode == 70)
                            return (Convert.ToInt32(tv.Value) & 1) != 0;
                    }
                }
            }
            catch (System.Exception) { }
            return null;
        }

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int GetAdsNameFn(long[] ename, ObjectId id);

        private static GetAdsNameFn _getAdsName;

        private static bool GetAdsName(long[] ename, ObjectId id)
        {
            if (_getAdsName == null)
            {
                string dll = "acdb" + Autodesk.AutoCAD.ApplicationServices.Application.Version.Major + ".dll";
                IntPtr module = GetModuleHandle(dll);
                IntPtr fn = module == IntPtr.Zero ? IntPtr.Zero : GetProcAddress(module,
                    "?acdbGetAdsName@@YA?AW4ErrorStatus@Acad@@AEAY01_JVAcDbObjectId@@@Z");
                if (fn == IntPtr.Zero)
                    return false;
                _getAdsName = (GetAdsNameFn)Marshal.GetDelegateForFunctionPointer(fn, typeof(GetAdsNameFn));
            }
            return _getAdsName(ename, id) == 0;               // eOk
        }

        // В accore.dll функция экспортирована и с C++-именем, и с простым.
        private static IntPtr acdbEntGet(long[] ename)
        {
            try { return acdbEntGetCpp(ename); }
            catch (EntryPointNotFoundException) { return acdbEntGetC(ename); }
        }

        [DllImport("accore.dll", CallingConvention = CallingConvention.Cdecl, EntryPoint = "?acdbEntGet@@YAPEAUresbuf@@QEB_J@Z")]
        private static extern IntPtr acdbEntGetCpp(long[] ename);

        [DllImport("accore.dll", CallingConvention = CallingConvention.Cdecl, EntryPoint = "acdbEntGet")]
        private static extern IntPtr acdbEntGetC(long[] ename);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr GetModuleHandle(string name);

        [DllImport("kernel32.dll", CharSet = CharSet.Ansi, BestFitMapping = false)]
        private static extern IntPtr GetProcAddress(IntPtr module, string name);
    }
}
