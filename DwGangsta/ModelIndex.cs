using System;
using System.Collections.Generic;
using System.Text;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using AcAp = Autodesk.AutoCAD.ApplicationServices.Application;

namespace DwGangsta
{
    /// <summary>
    /// Индекс пространства модели для «Передвинуть с видовым»: где лежит каждый объект
    /// и на каком слое — всё, чтобы ответить «что видно в этом видовом экране», не
    /// открывая сотни тысяч объектов на каждый перенос.
    ///
    /// Строится один раз на чертёж: на импортированном PDF в модели 300 тысяч отрезков,
    /// и один их обход — несколько секунд, а на старом ноутбуке и полминуты. Дальше индекс
    /// держится свежим сам: события базы помечают изменённые объекты, и при следующем
    /// вызове пересчитываются только они.
    ///
    /// У отрезка рамка и есть сам отрезок (с одной пометкой, какая из диагоналей), поэтому
    /// «проходит ли он через кадр» проверяется без открытия объекта. У вставки блока
    /// запоминается, что внутри лежит на каких слоях: в экране, где слой месяца заморожен,
    /// эта часть блока не видна.
    /// </summary>
    public sealed class ModelIndex
    {
        public enum Kind : byte
        {
            None,        // стёрт, без габаритов или не в модели
            Box,         // по габаритам: текст, штриховка, размер, таблица
            Segment,     // отрезок: диагональ своей рамки
            Curve,       // дуга, полилиния, сплайн — точная проверка открывает объект
            Block,       // вставка блока: видимость по слоям содержимого
            Infinite     // прямая или луч
        }

        public struct Item
        {
            public ObjectId Id;
            public ObjectId Layer;
            public double MinX, MinY, MaxX, MaxY;
            public Kind Kind;
            /// <summary>Отрезок идёт из (MinX, MinY) в (MaxX, MaxY); иначе из (MinX, MaxY) в (MaxX, MinY).</summary>
            public bool Rising;
            /// <summary>Объект скрыт сам по себе (Visible = false).</summary>
            public bool Hidden;
            /// <summary>Объект Civil 3D (или другой надстройки): его форму надстройка пересчитывает сама.</summary>
            public bool Civil;
        }

        /// <summary>Часть содержимого блока: рамка, слой и слои вставок по дороге к ней.</summary>
        public struct Part
        {
            public double MinX, MinY, MaxX, MaxY;
            /// <summary>Слой, по которому часть видна или выключена.</summary>
            public ObjectId Layer;
            /// <summary>Слои вложенных вставок: заморожен любой — часть не видна.</summary>
            public ObjectId[] Gates;
            /// <summary>
            /// Из чего слита часть — в координатах описания блока; в модель их переводит To.
            /// Рамка слитой части грубее кадра: линии PDF-подложки в клетке 6×6 м «задевают»
            /// экран, хотя ни одна в него не заходит. null — часть не слита (атрибут).
            /// </summary>
            public Piece[] Pieces;
            public Matrix3d To;

            /// <summary>Заходит ли в кадр хоть одна исходная деталь части.</summary>
            public bool Touches(ViewShape shape)
            {
                if (!shape.TouchesBox(MinX, MinY, MaxX, MaxY)) return false;
                if (Pieces == null || shape.BoxInside(MinX, MinY, MaxX, MaxY)) return true;
                bool plain = To == Matrix3d.Identity;
                foreach (Piece d in Pieces)
                {
                    if (d.Path != null && d.Filled)
                    {
                        var poly = new Point2d[d.Path.Length];
                        for (int k = 0; k < poly.Length; k++) poly[k] = plain ? d.Path[k] : Piece.Map(d.Path[k], To);
                        if (shape.TouchesPolygon(poly)) return true;
                        continue;
                    }
                    if (d.Path != null)
                    {
                        Point2d prev = plain ? d.Path[0] : Piece.Map(d.Path[0], To);
                        for (int k = 1; k < d.Path.Length; k++)
                        {
                            Point2d next = plain ? d.Path[k] : Piece.Map(d.Path[k], To);
                            if (shape.SeesSegment(prev.X, prev.Y, next.X, next.Y)) return true;
                            prev = next;
                        }
                        continue;
                    }
                    if (d.Segment)
                    {
                        Point3d a = new Point3d(d.X0, d.Y0, 0), b = new Point3d(d.X1, d.Y1, 0);
                        if (!plain) { a = a.TransformBy(To); b = b.TransformBy(To); }
                        if (shape.SeesSegment(a.X, a.Y, b.X, b.Y)) return true;
                        continue;
                    }
                    double x0 = d.X0, y0 = d.Y0, x1 = d.X1, y1 = d.Y1;
                    if (!plain)
                    {
                        x0 = y0 = double.MaxValue; x1 = y1 = double.MinValue;
                        for (int c = 0; c < 4; c++)
                        {
                            Point3d p = new Point3d((c & 1) == 0 ? d.X0 : d.X1, (c & 2) == 0 ? d.Y0 : d.Y1, 0).TransformBy(To);
                            x0 = Math.Min(x0, p.X); y0 = Math.Min(y0, p.Y); x1 = Math.Max(x1, p.X); y1 = Math.Max(y1, p.Y);
                        }
                    }
                    if (shape.TouchesBox(x0, y0, x1, y1)) return true;
                }
                return false;
            }
        }

        /// <summary>Деталь части блока: отрезок (из X0,Y0 в X1,Y1), ломаная по кривой или рамка.</summary>
        public struct Piece
        {
            public double X0, Y0, X1, Y1;
            public bool Segment;
            /// <summary>Полилиния, дуга, сплайн — точками вдоль себя: повёрнутая рамка таблицы
            /// своими габаритами накрывает соседний кадр, а сама линия проходит мимо.</summary>
            public Point2d[] Path;
            /// <summary>Path — контур залитого (таблица, маска, текст): в кадре и тогда, когда кадр внутри него.</summary>
            public bool Filled;

            public static Point2d Map(Point2d p, Matrix3d to)
            {
                Point3d q = new Point3d(p.X, p.Y, 0).TransformBy(to);
                return new Point2d(q.X, q.Y);
            }

            /// <summary>Точки вдоль кривой; null — не разложить, тогда деталь судится по рамке.</summary>
            public static Point2d[] Sample(Curve curve)
            {
                try
                {
                    var points = new List<Point2d>();
                    // Плоская полилиния в плане — по вершинам и дугам; прочее (и полилиния не
                    // в плане: её вершины — в своей системе) — точками вдоль параметра в мировых.
                    var poly = curve as Polyline;
                    if (poly != null && poly.Normal.Z > 0.9999)
                    {
                        int segments = poly.Closed ? poly.NumberOfVertices : poly.NumberOfVertices - 1;
                        for (int k = 0; k < poly.NumberOfVertices; k++)
                        {
                            points.Add(poly.GetPoint2dAt(k));
                            if (k < segments && poly.GetBulgeAt(k) != 0)
                            {
                                Point2d[] arc = poly.GetArcSegment2dAt(k).GetSamplePoints(10);
                                for (int a = 1; a < arc.Length - 1; a++) points.Add(arc[a]);
                            }
                        }
                        if (poly.Closed && points.Count > 0) points.Add(points[0]);
                    }
                    else
                    {
                        double s = curve.StartParam, e = curve.EndParam;
                        int n = Math.Min(2000, Math.Max(32, (int)((e - s) * 4)));
                        for (int k = 0; k <= n; k++)
                        {
                            Point3d p = curve.GetPointAtParameter(s + (e - s) * k / n);
                            points.Add(new Point2d(p.X, p.Y));
                        }
                    }
                    return points.Count >= 2 ? points.ToArray() : null;
                }
                catch (System.Exception)
                {
                    return null;
                }
            }

            public Piece Moved(Matrix3d to)
            {
                if (Path != null)
                {
                    var path = new Point2d[Path.Length];
                    for (int k = 0; k < path.Length; k++) path[k] = Map(Path[k], to);
                    return new Piece { X0 = X0, Y0 = Y0, X1 = X1, Y1 = Y1, Path = path, Filled = Filled };
                }
                if (Segment)
                {
                    Point3d a = new Point3d(X0, Y0, 0).TransformBy(to), b = new Point3d(X1, Y1, 0).TransformBy(to);
                    return new Piece { X0 = a.X, Y0 = a.Y, X1 = b.X, Y1 = b.Y, Segment = true };
                }
                double x0 = double.MaxValue, y0 = double.MaxValue, x1 = double.MinValue, y1 = double.MinValue;
                for (int c = 0; c < 4; c++)
                {
                    Point3d p = new Point3d((c & 1) == 0 ? X0 : X1, (c & 2) == 0 ? Y0 : Y1, 0).TransformBy(to);
                    x0 = Math.Min(x0, p.X); y0 = Math.Min(y0, p.Y); x1 = Math.Max(x1, p.X); y1 = Math.Max(y1, p.Y);
                }
                return new Piece { X0 = x0, Y0 = y0, X1 = x1, Y1 = y1 };
            }
        }

        sealed class Infinite
        {
            public Point3d Base;
            public Vector3d Direction;
            public bool IsRay;
        }

        readonly Database _db;
        readonly ObjectId _modelId;
        readonly ObjectId _layerZero;
        Item[] _items = new Item[1024];
        int _count;
        Dictionary<ObjectId, int> _where = new Dictionary<ObjectId, int>();
        readonly Dictionary<ObjectId, Infinite> _infinite = new Dictionary<ObjectId, Infinite>();
        readonly HashSet<ObjectId> _dirty = new HashSet<ObjectId>();
        // Описания блоков, от которых зависят вставки модели: правка любого другого блока
        // (а их в чертеже сотни, одни размеры дают по блоку на каждый) индекс не трогает.
        readonly HashSet<ObjectId> _knownDefs = new HashSet<ObjectId>();

        // Содержимое описаний блоков по слоям и готовые части каждой вставки. Правка любого
        // описания сбрасывает оба: блоки вкладываются друг в друга, и разбирать, кого
        // задело, дороже, чем пересобрать нужное заново.
        readonly Dictionary<ObjectId, List<Part>> _defParts = new Dictionary<ObjectId, List<Part>>();
        readonly Dictionary<ObjectId, List<Part>> _insertParts = new Dictionary<ObjectId, List<Part>>();
        bool _defsChanged;
        bool _built;

        static readonly Dictionary<Database, ModelIndex> Indexes = new Dictionary<Database, ModelIndex>();
        static bool _hooked;

        ModelIndex(Database db)
        {
            _db = db;
            _modelId = SymbolUtilityServices.GetBlockModelSpaceId(db);
            _layerZero = db.LayerZero;
            db.ObjectAppended += OnChanged;
            db.ObjectModified += OnChanged;
            db.ObjectErased += OnErased;
            db.ObjectUnappended += OnChanged;
            db.ObjectReappended += OnChanged;
        }

        /// <summary>Индекс чертежа: создаётся при первом обращении, живёт до закрытия чертежа.</summary>
        public static ModelIndex For(Database db)
        {
            ModelIndex index;
            if (Indexes.TryGetValue(db, out index)) return index;

            index = new ModelIndex(db);
            Indexes[db] = index;
            if (!_hooked)
            {
                _hooked = true;
                try { AcAp.DocumentManager.DocumentToBeDestroyed += (s, e) => Forget(e.Document.Database); }
                catch (System.Exception) { /* без менеджера документов (тесты) — живёт до выгрузки */ }
            }
            return index;
        }

        static void Forget(Database db)
        {
            ModelIndex index;
            if (db == null || !Indexes.TryGetValue(db, out index)) return;
            Indexes.Remove(db);
            db.ObjectAppended -= index.OnChanged;
            db.ObjectModified -= index.OnChanged;
            db.ObjectErased -= index.OnErased;
            db.ObjectUnappended -= index.OnChanged;
            db.ObjectReappended -= index.OnChanged;
        }

        public bool IsBuilt => _built;
        public int Count => _count;
        [System.Runtime.CompilerServices.IndexerName("At")]
        public ref readonly Item this[int i] => ref _items[i];
        public ObjectId LayerZero => _layerZero;

        public bool TryFind(ObjectId id, out int index) => _where.TryGetValue(id, out index);

        /// <summary>
        /// Объект менялся с последнего освежения. После _MOVE так видно, что она
        /// действительно сдвинула, а что пропустила (например, на замороженном слое).
        /// </summary>
        public bool Touched(ObjectId id) => _dirty.Contains(id);

        /// <summary>Всё, что менялось с последнего освежения, — в том числе то, что поехало следом само.</summary>
        public IEnumerable<ObjectId> TouchedIds => _dirty;

        public Infinite3d InfiniteOf(int i)
        {
            Infinite line = _infinite[_items[i].Id];
            return new Infinite3d(line.Base, line.Direction, line.IsRay);
        }

        // ------------------------------------------------------------- события базы
        //
        // Обработчики только записывают номер объекта — они вызываются на каждую правку
        // в чертеже, в том числе на перенос тысяч объектов, и обязаны быть мгновенными.

        void OnChanged(object sender, ObjectEventArgs e) => Touch(e.DBObject);
        void OnErased(object sender, ObjectErasedEventArgs e) => Touch(e.DBObject);

        void Touch(DBObject o)
        {
            if (!_built || o == null) return;
            ObjectId owner;
            try { owner = o.OwnerId; }
            catch (System.Exception) { return; }

            if (owner == _modelId) { _dirty.Add(o.ObjectId); return; }
            if (o is AttributeReference) { _dirty.Add(owner); return; }   // атрибут — часть вставки
            // Правка описания блока, который вставлен в модель. Сама запись пространства
            // модели тоже «меняется» при каждом добавлении объекта — её не считаем.
            if (o is Entity && _knownDefs.Contains(owner)) _defsChanged = true;
            else if (o is BlockTableRecord && _knownDefs.Contains(o.ObjectId)) _defsChanged = true;
        }

        // ------------------------------------------------------------- построение

        /// <summary>
        /// Первый обход модели. Esc прерывает — тогда false, и индекс остаётся пустым.
        /// </summary>
        public bool Build(Transaction tr, Action<int, int> progress, Func<bool> cancelled)
        {
            _count = 0; _where.Clear(); _infinite.Clear(); _dirty.Clear();
            _cells = null; _moved.Clear();
            _defParts.Clear(); _insertParts.Clear(); _knownDefs.Clear();

            var ms = (BlockTableRecord)tr.GetObject(_modelId, OpenMode.ForRead);
            var ids = new List<ObjectId>();
            foreach (ObjectId id in ms) ids.Add(id);

            // Сразу на весь размер: иначе на сотнях тысяч объектов массив и словарь
            // десятки раз переезжают в новую память — на слабом ноутбуке это заметно.
            _items = new Item[Math.Max(1024, ids.Count + ids.Count / 8)];
            _where = new Dictionary<ObjectId, int>(ids.Count + ids.Count / 8);

            for (int i = 0; i < ids.Count; i++)
            {
                if ((i & 2047) == 0)
                {
                    progress?.Invoke(i, ids.Count);
                    if (cancelled != null && cancelled()) { _count = 0; _where.Clear(); return false; }
                }
                Put(tr, ids[i]);
            }
            progress?.Invoke(ids.Count, ids.Count);
            _built = true;
            _defsChanged = false;
            return true;
        }

        /// <summary>Пересчитывает то, что поменялось с прошлого раза. Возвращает, сколько объектов.</summary>
        public int Refresh(Transaction tr)
        {
            if (_defsChanged)
            {
                // Описание блока поправили — вставки могли поменять и габариты, и части.
                _defsChanged = false;
                _defParts.Clear();
                _insertParts.Clear();
                EntityBounds.Reset();
                for (int i = 0; i < _count; i++)
                    if (_items[i].Kind == Kind.Block) _dirty.Add(_items[i].Id);
            }

            int count = _dirty.Count;
            foreach (ObjectId id in _dirty)
            {
                _insertParts.Remove(id);
                Put(tr, id);
            }
            _dirty.Clear();
            return count;
        }

        /// <summary>Записывает объект в индекс заново — или вычёркивает, если его больше нет в модели.</summary>
        void Put(Transaction tr, ObjectId id)
        {
            Item item = Describe(tr, id);
            int at;
            if (_where.TryGetValue(id, out at))
            {
                if (_cells != null) _moved.Add(new KeyValuePair<int, Item>(at, _items[at]));
                _items[at] = item;
            }
            else if (item.Kind != Kind.None)
            {
                if (_count == _items.Length) Array.Resize(ref _items, _items.Length * 2);
                at = _count++;
                _where[id] = at;
                _items[at] = item;
                if (_cells != null) _moved.Add(new KeyValuePair<int, Item>(at, new Item { Kind = Kind.None }));
            }
        }

        Item Describe(Transaction tr, ObjectId id)
        {
            var item = new Item { Id = id, Kind = Kind.None };
            if (id.IsNull || id.IsErased || !id.IsValid) return item;

            Entity ent;
            try { ent = tr.GetObject(id, OpenMode.ForRead, false) as Entity; }
            catch (System.Exception) { return item; }
            if (ent == null || ent.OwnerId != _modelId) return item;

            item.Layer = ent.LayerId;
            item.Hidden = !ent.Visible;
            item.Civil = IsCivil(ent);

            var line = ent as Line;
            if (line != null)
            {
                Point3d a = line.StartPoint, b = line.EndPoint;
                item.MinX = Math.Min(a.X, b.X); item.MaxX = Math.Max(a.X, b.X);
                item.MinY = Math.Min(a.Y, b.Y); item.MaxY = Math.Max(a.Y, b.Y);
                item.Rising = (b.X - a.X) * (b.Y - a.Y) >= 0;
                item.Kind = Kind.Segment;
                return item;
            }

            if (ent is Xline || ent is Ray)
            {
                var x = ent as Xline;
                var r = ent as Ray;
                _infinite[id] = new Infinite
                {
                    Base = x != null ? x.BasePoint : r.BasePoint,
                    Direction = x != null ? x.UnitDir : r.UnitDir,
                    IsRay = r != null
                };
                item.MinX = item.MinY = double.MinValue;
                item.MaxX = item.MaxY = double.MaxValue;
                item.Kind = Kind.Infinite;
                return item;
            }

            Extents3d ext;
            if (!EntityBounds.TryGet(ent, out ext)) return item;
            item.MinX = ext.MinPoint.X; item.MinY = ext.MinPoint.Y;
            item.MaxX = ext.MaxPoint.X; item.MaxY = ext.MaxPoint.Y;

            if (IsPlainBlock(ent))
            {
                item.Kind = Kind.Block;
                _knownDefs.Add(((BlockReference)ent).BlockTableRecord);
            }
            // Объекты Civil 3D (поверхности, трубы, колодцы, трассы, виды профилей) в .NET
            // тоже «кривые», но поверхность — это площадь, а не линия: по линии судить о ней
            // нельзя. Их — по рамке, как блоки и штриховки: лучше лишний раз спросить, чем
            // пропустить поверхность, накрывшую весь кадр.
            else if (ent is Curve && !IsCivil(ent)) item.Kind = Kind.Curve;
            else item.Kind = Kind.Box;
            return item;
        }

        /// <summary>Объект Civil 3D (или другой надстройки) — не из самого AutoCAD.</summary>
        static bool IsCivil(Entity ent)
        {
            string ns = ent.GetType().Namespace;
            return ns != null && !ns.StartsWith("Autodesk.AutoCAD", StringComparison.Ordinal);
        }

        /// <summary>Вставка, у которой видимость считается по слоям содержимого.</summary>
        static bool IsPlainBlock(Entity ent)
        {
            // Массив вставок и таблица — тоже вставки, но содержимое описания у них
            // не совпадает с тем, что нарисовано: судим по их собственному слою.
            return ent is BlockReference && !(ent is MInsertBlock) && !(ent is Table);
        }

        // ------------------------------------------------------------- сетка
        //
        // Чтобы на каждый экран не перебирать всю модель (а за один перенос таких переборов
        // десятки), объекты разложены по клеткам. Клетка — четверть типичного экрана: на
        // экран приходится полтора-два десятка клеток. Объекты крупнее четырёх клеток, прямые
        // и лучи лежат отдельным списком — их проверяет каждый запрос, но их единицы.
        // Сетка строится при первом запросе; немного изменённых объектов переставляются в
        // ней точечно, много — сетка строится заново: так дешевле.

        Dictionary<long, List<int>> _cells;
        readonly List<int> _large = new List<int>();
        double _cell;
        int[] _stamp = new int[0];
        int _stampNo;
        readonly List<KeyValuePair<int, Item>> _moved = new List<KeyValuePair<int, Item>>();
        const int RebuildAfter = 4096;

        /// <summary>
        /// Номера объектов, чьи клетки задевают прямоугольник, — без повторов. Это
        /// кандидаты: точная проверка — дело вызывающего.
        /// </summary>
        public List<int> Candidates(double x0, double y0, double x1, double y1, double cellHint)
        {
            EnsureGrid(cellHint);
            var found = new List<int>();
            long cx0 = Cell(x0), cx1 = Cell(x1), cy0 = Cell(y0), cy1 = Cell(y1);
            if ((cx1 - cx0 + 1) * (cy1 - cy0 + 1) > 65536)
            {
                // Прямоугольник на пол-модели — клеток больше, чем смысла: просто все подряд.
                for (int i = 0; i < _count; i++) found.Add(i);
                return found;
            }

            if (++_stampNo == int.MaxValue) { Array.Clear(_stamp, 0, _stamp.Length); _stampNo = 1; }
            foreach (int i in _large)
                if (_stamp[i] != _stampNo) { _stamp[i] = _stampNo; found.Add(i); }
            for (long cx = cx0; cx <= cx1; cx++)
                for (long cy = cy0; cy <= cy1; cy++)
                {
                    List<int> list;
                    if (!_cells.TryGetValue(Key(cx, cy), out list)) continue;
                    foreach (int i in list)
                        if (_stamp[i] != _stampNo) { _stamp[i] = _stampNo; found.Add(i); }
                }
            return found;
        }

        void EnsureGrid(double hint)
        {
            if (hint <= 0 || double.IsNaN(hint) || double.IsInfinity(hint)) hint = 1;
            bool fits = _cells != null && hint < _cell * 4 && hint > _cell / 4;
            if (fits && _moved.Count <= RebuildAfter)
            {
                foreach (var pair in _moved)
                {
                    Unplace(pair.Key, pair.Value);
                    Place(pair.Key, _items[pair.Key]);
                }
                _moved.Clear();
                if (_stamp.Length < _count) Array.Resize(ref _stamp, _items.Length);
                return;
            }

            _cell = hint;
            _cells = new Dictionary<long, List<int>>();
            _large.Clear();
            _moved.Clear();
            for (int i = 0; i < _count; i++) Place(i, _items[i]);
            _stamp = new int[_items.Length];
            _stampNo = 0;
        }

        bool IsLarge(in Item it)
        {
            return it.Kind == Kind.Infinite ||
                   it.MaxX - it.MinX > _cell * 4 || it.MaxY - it.MinY > _cell * 4 ||
                   double.IsInfinity(it.MinX) || double.IsInfinity(it.MaxX) ||
                   Math.Abs(it.MinX) > 1e14 || Math.Abs(it.MinY) > 1e14;
        }

        void Place(int i, in Item it)
        {
            if (it.Kind == Kind.None) return;
            if (IsLarge(it)) { _large.Add(i); return; }
            for (long cx = Cell(it.MinX); cx <= Cell(it.MaxX); cx++)
                for (long cy = Cell(it.MinY); cy <= Cell(it.MaxY); cy++)
                {
                    long key = Key(cx, cy);
                    List<int> list;
                    if (!_cells.TryGetValue(key, out list)) _cells[key] = list = new List<int>(4);
                    list.Add(i);
                }
        }

        void Unplace(int i, in Item it)
        {
            if (it.Kind == Kind.None) return;
            if (IsLarge(it)) { _large.Remove(i); return; }
            for (long cx = Cell(it.MinX); cx <= Cell(it.MaxX); cx++)
                for (long cy = Cell(it.MinY); cy <= Cell(it.MaxY); cy++)
                {
                    List<int> list;
                    if (_cells.TryGetValue(Key(cx, cy), out list)) list.Remove(i);
                }
        }

        long Cell(double v) => (long)Math.Floor(v / _cell);

        static long Key(long cx, long cy) => (cx << 32) ^ (cy & 0xFFFFFFFFL);

        // ------------------------------------------------------------- блоки по слоям

        /// <summary>
        /// Части вставки в координатах модели: что из содержимого на каком слое и где.
        /// Слой «0» внутри блока становится слоем вставки — как и рисует AutoCAD.
        /// Пусто — содержимого не прочитать (внешняя ссылка не загружена и т. п.):
        /// тогда вставка судится по собственной рамке и слою.
        /// </summary>
        public List<Part> PartsOf(Transaction tr, int i)
        {
            Item item = _items[i];
            List<Part> parts;
            if (_insertParts.TryGetValue(item.Id, out parts)) return parts;

            parts = new List<Part>();
            try
            {
                var insert = (BlockReference)tr.GetObject(item.Id, OpenMode.ForRead);
                AddInsert(tr, insert, parts, new HashSet<ObjectId>());
                for (int k = 0; k < parts.Count; k++)
                {
                    // Верхний уровень: «0» — это слой самой вставки, и он же — общий вентиль.
                    Part p = parts[k];
                    p.Layer = Resolve(p.Layer, insert.LayerId);
                    p.Gates = Append(Map(p.Gates, insert.LayerId), insert.LayerId);
                    parts[k] = p;
                }
            }
            catch (System.Exception) { parts.Clear(); }

            _insertParts[item.Id] = parts;
            return parts;
        }

        /// <summary>Части вставки в системе её владельца: «0» ещё не разрешён.</summary>
        void AddInsert(Transaction tr, BlockReference insert, List<Part> into, HashSet<ObjectId> stack)
        {
            Matrix3d to = insert.BlockTransform;
            foreach (Part p in DefParts(tr, insert.BlockTableRecord, stack))
                into.Add(Moved(p, to));

            // Атрибуты живут во вставке, а не в описании.
            foreach (ObjectId id in insert.AttributeCollection)
            {
                var attribute = tr.GetObject(id, OpenMode.ForRead) as AttributeReference;
                if (attribute == null || attribute.Invisible || !attribute.Visible) continue;
                Extents3d ext;
                if (!EntityBounds.TryGet(attribute, out ext)) continue;
                into.Add(new Part
                {
                    MinX = ext.MinPoint.X, MinY = ext.MinPoint.Y, MaxX = ext.MaxPoint.X, MaxY = ext.MaxPoint.Y,
                    Layer = attribute.LayerId, Gates = new ObjectId[0]
                });
            }
        }

        /// <summary>
        /// Содержимое описания блока по слоям, в его собственных координатах. Объекты
        /// с одинаковым слоем и вентилями сливаются, но не в одну рамку на весь блок,
        /// а по клеткам 16×16: блок из четырёх разрезов не должен «быть виден» и в пустом
        /// промежутке между ними.
        /// </summary>
        List<Part> DefParts(Transaction tr, ObjectId defId, HashSet<ObjectId> stack)
        {
            List<Part> cached;
            if (_defParts.TryGetValue(defId, out cached)) return cached;
            if (!stack.Add(defId)) return new List<Part>();       // блок внутри самого себя
            _knownDefs.Add(defId);

            var raw = new List<Part>();
            var def = tr.GetObject(defId, OpenMode.ForRead) as BlockTableRecord;
            if (def != null)
            {
                foreach (ObjectId id in def)
                {
                    var ent = tr.GetObject(id, OpenMode.ForRead) as Entity;
                    if (ent == null || !ent.Visible) continue;

                    var definition = ent as AttributeDefinition;
                    if (definition != null && (!definition.Constant || definition.Invisible)) continue;

                    if (IsPlainBlock(ent))
                    {
                        // Вложенная вставка: её части — со слоем вставки вместо «0»
                        // и с её слоем среди вентилей.
                        var nested = (BlockReference)ent;
                        var inner = new List<Part>();
                        AddInsert(tr, nested, inner, stack);
                        foreach (Part p in inner)
                        {
                            // Детали — в координаты этого описания: сливаться будут с его собственными.
                            Part q = Settled(p);
                            q.Layer = Resolve(p.Layer, nested.LayerId);
                            q.Gates = Append(Map(p.Gates, nested.LayerId), nested.LayerId);
                            raw.Add(q);
                        }
                        continue;
                    }

                    Extents3d ext;
                    if (!EntityBounds.TryGet(ent, out ext)) continue;
                    var line = ent as Line;
                    Piece piece;
                    if (line != null)
                        piece = new Piece { X0 = line.StartPoint.X, Y0 = line.StartPoint.Y, X1 = line.EndPoint.X, Y1 = line.EndPoint.Y, Segment = true };
                    else
                    {
                        piece = new Piece { X0 = ext.MinPoint.X, Y0 = ext.MinPoint.Y, X1 = ext.MaxPoint.X, Y1 = ext.MaxPoint.Y };
                        if (Outlines.Has(id)) { piece.Path = Outlines.Of(ent); piece.Filled = piece.Path != null; }
                        else if (ent is Curve) piece.Path = Piece.Sample((Curve)ent);
                    }
                    raw.Add(new Part
                    {
                        MinX = ext.MinPoint.X, MinY = ext.MinPoint.Y, MaxX = ext.MaxPoint.X, MaxY = ext.MaxPoint.Y,
                        Layer = ent.LayerId, Gates = new ObjectId[0], Pieces = new[] { piece }, To = Matrix3d.Identity
                    });
                }
            }
            stack.Remove(defId);

            List<Part> merged = Merge(raw);
            _defParts[defId] = merged;
            return merged;
        }

        // 16 клеток по стороне: на блоке 32×49 м клетка ~2×3 м — мельче маленького экрана-фрагмента.
        const int Cells = 16;

        static List<Part> Merge(List<Part> raw)
        {
            if (raw.Count == 0) return raw;
            double x0 = double.MaxValue, y0 = double.MaxValue, x1 = double.MinValue, y1 = double.MinValue;
            foreach (Part p in raw)
            {
                x0 = Math.Min(x0, p.MinX); y0 = Math.Min(y0, p.MinY);
                x1 = Math.Max(x1, p.MaxX); y1 = Math.Max(y1, p.MaxY);
            }
            double cw = Math.Max((x1 - x0) / Cells, 1e-9), ch = Math.Max((y1 - y0) / Cells, 1e-9);

            var groups = new Dictionary<string, int>();
            var merged = new List<Part>();
            var pieces = new List<List<Piece>>();
            var key = new StringBuilder();
            foreach (Part p in raw)
            {
                int cx = Math.Min(Cells - 1, (int)(((p.MinX + p.MaxX) / 2 - x0) / cw));
                int cy = Math.Min(Cells - 1, (int)(((p.MinY + p.MaxY) / 2 - y0) / ch));
                key.Clear();
                key.Append(p.Layer.Handle.Value).Append('|').Append(cx).Append(',').Append(cy);
                foreach (ObjectId g in p.Gates) key.Append('|').Append(g.Handle.Value);

                int at;
                string k = key.ToString();
                if (groups.TryGetValue(k, out at))
                {
                    Part m = merged[at];
                    m.MinX = Math.Min(m.MinX, p.MinX); m.MinY = Math.Min(m.MinY, p.MinY);
                    m.MaxX = Math.Max(m.MaxX, p.MaxX); m.MaxY = Math.Max(m.MaxY, p.MaxY);
                    merged[at] = m;
                    pieces[at].AddRange(p.Pieces);
                }
                else
                {
                    groups[k] = merged.Count;
                    merged.Add(p);
                    pieces.Add(new List<Piece>(p.Pieces));
                }
            }
            for (int m = 0; m < merged.Count; m++)
            {
                Part q = merged[m];
                q.Pieces = pieces[m].ToArray();
                merged[m] = q;
            }
            return merged;
        }

        /// <summary>Часть с деталями, переведёнными через её To, — To становится единичной.</summary>
        static Part Settled(Part p)
        {
            if (p.Pieces == null)
                return new Part
                {
                    MinX = p.MinX, MinY = p.MinY, MaxX = p.MaxX, MaxY = p.MaxY, Layer = p.Layer, Gates = p.Gates,
                    Pieces = new[] { new Piece { X0 = p.MinX, Y0 = p.MinY, X1 = p.MaxX, Y1 = p.MaxY } }, To = Matrix3d.Identity
                };
            if (p.To == Matrix3d.Identity) return p;
            var moved = new Piece[p.Pieces.Length];
            for (int k = 0; k < moved.Length; k++) moved[k] = p.Pieces[k].Moved(p.To);
            p.Pieces = moved;
            p.To = Matrix3d.Identity;
            return p;
        }

        ObjectId Resolve(ObjectId layer, ObjectId insertLayer)
        {
            return layer == _layerZero ? insertLayer : layer;
        }

        ObjectId[] Map(ObjectId[] gates, ObjectId insertLayer)
        {
            var mapped = new ObjectId[gates.Length];
            for (int i = 0; i < gates.Length; i++) mapped[i] = Resolve(gates[i], insertLayer);
            return mapped;
        }

        static ObjectId[] Append(ObjectId[] gates, ObjectId layer)
        {
            foreach (ObjectId g in gates) if (g == layer) return gates;
            var more = new ObjectId[gates.Length + 1];
            gates.CopyTo(more, 0);
            more[gates.Length] = layer;
            return more;
        }

        /// <summary>Рамка части после переноса в систему владельца — по четырём углам в плане.</summary>
        static Part Moved(Part p, Matrix3d to)
        {
            double x0 = double.MaxValue, y0 = double.MaxValue, x1 = double.MinValue, y1 = double.MinValue;
            for (int c = 0; c < 4; c++)
            {
                var corner = new Point3d((c & 1) == 0 ? p.MinX : p.MaxX, (c & 2) == 0 ? p.MinY : p.MaxY, 0)
                                 .TransformBy(to);
                x0 = Math.Min(x0, corner.X); y0 = Math.Min(y0, corner.Y);
                x1 = Math.Max(x1, corner.X); y1 = Math.Max(y1, corner.Y);
            }
            return new Part { MinX = x0, MinY = y0, MaxX = x1, MaxY = y1, Layer = p.Layer, Gates = p.Gates,
                              Pieces = p.Pieces, To = p.Pieces == null ? Matrix3d.Identity : to * p.To };
        }
    }

    /// <summary>Прямая или луч — для проверки, проходит ли через кадр.</summary>
    public struct Infinite3d
    {
        public readonly Point3d Base;
        public readonly Vector3d Direction;
        public readonly bool IsRay;

        public Infinite3d(Point3d basePoint, Vector3d direction, bool isRay)
        {
            Base = basePoint; Direction = direction; IsRay = isRay;
        }
    }
}
