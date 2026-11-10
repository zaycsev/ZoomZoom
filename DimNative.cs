using System;
using System.Collections.Generic;
using System.Globalization;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

namespace ZoomZoom
{
    /// <summary>
    /// Размер из Revit — в обычный размер AutoCAD, нарисованный самим AutoCAD,
    /// но так, чтобы он выглядел как у Revit.
    ///
    /// Revit кладёт картинку каждого размера в именованный блок «DIMBLOCKn-
    /// Вид». При вставке в другой чертёж, где такой блок уже есть, AutoCAD
    /// берёт свой — и вставленный размер показывает чужую картинку. А любой
    /// правкой (перенос, копия) картинку Revit AutoCAD и так перерисовывает
    /// по-своему. Поэтому размер получает свой безымянный блок (*D) и рисует
    /// его AutoCAD — по переопределениям, подобранным под картинку Revit:
    ///
    ///   - подпись: стиль текста и текст Revit (число — живое, «<>»), высота
    ///     подогнана по длине надписи, положение — по середине надписи Revit;
    ///   - режим подписи DIMTMOVE (0 — размерная линия продлевается под
    ///     подпись, 1 — выноска с полкой, 2 — без выноски): пробуются все,
    ///     остаётся тот, чей рисунок ближе всего к картинке Revit;
    ///   - выносные линии: Revit рисует их от конца до конца, даже когда точка
    ///     привязки лежит на самой размерной линии, а AutoCAD в таком случае
    ///     не знает, куда тянуть. Начало выносной ставится туда, где у Revit
    ///     кончается линия, отступ — ноль, вылет — как у Revit. Длину
    ///     повёрнутого размера это не меняет: она меряется вдоль размерной.
    ///
    /// Такой размер дальше ведёт себя как нарисованный руками: копируется,
    /// крутится и вставляется куда угодно, и без плагина тоже.
    /// </summary>
    internal static class DimNative
    {
        private struct Seg
        {
            public Point3d A, B;
            public LineWeight W;
            public Seg(Point3d a, Point3d b, LineWeight w = LineWeight.ByBlock) { A = a; B = b; W = w; }
        }

        /// <summary>
        /// Размер нарисован не AutoCAD-ом: блок с именем (у AutoCAD они
        /// безымянные, *D) или отметка Revit в XDATA и картинка Revit в
        /// безымянном блоке (так оставляли прошлые версии Зум-зума). Отметка
        /// Revit остаётся и после того, как AutoCAD перерисовал размер сам, —
        /// такой размер уже свой: в его блоке служебные точки AutoCAD (POINT),
        /// которых в картинке Revit нет. Переводить его заново незачем: это
        /// долго (на чертеже с тысячами размеров — минуты) и сдвигает подписи.
        /// </summary>
        internal static bool IsForeign(Transaction tr, Dimension dim)
        {
            if (dim.Annotative == AnnotativeStates.True || dim.DimBlockId.IsNull)
                return false;
            var btr = (BlockTableRecord)tr.GetObject(dim.DimBlockId, OpenMode.ForRead);
            if (!btr.IsAnonymous)
                return true;
            ResultBuffer rb = dim.GetXDataForApplication("REVIT");
            if (rb == null)
                return false;
            rb.Dispose();
            var pointClass = Autodesk.AutoCAD.Runtime.RXObject.GetClass(typeof(DBPoint));
            foreach (ObjectId id in btr)
                if (id.ObjectClass.IsDerivedFrom(pointClass))
                    return false;
            return true;
        }

        /// <summary>
        /// Перевести размеры. Каждый — во вложенной транзакции: сорвётся
        /// один — откатится только он и останется как был. Прежние блоки
        /// Revit, на которые больше никто не ссылается, удаляются.
        /// </summary>
        internal static int ConvertAll(Database db, IEnumerable<ObjectId> ids)
        {
            int done = 0;
            var oldBlocks = new ObjectIdCollection();
            Dictionary<ObjectId, ObjectId> ownStyles = OwnStyles(db, ids);
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                foreach (ObjectId id in ids)
                {
                    using (Transaction one = db.TransactionManager.StartTransaction())
                    {
                        try
                        {
                            var dim = one.GetObject(id, OpenMode.ForWrite, false, true) as Dimension;
                            if (dim == null || !IsForeign(one, dim))
                                continue;
                            ObjectId own;
                            if (ownStyles.TryGetValue(dim.DimensionStyle, out own))
                                dim.DimensionStyle = own;
                            ObjectId old = dim.DimBlockId;
                            if (Convert(one, dim))
                            {
                                one.Commit();
                                done++;
                                if (old != dim.DimBlockId && !oldBlocks.Contains(old))
                                    oldBlocks.Add(old);
                            }
                        }
                        catch (System.Exception) { }
                    }
                }

                try
                {
                    db.Purge(oldBlocks);
                    foreach (ObjectId id in oldBlocks)
                        tr.GetObject(id, OpenMode.ForWrite).Erase();
                }
                catch (System.Exception) { }
                tr.Commit();
            }
            return done;
        }

        /// <summary>
        /// Свой размерный стиль на каждый набор настроек: копия «имя ZZxxxxxx»,
        /// где xxxxxx — отпечаток всех DIM-переменных стиля. Стили Revit
        /// называются одинаково во всех выгрузках («2_5__0_01_»), а настройки у
        /// них разные — и при вставке в чертёж, где одноимённый стиль уже есть,
        /// AutoCAD берёт свой: засечки, вылеты и зазоры у вставленных размеров
        /// меняются. С отпечатком в имени одинаковые стили сливаются, а разные
        /// друг друга не подменяют.
        /// </summary>
        private static Dictionary<ObjectId, ObjectId> OwnStyles(Database db, IEnumerable<ObjectId> ids)
        {
            var map = new Dictionary<ObjectId, ObjectId>();
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                var table = (DimStyleTable)tr.GetObject(db.DimStyleTableId, OpenMode.ForRead);
                foreach (ObjectId id in ids)
                {
                    try
                    {
                        var dim = tr.GetObject(id, OpenMode.ForRead, false, true) as Dimension;
                        if (dim == null || !IsForeign(tr, dim) || map.ContainsKey(dim.DimensionStyle))
                            continue;
                        var style = (DimStyleTableRecord)tr.GetObject(dim.DimensionStyle, OpenMode.ForRead);
                        string name = style.Name + " ZZ" + Fingerprint(tr, style);
                        if (name.Length > 255)
                            continue;
                        if (table.Has(name))
                        {
                            map[style.ObjectId] = table[name];
                            continue;
                        }
                        var copy = (DimStyleTableRecord)style.Clone();
                        copy.Name = name;
                        if (!table.IsWriteEnabled)
                            table.UpgradeOpen();
                        map[style.ObjectId] = table.Add(copy);
                        tr.AddNewlyCreatedDBObject(copy, true);
                    }
                    catch (System.Exception) { }
                }
                tr.Commit();
            }
            return map;
        }

        /// <summary>Отпечаток настроек стиля: все свойства Dim*, ссылки — по именам.</summary>
        private static string Fingerprint(Transaction tr, DimStyleTableRecord style)
        {
            var sb = new System.Text.StringBuilder();
            var props = typeof(DimStyleTableRecord).GetProperties(
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
            Array.Sort(props, (a, b) => string.CompareOrdinal(a.Name, b.Name));
            foreach (var p in props)
            {
                if (!p.Name.StartsWith("Dim", StringComparison.Ordinal) || !p.CanRead ||
                    p.GetIndexParameters().Length > 0)
                    continue;
                object v;
                try { v = p.GetValue(style, null); }
                catch (System.Exception) { continue; }
                if (v is ObjectId)
                {
                    var oid = (ObjectId)v;
                    string refName = string.Empty;
                    if (!oid.IsNull && oid.IsValid && !oid.IsErased)
                    {
                        try
                        {
                            var rec = tr.GetObject(oid, OpenMode.ForRead) as SymbolTableRecord;
                            if (rec != null) refName = rec.Name;
                        }
                        catch (System.Exception) { }
                    }
                    v = refName;
                }
                sb.Append(p.Name).Append('=')
                  .Append(System.Convert.ToString(v, CultureInfo.InvariantCulture)).Append(';');
            }
            using (var md5 = System.Security.Cryptography.MD5.Create())
            {
                byte[] h = md5.ComputeHash(System.Text.Encoding.UTF8.GetBytes(sb.ToString()));
                return BitConverter.ToString(h, 0, 3).Replace("-", "");
            }
        }

        /// <summary>Перевести размер. Картинка Revit берётся из его нынешнего блока.</summary>
        internal static bool Convert(Transaction tr, Dimension dim)
        {
            if (dim.DimBlockId.IsNull)
                return false;

            List<Seg> revit = Segments(tr, dim);
            string below;
            MText revitText = RevitText(tr, dim, out below);

            // У текста Revit задана ширина рамки, и габарит считается по ней,
            // а не по буквам: при выравнивании влево середина рамки правее
            // середины надписи. Ширину снимаем — блок всё равно пересоберётся.
            if (revitText != null && revitText.Width > 0)
            {
                revitText.UpgradeOpen();
                revitText.Width = 0;
            }
            Point3d? target = revitText != null ? Center(revitText) : (Point3d?)null;
            if (revitText != null)
            {
                _refAnchor = Anchor(revitText);
                _refDir = revitText.Direction.GetNormal();
                _refWidth = Length(revitText);
            }

            ObjectId oldBlock = dim.DimBlockId;
            var old = (BlockTableRecord)tr.GetObject(oldBlock, OpenMode.ForRead);
            bool named = !old.IsAnonymous;

            // Свой безымянный блок — как у размера, нарисованного в AutoCAD.
            if (named)
            {
                var bt = (BlockTable)tr.GetObject(dim.Database.BlockTableId, OpenMode.ForWrite);
                var btr = new BlockTableRecord();
                btr.Name = "*D";
                bt.Add(btr);
                tr.AddNewlyCreatedDBObject(btr, true);
                dim.DimBlockId = btr.ObjectId;
            }

            ResetTextWidth(dim);

            var rotated = dim as RotatedDimension;
            if (rotated != null)
                MatchExtensionLines(rotated, revit);

            if (revitText != null)
            {
                MatchText(tr, dim, revitText);
                if (below != null)
                {
                    // Не \X: под \X AutoCAD рисует полку между строками, а у
                    // Revit её нет — просто вторая строка.
                    dim.DimensionText = dim.DimensionText + "\\P" + below;
                    dim.RecomputeDimensionBlock(true);
                }
                FreeRotation(tr, dim, revitText.Direction);
            }

            if (target.HasValue)
                LayOut(tr, dim, revit, target.Value);
            else
            {
                Ticks ticks = RevitTicks(tr, dim, revit);
                if (ticks != null)
                    SetTicks(tr, dim, ticks, 1.0);
                dim.RecomputeDimensionBlock(true);
            }
            return true;
        }

        // ------------------------------------------------------------- засечки

        // Имена засечек, которые AutoCAD рисует как «галочку» (как _Oblique:
        // размерная не подрезается у засечки и продлевается на DIMDLE, засечки
        // не выворачиваются наружу). Свою засечку под любым другим именем
        // AutoCAD считает стрелкой и перекладывает весь размер. Взяты редкие
        // встроенные; _ArchTick — последним, им пользуются чаще.
        private static readonly string[] TickSlots = { "_Integral", "_Small", "_DotSmall", "_ArchTick" };
        private const string TickMark = "ZoomZoom: засечка Revit";

        // Во сколько раз крупнее рисуется засечка во втором варианте (и во
        // столько же раз меньше DIMASZ) — см. LayOut.
        private const double TickZoom = 100.0;

        /// <summary>
        /// Засечки Revit: вес линии, есть ли засечка на каждом конце и какая
        /// засечка стоит у AutoCAD (_Oblique или _None — у цепочек Revit
        /// пишет «без засечки», а засечки рисует в картинке сам).
        /// </summary>
        private sealed class Ticks
        {
            public LineWeight Weight;       // ByBlock — как у размерной
            public bool None1, None2;       // у AutoCAD на конце _None
            public bool Has1, Has2;         // у Revit на конце засечка
            public double Asz;              // DIMASZ до нас
        }

        /// <summary>
        /// Засечки Revit рисует своим весом линии (обычно 0.40 при 0.20 у
        /// размерной), а встроенная _Oblique у AutoCAD — весом размерной
        /// линии (ByBlock), отдельного веса для засечек у размера нет. Null —
        /// засечки у AutoCAD не _Oblique или у Revit их не разобрать.
        /// </summary>
        private static Ticks RevitTicks(Transaction tr, Dimension dim, List<Seg> revit)
        {
            var rotated = dim as RotatedDimension;
            var aligned = dim as AlignedDimension;
            Vector3d along = Along(dim);
            Point3d? line = DimLineOf(dim);
            if (along.IsZeroLength() || !line.HasValue)
                return null;
            ObjectId a1 = dim.Dimsah ? dim.Dimblk1 : dim.Dimblk;
            ObjectId a2 = dim.Dimsah ? dim.Dimblk2 : dim.Dimblk;
            bool none1 = IsBlock(tr, a1, "_None"), none2 = IsBlock(tr, a2, "_None");
            if (!(none1 || IsBlock(tr, a1, "_Oblique")) || !(none2 || IsBlock(tr, a2, "_Oblique")))
                return null;

            Vector3d across = dim.Normal.CrossProduct(along).GetNormal();
            Point3d d = line.Value;
            Point3d x1 = rotated != null ? rotated.XLine1Point : aligned.XLine1Point;
            Point3d x2 = rotated != null ? rotated.XLine2Point : aligned.XLine2Point;
            double s1 = (x1 - d).DotProduct(along), s2 = (x2 - d).DotProduct(along);
            double tol = 1e-6 * Math.Max(1.0, MaxAbs(d));

            Func<Point3d, bool> onLine = p => Math.Abs((p - d).DotProduct(across)) <= tol;
            Func<Point3d, double, bool> at = (p, s) => onLine(p) && Math.Abs((p - d).DotProduct(along) - s) <= tol;
            Func<Point3d, bool> atTick = p => at(p, s1) || at(p, s2);

            LineWeight? tick = null, main = null;
            bool mixed = false, has1 = false, has2 = false;
            foreach (Seg g in revit)
            {
                Vector3d v = g.B - g.A;
                if (v.Length <= tol)
                    continue;
                double c = Math.Abs(v.GetNormal().DotProduct(along));
                if (c > 0.999999)
                {
                    if (onLine(g.A) && onLine(g.B) && !main.HasValue)
                        main = g.W;
                    continue;
                }
                if (c < 0.05 || c > 0.995 || !(atTick(g.A) || atTick(g.B)))
                    continue;
                if (tick.HasValue && tick.Value != g.W)
                    mixed = true;                       // засечки разного веса — не выразить
                tick = g.W;
                has1 |= at(g.A, s1) || at(g.B, s1);
                has2 |= at(g.A, s2) || at(g.B, s2);
            }
            var t = new Ticks
            {
                Weight = LineWeight.ByBlock, Asz = dim.Dimasz,
                None1 = none1, None2 = none2, Has1 = has1, Has2 = has2,
            };
            if (!mixed && tick.HasValue && main.HasValue && (int)tick.Value >= 0 && tick.Value != main.Value)
                t.Weight = tick.Value;
            return t;
        }

        /// <summary>
        /// Засечки размера: копия _Oblique весом Revit, нарисованная в zoom
        /// раз крупнее, при DIMASZ в zoom раз меньше, — под встроенным
        /// именем-«галочкой» (TickSlots). Вид засечки тот же, раскладка — как
        /// у _Oblique. Без своего веса и без увеличения засечка остаётся _Oblique.
        /// </summary>
        private static bool SetTicks(Transaction tr, Dimension dim, Ticks t, double zoom)
        {
            // Конец получает нашу засечку, если там _Oblique, которую надо
            // утяжелить или увеличить, или _None, а Revit засечку нарисовал.
            bool change = t.Weight != LineWeight.ByBlock || zoom != 1.0;
            bool own1 = t.None1 ? t.Has1 : change;
            bool own2 = t.None2 ? t.Has2 : change;
            if (!own1 && !own2)
                return zoom == 1.0;
            ObjectId own = TickBlock(tr, dim.Database, t.Weight, zoom);
            if (own.IsNull)
                return false;
            ObjectId b1 = dim.Dimsah ? dim.Dimblk1 : dim.Dimblk;
            ObjectId b2 = dim.Dimsah ? dim.Dimblk2 : dim.Dimblk;
            dim.Dimsah = true;
            dim.Dimblk1 = own1 ? own : b1;
            dim.Dimblk2 = own2 ? own : b2;
            dim.Dimasz = t.Asz / zoom;
            return true;
        }

        /// <summary>
        /// Раскладка подписи (ChooseTextMode) в двух вариантах засечек.
        /// В режиме DIMTMOVE=0 AutoCAD не пускает подпись к выносной ближе
        /// DIMASZ x DIMSCALE (размер засечки), а Revit ставит ближе — подпись
        /// уезжает на 0.03–0.2 м. Засечка, нарисованная в TickZoom раз крупнее
        /// при во столько же раз меньшем DIMASZ, выглядит так же, а упор почти
        /// исчезает. Но DIMASZ участвует и в другом (продление размерной при
        /// засечках снаружи), поэтому второй вариант пробуется, только если
        /// подпись не встала на место, и остаётся, только если он ближе к
        /// картинке Revit.
        /// </summary>
        private static void LayOut(Transaction tr, Dimension dim, List<Seg> revit, Point3d target)
        {
            Ticks ticks = RevitTicks(tr, dim, revit);
            var start = DimState.Take(dim);

            if (ticks != null)
                SetTicks(tr, dim, ticks, 1.0);
            ChooseTextMode(tr, dim, revit, target);
            if (ticks == null)
                return;

            MText now = TextOf(tr, dim);
            double h = now != null ? now.TextHeight : 0;
            if (h <= 0 || Miss(tr, dim, target) <= 0.02 * h)
                return;

            double first = Rate(tr, dim, revit, target);
            var firstState = DimState.Take(dim);
            start.Put(dim);
            if (!SetTicks(tr, dim, ticks, TickZoom))
            {
                firstState.Put(dim);
                dim.RecomputeDimensionBlock(true);
                return;
            }
            ChooseTextMode(tr, dim, revit, target);
            if (Rate(tr, dim, revit, target) < first - 1e-9)
                return;
            firstState.Put(dim);
            dim.RecomputeDimensionBlock(true);
        }

        /// <summary>Всё, что раскладка меняет в размере, — чтобы вернуть вариант.</summary>
        private sealed class DimState
        {
            private double _gap, _asz;
            private bool _tix, _upt, _sah;
            private int _tmove;
            private string _text;
            private Point3d _textPos;
            private Point3d? _line;
            private ObjectId _blk1, _blk2;

            public static DimState Take(Dimension d)
            {
                return new DimState
                {
                    _gap = d.Dimgap, _asz = d.Dimasz, _tix = d.Dimtix, _upt = d.Dimupt, _sah = d.Dimsah,
                    _tmove = d.Dimtmove, _text = d.DimensionText, _textPos = d.TextPosition,
                    _line = DimLineOf(d), _blk1 = d.Dimblk1, _blk2 = d.Dimblk2,
                };
            }

            public void Put(Dimension d)
            {
                RestoreDimLine(d, _line);
                d.Dimgap = _gap; d.Dimasz = _asz; d.Dimtix = _tix; d.Dimupt = _upt;
                d.Dimsah = _sah; d.Dimblk1 = _blk1; d.Dimblk2 = _blk2;
                d.DimensionText = _text; d.Dimtmove = _tmove;
                d.TextPosition = _textPos;
            }
        }

        private static bool IsBlock(Transaction tr, ObjectId id, string name)
        {
            if (id.IsNull || !id.IsValid || id.IsErased)
                return false;
            var btr = tr.GetObject(id, OpenMode.ForRead) as BlockTableRecord;
            return btr != null && string.Equals(btr.Name, name, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Засечка нужного веса и увеличения: уже заведённая нами (узнаём по
        /// метке в описании блока) или новая — в первом свободном имени. Чужой
        /// блок с таким именем не трогаем; свободных нет — Null.
        /// </summary>
        private static ObjectId TickBlock(Transaction tr, Database db, LineWeight w, double zoom)
        {
            var ci = CultureInfo.InvariantCulture;
            string mark = TickMark + ", вес " +
                          (w == LineWeight.ByBlock ? "ByBlock" : ((int)w / 100.0).ToString("0.00", ci)) +
                          (zoom == 1.0 ? "" : ", x" + zoom.ToString("0.###", ci));
            var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
            foreach (string name in TickSlots)
            {
                if (!bt.Has(name))
                    continue;
                var b = (BlockTableRecord)tr.GetObject(bt[name], OpenMode.ForRead);
                if (b.Comments == mark || (zoom == 1.0 && w != LineWeight.ByBlock &&
                                           b.Comments == TickMark + ", вес " + ((int)w / 100.0).ToString("0.00", ci)))
                    return b.ObjectId;
            }
            foreach (string name in TickSlots)
            {
                if (bt.Has(name))
                    continue;
                var btr = new BlockTableRecord { Name = name, Comments = mark };
                if (!bt.IsWriteEnabled)
                    bt.UpgradeOpen();
                ObjectId id = bt.Add(btr);
                tr.AddNewlyCreatedDBObject(btr, true);
                // Косая черта — как у встроенной _Oblique: от (-0.5,-0.5) до
                // (0.5,0.5), слой 0, цвет и тип линии «по блоку».
                double r = 0.5 * zoom;
                var line = new Line(new Point3d(-r, -r, 0), new Point3d(r, r, 0));
                line.SetDatabaseDefaults(db);
                line.Layer = "0";
                line.ColorIndex = 0;
                line.Linetype = "ByBlock";
                line.LineWeight = w;
                btr.AppendEntity(line);
                tr.AddNewlyCreatedDBObject(line, true);
                return id;
            }
            return ObjectId.Null;
        }
        // ------------------------------------------------------- выносные линии

        private static void MatchExtensionLines(RotatedDimension dim, List<Seg> revit)
        {
            if (!dim.Normal.IsParallelTo(Vector3d.ZAxis))
                return;

            Matrix3d toWcs = Matrix3d.PlaneToWorld(dim.Normal);
            Vector3d along = Vector3d.XAxis.RotateBy(dim.Rotation, Vector3d.ZAxis).TransformBy(toWcs).GetNormal();
            Vector3d across = dim.Normal.CrossProduct(along).GetNormal();
            Point3d d = dim.DimLinePoint;

            double scale = dim.Dimscale > 0 ? dim.Dimscale : 1.0;
            double tol = 1e-6 * Math.Max(1.0, MaxAbs(d));

            // У каждой выносной Revit — отрезок вдоль неё, от tMin до tMax
            // (расстояние от размерной линии со знаком). AutoCAD рисует выносную
            // от начала до размерной и дальше на вылет DIMEXE — один на обе.
            // Какой конец считать началом, решаем перебором: годится конец,
            // за которым линия переходит размерную, а из двух выносных берём
            // пару с одинаковым вылетом.
            Point3d[] xl = { dim.XLine1Point, dim.XLine2Point };
            var choices = new List<KeyValuePair<double, double>>[2];   // (начало, вылет)
            double[] along2 = new double[2];
            for (int i = 0; i < 2; i++)
            {
                double s = (xl[i] - d).DotProduct(along);
                along2[i] = s;
                double tMin = double.MaxValue, tMax = double.MinValue;
                foreach (Seg g in revit)
                {
                    if (Math.Abs((g.A - d).DotProduct(along) - s) > tol ||
                        Math.Abs((g.B - d).DotProduct(along) - s) > tol)
                        continue;
                    foreach (Point3d p in new[] { g.A, g.B })
                    {
                        double t = (p - d).DotProduct(across);
                        tMin = Math.Min(tMin, t);
                        tMax = Math.Max(tMax, t);
                    }
                }
                choices[i] = new List<KeyValuePair<double, double>>();
                if (tMin > tMax)
                    continue;                           // выносной у Revit нет
                if (tMin < -tol && tMax >= -tol)
                    choices[i].Add(new KeyValuePair<double, double>(tMin, Math.Max(0, tMax)));
                if (tMax > tol && tMin <= tol)
                    choices[i].Add(new KeyValuePair<double, double>(tMax, Math.Max(0, -tMin)));
                if (choices[i].Count == 0)
                    return;                             // не переходит размерную — так AutoCAD не нарисует
            }

            bool has1 = choices[0].Count > 0, has2 = choices[1].Count > 0;
            if (!has1 && !has2)
                return;

            KeyValuePair<double, double>? c1 = null, c2 = null;
            double bestDiff = double.MaxValue;
            foreach (var a in has1 ? choices[0] : new List<KeyValuePair<double, double>> { new KeyValuePair<double, double>(double.NaN, double.NaN) })
                foreach (var b in has2 ? choices[1] : new List<KeyValuePair<double, double>> { new KeyValuePair<double, double>(double.NaN, double.NaN) })
                {
                    double diff = has1 && has2 ? Math.Abs(a.Value - b.Value) : 0;
                    if (diff < bestDiff)
                    {
                        bestDiff = diff;
                        c1 = has1 ? a : (KeyValuePair<double, double>?)null;
                        c2 = has2 ? b : (KeyValuePair<double, double>?)null;
                    }
                }
            double exe = c1.HasValue ? c1.Value.Value : c2.Value.Value;
            if (bestDiff > 1e-6 * Math.Max(1.0, exe))
                return;                                 // вылет у линий разный — одним DIMEXE не выразить

            if (c1.HasValue)
                dim.XLine1Point = d + along * along2[0] + across * c1.Value.Key;
            if (c2.HasValue)
                dim.XLine2Point = d + along * along2[1] + across * c2.Value.Key;
            dim.Dimse1 = !has1;
            dim.Dimse2 = !has2;
            dim.Dimexo = 0;
            dim.Dimexe = exe / scale;
            dim.DimfxlenOn = false;
        }

        // ----------------------------------------------------------------- подпись

        /// <summary>
        /// Снять заданную ширину подписи (TextDefinedSize). TransformBy при
        /// масштабировании записывает в сам размер ширину рамки текста Revit, а
        /// она шире букв: AutoCAD раскладывает размер по рамке, и полка под
        /// подписью кончается на (рамка − буквы) / 2 раньше края строки, а
        /// крупные подписи отодвигаются от выносной. Без заданной ширины рамка
        /// — по буквам, как у Revit. Свойство появилось в AutoCAD 2017 —
        /// через отражение, чтобы сборка под 2015–2016 тоже работала.
        /// </summary>
        private static void ResetTextWidth(Dimension dim)
        {
            var reset = typeof(Dimension).GetMethod("ResetTextDefinedSize", Type.EmptyTypes);
            if (reset == null)
                return;
            try { reset.Invoke(dim, null); }
            catch (System.Exception) { }
        }

        /// <summary>
        /// Revit записывает поворот подписи явно (TextRotation). AutoCAD же
        /// раскладывает размер так, будто поворота нет, и поворачивает подпись
        /// только потом, вокруг её середины: вертикальная подпись встаёт от
        /// линии на половину своей длины, а не высоты, и не с той стороны.
        /// Если без явного поворота AutoCAD сам повернёт подпись так же —
        /// поворот снимаем.
        /// </summary>
        private static void FreeRotation(Transaction tr, Dimension dim, Vector3d revitDir)
        {
            double rot = dim.TextRotation;
            if (rot == 0)
                return;
            dim.TextRotation = 0;
            dim.RecomputeDimensionBlock(true);
            MText now = TextOf(tr, dim);
            if (now != null && now.Direction.IsCodirectionalTo(revitDir, new Tolerance(1e-6, 1e-6)))
                return;
            dim.TextRotation = rot;
            dim.RecomputeDimensionBlock(true);
        }

        /// <summary>
        /// Подпись — тем же шрифтом и той же величины, что у Revit. Стиль
        /// текста размеру ставим Revit-овский, текст — Revit-овский же, но с
        /// числом, заменённым на «<>» (размер остаётся живым: изменится длина —
        /// изменится и число), а если величина всё равно разошлась — множитель
        /// высоты \H...x.
        /// </summary>
        private static void MatchText(Transaction tr, Dimension dim, MText revit)
        {
            string contents = revit.Contents ?? string.Empty;
            double revitLength = Length(revit);

            dim.TextStyleId = FreeHeightStyle(tr, revit.TextStyleId);      // DIMTXSTY
            dim.DimensionText = string.Empty;
            dim.RecomputeDimensionBlock(true);
            MText now = TextOf(tr, dim);
            if (now == null)
                return;

            string measured = now.Text ?? string.Empty;
            string body = contents;
            int at = measured.Length > 0 ? body.LastIndexOf(measured, StringComparison.Ordinal) : -1;
            if (at < 0 && measured.Length > 0)
            {
                // Revit пишет кириллицу кодами \U+XXXX, а AutoCAD отдаёт
                // измеренное (с суффиксом стиля, «1670 - по буквенной оси»)
                // обычными буквами — сравниваем раскодированное.
                string plain = System.Text.RegularExpressions.Regex.Replace(body, @"\\U\+([0-9A-Fa-f]{4})",
                    m => ((char)System.Convert.ToInt32(m.Groups[1].Value, 16)).ToString());
                at = plain.LastIndexOf(measured, StringComparison.Ordinal);
                if (at >= 0)
                    body = plain;
            }
            if (at >= 0)
                body = body.Remove(at, measured.Length).Insert(at, "<>");

            dim.DimensionText = body;
            dim.RecomputeDimensionBlock(true);
            now = TextOf(tr, dim);
            double nowLength = now != null ? Length(now) : 0;
            if (nowLength <= 0 || revitLength <= 0)
                return;

            // Шрифт тот же — значит, совпадёт длина надписи, совпадёт и высота
            // букв. По высоте рамки сверять нельзя: у Revit она выше самих букв.
            // Высоту задаём самому размеру (DIMTXT): по ней AutoCAD и раскладывает
            // размер — зазоры, место под подпись. Множитель \H в тексте он при
            // раскладке не видит, да и в чужом чертеже тот умножился бы заново.
            for (int pass = 0; pass < 3; pass++)
            {
                double k = revitLength / nowLength;
                if (Math.Abs(k - 1.0) <= 1e-4)
                    return;
                dim.Dimtxt = dim.Dimtxt * k;
                dim.RecomputeDimensionBlock(true);
                now = TextOf(tr, dim);
                nowLength = now != null ? Length(now) : 0;
                if (nowLength <= 0)
                    return;
            }

            // DIMTXT не подействовал — высота задана прямо в тексте Revit (\H).
            double rest = revitLength / nowLength;
            if (Math.Abs(rest - 1.0) > 1e-4)
                dim.DimensionText = "{\\H" + rest.ToString("0.########", CultureInfo.InvariantCulture) +
                                    "x;" + body + "}";
        }

        /// <summary>
        /// Стиль текста без фиксированной высоты. Фиксированная высота стиля
        /// перебивает DIMTXT, а у стилей Revit она задана (и после Зум-зума
        /// уменьшена только в копии _ZZ1000, а картинка размера ссылается на
        /// исходный стиль). Берём копию «имя_ZZDIM» с высотой 0 — тот же шрифт,
        /// та же ширина.
        /// </summary>
        private static ObjectId FreeHeightStyle(Transaction tr, ObjectId styleId)
        {
            var style = (TextStyleTableRecord)tr.GetObject(styleId, OpenMode.ForRead);
            if (style.TextSize == 0)
                return styleId;

            string name = style.Name + "_ZZDIM";
            var table = (TextStyleTable)tr.GetObject(style.OwnerId, OpenMode.ForRead);
            if (table.Has(name))
                return table[name];

            var copy = (TextStyleTableRecord)style.Clone();
            copy.Name = name;
            copy.TextSize = 0;
            table.UpgradeOpen();
            ObjectId id = table.Add(copy);
            tr.AddNewlyCreatedDBObject(copy, true);
            return id;
        }

        /// <summary>
        /// Режим подписи и её положение: пробуем все режимы, у каждого
        /// подгоняем положение до совпадения середины надписи, сравниваем
        /// нарисованное с картинкой Revit и берём самый похожий.
        /// </summary>
        private static void ChooseTextMode(Transaction tr, Dimension dim, List<Seg> revit, Point3d target)
        {
            var rotated = dim as RotatedDimension;
            var aligned = dim as AlignedDimension;
            Point3d? dimLine = rotated != null ? rotated.DimLinePoint
                             : aligned != null ? aligned.DimLinePoint : (Point3d?)null;

            // DIMTMOVE=0 двигает вместе с подписью и размерную линию — пробуем
            // его только там, где точку размерной линии можно вернуть.
            var modes = dimLine.HasValue ? new[] { 2, 1, 0 } : new[] { 2, 1 };

            Point3d? leaderEnd = dimLine.HasValue ? LeaderEnd(dim, revit, dimLine.Value) : null;

            double bestScore = double.MaxValue;
            int bestMode = 2;
            Point3d bestPos = target;
            double gap0 = dim.Dimgap, bestGap = gap0;
            bool tix0 = dim.Dimtix, bestTix = tix0;
            bool upt0 = dim.Dimupt, bestUpt = upt0;
            string text0 = dim.DimensionText, bestText = text0;
            Action<int, Point3d> consider = (mode, pos) =>
            {
                double score = Rate(tr, dim, revit, target);
                if (score < bestScore - 1e-9)
                {
                    bestScore = score;
                    bestMode = mode;
                    bestPos = pos;
                    bestGap = gap0;
                    bestTix = tix0;
                    bestUpt = upt0;
                    bestText = text0;
                }
            };

            foreach (int mode in modes)
            {
                RestoreDimLine(dim, dimLine);
                dim.Dimtmove = mode;
                if (mode == 2 || !dimLine.HasValue)
                {
                    consider(mode, Place(tr, dim, target));
                    continue;
                }
                if (mode == 0)
                {
                    // При DIMUPT=0 точка подписи в этом режиме задаёт только
                    // положение размерной линии, а вдоль неё AutoCAD ставит
                    // подпись сам — прижимает к выносной. DIMUPT=1 отдаёт
                    // точке и подпись. Пробуем оба, и с DIMTIX, и без.
                    foreach (bool upt in upt0 ? new[] { true } : new[] { false, true })
                        foreach (bool tix in tix0 ? new[] { true, false } : new[] { false })
                        {
                            RestoreDimLine(dim, dimLine);
                            dim.Dimgap = gap0;
                            dim.Dimtix = tix;
                            dim.Dimupt = upt;
                            Point3d pos = PlaceWithLine(tr, dim, target, dimLine.Value);
                            double before = bestScore;
                            consider(mode, pos);
                            if (bestScore < before)
                            {
                                bestGap = dim.Dimgap;
                                bestTix = tix;
                                bestUpt = upt;
                            }
                        }
                    dim.Dimgap = gap0;
                    dim.Dimtix = tix0;
                    dim.Dimupt = upt0;
                    continue;
                }

                // Режим 1: точка подписи — конец выноски AutoCAD-а, а куда он
                // ляжет относительно надписи, AutoCAD решает сам. Пробуем
                // несколько начальных точек: конец выноски Revit, концы надписи
                // на оси размерной линии (выноска вдоль линии — это продолжение
                // размерной вдоль подписи), — и каждую доводим малыми шагами.
                // DIMTIX («подпись всегда между выносными») у стилей Revit
                // включён; пробуем и с ним, и без него — с ним AutoCAD иногда
                // не считает подпись вынесенной и выноску не рисует.
                List<Point3d> starts = LeaderCandidates(tr, dim, dimLine.Value, leaderEnd, target);
                foreach (bool tix in tix0 ? new[] { true, false } : new[] { false })
                {
                    foreach (Point3d start in starts)
                    {
                        RestoreDimLine(dim, dimLine);
                        dim.Dimtmove = 1;
                        dim.Dimtix = tix;
                        double before = bestScore;
                        Point3d from = leaderEnd.HasValue ? WithLeader(tr, dim, dimLine.Value, start) : start;
                        consider(mode, Refine(tr, dim, revit, target, from));
                        if (bestScore < before)
                            bestTix = tix;
                    }
                }
                dim.Dimtix = tix0;
            }

            RestoreDimLine(dim, dimLine);
            dim.Dimgap = bestGap;
            dim.Dimtix = bestTix;
            dim.Dimupt = bestUpt;
            dim.DimensionText = bestText;
            dim.Dimtmove = bestMode;
            dim.TextPosition = bestPos;
            dim.RecomputeDimensionBlock(true);

        }

        /// <summary>
        /// DIMTMOVE=0: точка подписи — середина надписи, а размерную линию
        /// AutoCAD ставит от неё через зазор DIMGAP. Надпись ставим на место
        /// Revit, зазор подбираем так, чтобы размерная линия осталась где была.
        /// Если зазор вышел бы отрицательным (это рамка вокруг текста) — ноль,
        /// и надпись сдвигается вместе с линией: линия точно на месте, надпись
        /// — на остаток.
        /// </summary>
        private static Point3d PlaceWithLine(Transaction tr, Dimension dim, Point3d target, Point3d dimLine)
        {
            Vector3d along = Along(dim);
            if (along.IsZeroLength())
                return Place(tr, dim, target);
            Vector3d across = dim.Normal.CrossProduct(along).GetNormal();
            double side = Math.Sign((target - dimLine).DotProduct(across));
            double scale = dim.Dimscale > 0 ? dim.Dimscale : 1.0;
            double tol = 1e-9 * Math.Max(1.0, MaxAbs(dimLine));

            Point3d pos = Place(tr, dim, target);
            bool clamped = false;
            for (int pass = 0; pass < 4 && !clamped; pass++)
            {
                Point3d? now = DimLineOf(dim);
                if (!now.HasValue)
                    break;
                double off = (dimLine - now.Value).DotProduct(across);
                if (Math.Abs(off) < tol)
                    break;
                double gap = dim.Dimgap - side * off / scale;
                if (gap < 0)
                {
                    gap = 0;
                    clamped = true;
                }
                dim.Dimgap = gap;
                pos = Place(tr, dim, target);
            }

            Point3d? line = DimLineOf(dim);
            if (line.HasValue)
            {
                double off = (dimLine - line.Value).DotProduct(across);
                if (Math.Abs(off) >= tol)
                {
                    dim.TextPosition = pos + across * off;
                    dim.RecomputeDimensionBlock(true);
                    pos = dim.TextPosition;
                }
            }
            // Вдоль линии AutoCAD в этом режиме ставит подпись сам — прижимает
            // к выносной, и точка подписи на это почти не влияет. Пробуем
            // довести секущими; не выйдет — остаётся лучшее из найденного.
            Func<Point3d, double> missAlong = p =>
            {
                dim.TextPosition = p;
                dim.RecomputeDimensionBlock(true);
                MText t = TextOf(tr, dim);
                return t == null ? double.NaN : Offset(t).DotProduct(along);
            };
            double eps = 1e-9 * Math.Max(1.0, MaxAbs(target));
            Point3d p0 = pos;
            double m0 = missAlong(p0);
            Point3d best = p0;
            double bestMiss = Math.Abs(m0);
            if (!double.IsNaN(m0) && bestMiss > eps)
            {
                Point3d p1 = p0 + along * m0;
                double m1 = missAlong(p1);
                for (int pass = 0; pass < 8 && !double.IsNaN(m1); pass++)
                {
                    if (Math.Abs(m1) < bestMiss)
                    {
                        best = p1;
                        bestMiss = Math.Abs(m1);
                    }
                    if (bestMiss <= eps || Math.Abs(m1 - m0) < 1e-12)
                        break;
                    double step = (p1 - p0).DotProduct(along) * m1 / (m0 - m1);
                    p0 = p1; m0 = m1;
                    p1 = p1 + along * step;
                    m1 = missAlong(p1);
                }
            }
            dim.TextPosition = best;
            dim.RecomputeDimensionBlock(true);
            pos = best;
            return pos;
        }

        /// <summary>Насколько нарисованное сейчас расходится с картинкой Revit.</summary>
        private static double Rate(Transaction tr, Dimension dim, List<Seg> revit, Point3d target)
        {
            return Score(revit, Segments(tr, dim)) + Miss(tr, dim, target);
        }

        private static List<Point3d> LeaderCandidates(Transaction tr, Dimension dim, Point3d dimLine,
                                                      Point3d? leaderEnd, Point3d target)
        {
            var list = new List<Point3d>();
            if (leaderEnd.HasValue)
            {
                list.Add(leaderEnd.Value);
                // Конец выноски Revit бывает между выносными — там AutoCAD
                // считает подпись внутренней и выноску не рисует. Точка на
                // полке Revit у конца подписи — за выносными: AutoCAD рисует
                // выноску и полку вдоль подписи (и вдоль повёрнутой тоже).
                double s0 = (_refAnchor - leaderEnd.Value).DotProduct(_refDir);
                list.Add(leaderEnd.Value + _refDir * s0);
                list.Add(leaderEnd.Value + _refDir * (s0 + _refWidth));
            }
            // Концы надписи на оси размерной линии — по габариту надписи,
            // поставленной без выноски точно на место Revit.
            dim.Dimtmove = 2;
            Place(tr, dim, target);
            MText mt = TextOf(tr, dim);
            if (mt != null)
            {
                Vector3d along = Along(dim);
                Extents3d ext = mt.GeometricExtents;
                list.Add(dimLine + along * (ext.MinPoint - dimLine).DotProduct(along));
                list.Add(dimLine + along * (ext.MaxPoint - dimLine).DotProduct(along));
            }
            RestoreDimLine(dim, dimLine);
            dim.Dimtmove = 1;
            return list;
        }

        /// <summary>
        /// Выноску (DIMTMOVE=1) AutoCAD рисует, только если рамка подписи не
        /// заходит на выносные за размерной линией (их вылет DIMEXE). Revit
        /// ставит подпись чуть ближе (у E3B — на 8 мм), и AutoCAD считает её
        /// внутренней: выноски нет, подпись центрируется на точке. Если так —
        /// отодвигаем точку от размерной линии на самое малое расстояние, при
        /// котором выноска появляется (подбор делением пополам).
        /// </summary>
        private static Point3d WithLeader(Transaction tr, Dimension dim, Point3d dimLine, Point3d start)
        {
            Vector3d along = Along(dim);
            if (along.IsZeroLength())
                return start;
            Vector3d across = dim.Normal.CrossProduct(along).GetNormal();
            double off = (start - dimLine).DotProduct(across);
            if (Math.Abs(off) < 1e-9)
                return start;
            Vector3d away = across * Math.Sign(off);
            MText mt = TextOf(tr, dim);
            double h = mt != null && mt.TextHeight > 0 ? mt.TextHeight : 1.0;

            Func<double, bool> leader = d =>
            {
                dim.TextPosition = start + away * d;
                dim.RecomputeDimensionBlock(true);
                return HasLeader(tr, dim);
            };
            if (leader(0))
                return start;
            double lo = 0, hi = -1;
            foreach (double k in new[] { 0.05, 0.1, 0.2, 0.5, 1.0, 2.0 })
            {
                if (leader(k * h)) { hi = k * h; break; }
                lo = k * h;
            }
            if (hi < 0)
                return start;
            for (int i = 0; i < 30 && hi - lo > 1e-7 * h; i++)
            {
                double mid = (lo + hi) / 2;
                if (leader(mid)) hi = mid; else lo = mid;
            }
            leader(hi);
            return start + away * hi;
        }

        /// <summary>Нарисовал ли AutoCAD выноску: отрезок, кончающийся в точке подписи.</summary>
        private static bool HasLeader(Transaction tr, Dimension dim)
        {
            Point3d p = dim.TextPosition;
            double tol = 1e-6 * Math.Max(1.0, MaxAbs(p));
            foreach (Seg s in Segments(tr, dim))
                if (s.A.DistanceTo(p) <= tol || s.B.DistanceTo(p) <= tol)
                    return true;
            return false;
        }

        /// <summary>
        /// Довести точку подписи малыми шагами: сдвиг на промах середины
        /// надписи, пока от этого становится лучше.
        /// </summary>
        private static Point3d Refine(Transaction tr, Dimension dim, List<Seg> revit, Point3d target, Point3d start)
        {
            dim.TextPosition = start;
            dim.RecomputeDimensionBlock(true);
            Point3d best = start;
            double bestScore = Rate(tr, dim, revit, target);
            for (int pass = 0; pass < 4; pass++)
            {
                MText now = TextOf(tr, dim);
                if (now == null)
                    break;
                Point3d next = best + Offset(now);
                dim.TextPosition = next;
                dim.RecomputeDimensionBlock(true);
                double score = Rate(tr, dim, revit, target);
                if (score >= bestScore - 1e-9)
                    break;
                best = next;
                bestScore = score;
            }
            dim.TextPosition = best;
            dim.RecomputeDimensionBlock(true);
            return best;
        }

        /// <summary>
        /// Конец выноски Revit: отрезок, который начинается на размерной линии
        /// между выносными (не на краю — там засечки) и уходит от неё. Его
        /// дальний конец — место, где выноска переходит в полку под подписью.
        /// Null — выноски у Revit не было.
        /// </summary>
        private static Point3d? LeaderEnd(Dimension dim, List<Seg> revit, Point3d dimLine)
        {
            Vector3d along = Along(dim);
            if (along.IsZeroLength())
                return null;
            Vector3d across = dim.Normal.CrossProduct(along).GetNormal();

            var rotated = dim as RotatedDimension;
            var aligned = dim as AlignedDimension;
            Point3d x1 = rotated != null ? rotated.XLine1Point : aligned.XLine1Point;
            Point3d x2 = rotated != null ? rotated.XLine2Point : aligned.XLine2Point;
            double s1 = Math.Min((x1 - dimLine).DotProduct(along), (x2 - dimLine).DotProduct(along));
            double s2 = Math.Max((x1 - dimLine).DotProduct(along), (x2 - dimLine).DotProduct(along));
            double span = s2 - s1;
            if (span <= 0)
                return null;
            double tol = 1e-6 * Math.Max(1.0, MaxAbs(dimLine));
            double margin = 0.05 * span;

            foreach (Seg g in revit)
            {
                foreach (bool startOnLine in new[] { true, false })
                {
                    Point3d on = startOnLine ? g.A : g.B;
                    Point3d off = startOnLine ? g.B : g.A;
                    if (Math.Abs((on - dimLine).DotProduct(across)) > tol)
                        continue;
                    double s = (on - dimLine).DotProduct(along);
                    if (s < s1 + margin || s > s2 - margin)
                        continue;
                    if (Math.Abs((off - dimLine).DotProduct(across)) <= tol)
                        continue;                       // лежит на самой размерной
                    return off;
                }
            }
            return null;
        }

        private static Vector3d Along(Dimension dim)
        {
            var rotated = dim as RotatedDimension;
            if (rotated != null)
                return Vector3d.XAxis.RotateBy(rotated.Rotation, Vector3d.ZAxis)
                                     .TransformBy(Matrix3d.PlaneToWorld(dim.Normal)).GetNormal();
            var aligned = dim as AlignedDimension;
            if (aligned != null && !aligned.XLine1Point.IsEqualTo(aligned.XLine2Point))
                return (aligned.XLine2Point - aligned.XLine1Point).GetNormal();
            return new Vector3d();
        }

        private static Point3d? DimLineOf(Dimension dim)
        {
            var rotated = dim as RotatedDimension;
            if (rotated != null) return rotated.DimLinePoint;
            var aligned = dim as AlignedDimension;
            if (aligned != null) return aligned.DimLinePoint;
            return null;
        }

        private static void RestoreDimLine(Dimension dim, Point3d? p)
        {
            if (!p.HasValue)
                return;
            var rotated = dim as RotatedDimension;
            var aligned = dim as AlignedDimension;
            if (rotated != null) rotated.DimLinePoint = p.Value;
            else if (aligned != null) aligned.DimLinePoint = p.Value;
        }

        /// <summary>Поставить подпись так, чтобы середина надписи легла в target.</summary>
        private static Point3d Place(Transaction tr, Dimension dim, Point3d target)
        {
            dim.TextPosition = target;
            for (int pass = 0; pass < 4; pass++)
            {
                dim.RecomputeDimensionBlock(true);
                MText now = TextOf(tr, dim);
                if (now == null)
                    break;
                Vector3d miss = Offset(now);
                if (miss.Length < 1e-9 * Math.Max(1.0, MaxAbs(target)))
                    break;
                dim.TextPosition = dim.TextPosition + miss;
            }
            dim.RecomputeDimensionBlock(true);
            return dim.TextPosition;
        }

        private static double Miss(Transaction tr, Dimension dim, Point3d target)
        {
            MText now = TextOf(tr, dim);
            return now == null ? 0 : Offset(now).Length;
        }

        /// <summary>
        /// Насколько нарисованное расходится с картинкой Revit. Линии Revit,
        /// которых у AutoCAD нет (полка под подписью, продолжение размерной), —
        /// по худшей точке: одна пропавшая линия заметна сразу, а в среднем
        /// она тонет. Лишние линии AutoCAD — в среднем: продление размерной
        /// за засечку в цепочке размеров ложится на соседний и не видно.
        /// </summary>
        private static double Score(List<Seg> revit, List<Seg> drawn)
        {
            return OneWay(revit, drawn, true) + OneWay(drawn, revit, false);
        }

        private static double OneWay(List<Seg> from, List<Seg> to, bool worst)
        {
            if (from.Count == 0)
                return 0;
            if (to.Count == 0)
                return 1e9;
            double sum = 0, max = 0;
            int n = 0;
            foreach (Seg s in from)
            {
                for (int k = 0; k <= 8; k++)
                {
                    Point3d p = s.A + (s.B - s.A) * (k / 8.0);
                    double best = double.MaxValue;
                    foreach (Seg t in to)
                        best = Math.Min(best, Distance(p, t));
                    sum += best;
                    max = Math.Max(max, best);
                    n++;
                }
            }
            return worst ? max : sum / n;
        }

        private static double Distance(Point3d p, Seg s)
        {
            Vector3d v = s.B - s.A;
            double len2 = v.DotProduct(v);
            if (len2 <= 0)
                return p.DistanceTo(s.A);
            double t = Math.Max(0, Math.Min(1, (p - s.A).DotProduct(v) / len2));
            return p.DistanceTo(s.A + v * t);
        }

        // ------------------------------------------------------------ картинка

        /// <summary>Отрезки картинки размера; засечки-блоки разбираются на линии.</summary>
        private static List<Seg> Segments(Transaction tr, Dimension dim)
        {
            var list = new List<Seg>();
            var btr = (BlockTableRecord)tr.GetObject(dim.DimBlockId, OpenMode.ForRead);
            foreach (ObjectId id in btr)
            {
                var ent = tr.GetObject(id, OpenMode.ForRead) as Entity;
                AddSegments(ent, list);
            }
            return list;
        }

        private static void AddSegments(Entity ent, List<Seg> list)
        {
            var line = ent as Line;
            if (line != null)
            {
                list.Add(new Seg(line.StartPoint, line.EndPoint, line.LineWeight));
                return;
            }
            var br = ent as BlockReference;
            if (br == null)
                return;
            using (var parts = new DBObjectCollection())
            {
                try { br.Explode(parts); }
                catch (System.Exception) { return; }
                foreach (DBObject o in parts)
                {
                    var l = o as Line;
                    if (l != null)
                        list.Add(new Seg(l.StartPoint, l.EndPoint, l.LineWeight));
                    var pl = o as Polyline;
                    if (pl != null)
                        for (int i = 0; i + 1 < pl.NumberOfVertices; i++)
                            list.Add(new Seg(pl.GetPoint3dAt(i), pl.GetPoint3dAt(i + 1)));
                    o.Dispose();
                }
            }
        }

        /// <summary>
        /// Длина надписи вдоль строки. Габарит (GeometricExtents) у надписи
        /// внутри блока размера после пересчёта бывает прежним, а фактическая
        /// ширина строки (ActualWidth) пересчитывается всегда.
        /// </summary>
        private static double Length(MText mt)
        {
            try { return mt.ActualWidth; }
            catch (System.Exception) { return 0; }
        }

        /// <summary>
        /// Подпись Revit. Подпись с текстом под линией («&lt;&gt;\X2700 - по
        /// цифровой оси») Revit кладёт двумя надписями, и первой в блоке бывает
        /// нижняя. Подписью размера считаем верхнюю (в ней число), нижнюю
        /// возвращаем в below — она пойдёт после \X, как у самого размера.
        /// </summary>
        private static MText RevitText(Transaction tr, Dimension dim, out string below)
        {
            below = null;
            var list = new List<MText>();
            var btr = (BlockTableRecord)tr.GetObject(dim.DimBlockId, OpenMode.ForRead);
            foreach (ObjectId id in btr)
            {
                var mt = tr.GetObject(id, OpenMode.ForRead) as MText;
                if (mt != null)
                    list.Add(mt);
            }
            if (list.Count != 2)
                return list.Count > 0 ? list[0] : null;
            Vector3d up = list[0].Normal.CrossProduct(list[0].Direction).GetNormal();
            bool firstOnTop = (Center(list[0]) - Center(list[1])).DotProduct(up) > 0;
            MText top = firstOnTop ? list[0] : list[1];
            below = (firstOnTop ? list[1] : list[0]).Contents;
            return top;
        }

        private static MText TextOf(Transaction tr, Dimension dim)
        {
            var btr = (BlockTableRecord)tr.GetObject(dim.DimBlockId, OpenMode.ForRead);
            foreach (ObjectId id in btr)
            {
                var mt = tr.GetObject(id, OpenMode.ForRead) as MText;
                if (mt != null)
                    return mt;
            }
            return null;
        }

        // Надпись Revit, с которой совмещаем подпись AutoCAD (на время Convert).
        private static Point3d _refAnchor;
        private static Vector3d _refDir = Vector3d.XAxis;    // направление строки Revit
        private static double _refWidth;                      // длина строки Revit

        /// <summary>
        /// На сколько сдвинуть подпись AutoCAD, чтобы её буквы легли на буквы
        /// Revit: совмещаем левый край строки на базовой линии (Anchor).
        /// </summary>
        private static Vector3d Offset(MText now)
        {
            return _refAnchor - Anchor(now);
        }

        /// <summary>
        /// Левый край строки на базовой линии — точка, по которой буквы
        /// совпадают при любом выравнивании и как бы программа ни считала
        /// рамку строки (у Revit рамка выше букв, у AutoCAD — по высоте букв).
        /// Считается из точки вставки, выравнивания, ширины строки и высоты
        /// букв (TextHeight): верх рамки — на высоте букв над базовой линией.
        /// </summary>
        private static Point3d Anchor(MText mt)
        {
            double w = mt.ActualWidth, h = mt.TextHeight;
            int a = (int)mt.Attachment;
            if (a < 1 || a > 9)
                a = 1;
            int col = (a - 1) % 3;
            int row = (a - 1) / 3;
            Vector3d ux = mt.Direction.GetNormal();
            Vector3d uy = mt.Normal.CrossProduct(ux).GetNormal();
            // В несколько строк — базовая линия первой строки: на высоту букв
            // ниже верха рамки, а верх — по полной высоте надписи.
            string c = mt.Contents ?? string.Empty;
            if (c.IndexOf("\\P", StringComparison.Ordinal) >= 0)
            {
                double full = mt.ActualHeight;
                Point3d top = mt.Location + uy * (row == 0 ? 0 : row == 1 ? full / 2 : full);
                return top - ux * (col * w / 2) - uy * h;
            }
            return mt.Location - ux * (col * w / 2) - uy * (row == 0 ? h : row == 1 ? h / 2 : 0);
        }

        /// <summary>
        /// Середина надписи — из точки вставки, выравнивания и фактических
        /// ширины и высоты строки. Габарит (GeometricExtents) у надписи внутри
        /// блока размера после пересчёта бывает устаревшим.
        /// </summary>
        private static Point3d Center(MText mt)
        {
            double w = mt.ActualWidth, h = mt.ActualHeight;
            int a = (int)mt.Attachment;
            if (a < 1 || a > 9)
                a = 1;
            int col = (a - 1) % 3;                  // 0 — влево, 1 — по центру, 2 — вправо
            int row = (a - 1) / 3;                  // 0 — верх, 1 — середина, 2 — низ
            double x = w / 2 - col * w / 2;
            double y = row == 0 ? -h / 2 : row == 2 ? h / 2 : 0;
            Vector3d ux = mt.Direction.GetNormal();
            Vector3d uy = mt.Normal.CrossProduct(ux).GetNormal();
            return mt.Location + ux * x + uy * y;
        }

        private static double MaxAbs(Point3d p)
        {
            return Math.Max(Math.Abs(p.X), Math.Max(Math.Abs(p.Y), Math.Abs(p.Z)));
        }
    }
}