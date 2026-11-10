using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

namespace DwGangsta
{
    /// <summary>
    /// Кадр видового экрана в модели по его настоящей форме — прямоугольник или
    /// контур подрезки (_VPCLIP) — и честная проверка «виден ли объект в кадре».
    ///
    /// По габаритам судить нельзя: рамка вокруг полчертежа своими габаритами
    /// накрывает каждый экран, хотя видна в нём разве что её линия. Поэтому
    /// линия, полилиния, дуга и прочие кривые видны, только если сама кривая
    /// проходит через кадр. Остальное (блоки, тексты, штриховки) — по габаритам,
    /// но против настоящего контура кадра, а не его описанного прямоугольника.
    /// </summary>
    public sealed class ViewShape : IDisposable
    {
        readonly Point2d[] _pts;
        readonly double _minX, _minY, _maxX, _maxY;
        readonly Polyline _edge;      // тот же контур кривой — для IntersectWith

        ViewShape(Point3dCollection outline)
        {
            _pts = new Point2d[outline.Count];
            _minX = _minY = double.MaxValue;
            _maxX = _maxY = double.MinValue;
            _edge = new Polyline(outline.Count);
            for (int i = 0; i < outline.Count; i++)
            {
                var p = new Point2d(outline[i].X, outline[i].Y);
                _pts[i] = p;
                _edge.AddVertexAt(i, p, 0, 0, 0);
                _minX = Math.Min(_minX, p.X); _maxX = Math.Max(_maxX, p.X);
                _minY = Math.Min(_minY, p.Y); _maxY = Math.Max(_maxY, p.Y);
            }
            _edge.Closed = true;
        }

        /// <summary>Тот же кадр, сдвинутый в модели: где экран смотрел до переноса или будет после.</summary>
        public ViewShape Offset(double dx, double dy)
        {
            var moved = new Point3dCollection();
            foreach (Point2d p in _pts) moved.Add(new Point3d(p.X + dx, p.Y + dy, 0));
            return new ViewShape(moved);
        }

        /// <summary>Кадр экрана; null — экран показывает не вид сверху (3D, перспектива).</summary>
        public static ViewShape FromViewport(Viewport vp, Transaction tr)
        {
            Point3dCollection outline = ViewRegion.OutlineInModel(vp, tr);
            return outline != null && outline.Count >= 3 ? new ViewShape(outline) : null;
        }

        public double MinX => _minX;
        public double MinY => _minY;
        public double MaxX => _maxX;
        public double MaxY => _maxY;

        /// <summary>Рамки хотя бы соприкасаются — первая, самая дешёвая отбраковка.</summary>
        public bool Overlaps(double x0, double y0, double x1, double y1)
        {
            return !(x1 < _minX || x0 > _maxX || y1 < _minY || y0 > _maxY);
        }

        /// <summary>Виден ли объект с габаритами ext в этом кадре.</summary>
        public bool Sees(Entity ent, Extents3d ext)
        {
            double x0 = ext.MinPoint.X, y0 = ext.MinPoint.Y, x1 = ext.MaxPoint.X, y1 = ext.MaxPoint.Y;
            if (!Overlaps(x0, y0, x1, y1)) return false;

            var line = ent as Line;
            if (line != null)
                return SeesSegment(line.StartPoint.X, line.StartPoint.Y, line.EndPoint.X, line.EndPoint.Y);

            var curve = ent as Curve;
            if (curve != null) return SeesCurve(curve, x0, y0, x1, y1);
            return TouchesBox(x0, y0, x1, y1);
        }

        /// <summary>
        /// Кривая: рамка целиком в кадре — видна; рамка мимо — нет. Только если рамка
        /// пересекает край кадра, считается настоящее пересечение — дорогое, но таких
        /// кривых на кадр единицы.
        /// </summary>
        public bool SeesCurve(Curve curve, double x0, double y0, double x1, double y1)
        {
            if (!TouchesBox(x0, y0, x1, y1)) return false;
            if (BoxInside(x0, y0, x1, y1)) return true;
            try
            {
                var hits = new Point3dCollection();
                curve.IntersectWith(_edge, Intersect.OnBothOperands,
                                    new Plane(Point3d.Origin, Vector3d.ZAxis), hits, IntPtr.Zero, IntPtr.Zero);
                if (hits.Count > 0) return true;
                // Не пересекает контур: либо целиком внутри, либо целиком снаружи
                // (в том числе вокруг кадра — как рамка участка).
                return Contains(new Point2d(curve.StartPoint.X, curve.StartPoint.Y));
            }
            catch (Exception)
            {
                return true;      // пересечение не считается — по габаритам, а они кадр задели
            }
        }

        /// <summary>Отрезок проходит через кадр: конец внутри или пересекает край.</summary>
        public bool SeesSegment(double ax, double ay, double bx, double by)
        {
            if (!Overlaps(Math.Min(ax, bx), Math.Min(ay, by), Math.Max(ax, bx), Math.Max(ay, by))) return false;
            var a = new Point2d(ax, ay);
            var b = new Point2d(bx, by);
            if (Contains(a) || Contains(b)) return true;
            for (int i = 0; i < _pts.Length; i++)
                if (Cross(_pts[i], _pts[(i + 1) % _pts.Length], a, b)) return true;
            return false;
        }

        /// <summary>Прямая или луч проходит через кадр: вершины контура по обе стороны от неё.</summary>
        public bool SeesInfinite(Infinite3d line)
        {
            double dx = line.Direction.X, dy = line.Direction.Y;
            if (Math.Abs(dx) < 1e-12 && Math.Abs(dy) < 1e-12) return false;   // смотрит вдоль Z
            bool left = false, right = false, ahead = !line.IsRay;
            foreach (Point2d p in _pts)
            {
                double vx = p.X - line.Base.X, vy = p.Y - line.Base.Y;
                double side = dx * vy - dy * vx;
                if (side >= 0) left = true;
                if (side <= 0) right = true;
                if (vx * dx + vy * dy >= 0) ahead = true;
            }
            return left && right && ahead;
        }

        /// <summary>Прямоугольник задевает контур кадра.</summary>
        public bool TouchesBox(double x0, double y0, double x1, double y1)
        {
            if (!Overlaps(x0, y0, x1, y1)) return false;
            var box = new[] { new Point2d(x0, y0), new Point2d(x1, y0), new Point2d(x1, y1), new Point2d(x0, y1) };
            foreach (Point2d p in box)
                if (Contains(p)) return true;
            foreach (Point2d p in _pts)
                if (p.X >= x0 && p.X <= x1 && p.Y >= y0 && p.Y <= y1) return true;
            for (int i = 0; i < _pts.Length; i++)
            {
                Point2d a = _pts[i], b = _pts[(i + 1) % _pts.Length];
                for (int k = 0; k < 4; k++)
                    if (Cross(a, b, box[k], box[(k + 1) % 4])) return true;
            }
            return false;
        }

        /// <summary>
        /// Прямоугольник целиком внутри кадра: все углы внутри, и край кадра в него
        /// не заходит (у невыпуклой подрезки он может вдаваться внутрь).
        /// </summary>
        public bool BoxInside(double x0, double y0, double x1, double y1)
        {
            if (x0 < _minX || x1 > _maxX || y0 < _minY || y1 > _maxY) return false;
            var box = new[] { new Point2d(x0, y0), new Point2d(x1, y0), new Point2d(x1, y1), new Point2d(x0, y1) };
            foreach (Point2d p in box)
                if (!Contains(p)) return false;
            foreach (Point2d p in _pts)
                if (p.X > x0 && p.X < x1 && p.Y > y0 && p.Y < y1) return false;
            for (int i = 0; i < _pts.Length; i++)
            {
                Point2d a = _pts[i], b = _pts[(i + 1) % _pts.Length];
                for (int k = 0; k < 4; k++)
                    if (Cross(a, b, box[k], box[(k + 1) % 4])) return false;
            }
            return true;
        }

        /// <summary>Точка внутри контура — подсчёт пересечений луча, годится и для невыпуклой подрезки.</summary>
        /// <summary>Многоугольник (контур маски, растра) задевает кадр: ребро проходит через кадр или кадр внутри него.</summary>
        public bool TouchesPolygon(IList<Point2d> poly)
        {
            for (int i = 0; i < poly.Count; i++)
            {
                Point2d a = poly[i], b = poly[(i + 1) % poly.Count];
                if (SeesSegment(a.X, a.Y, b.X, b.Y)) return true;
            }
            // Рёбра мимо — либо кадр целиком внутри многоугольника, либо они врозь.
            Point2d p = _pts[0];
            bool inside = false;
            for (int i = 0, j = poly.Count - 1; i < poly.Count; j = i++)
            {
                Point2d a = poly[i], b = poly[j];
                if ((a.Y > p.Y) != (b.Y > p.Y) && p.X < (b.X - a.X) * (p.Y - a.Y) / (b.Y - a.Y) + a.X)
                    inside = !inside;
            }
            return inside;
        }

        /// <summary>Точка внутри кадра.</summary>
        public bool ContainsPoint(double x, double y) => Contains(new Point2d(x, y));

        bool Contains(Point2d p)
        {
            bool inside = false;
            for (int i = 0, j = _pts.Length - 1; i < _pts.Length; j = i++)
            {
                Point2d a = _pts[i], b = _pts[j];
                if ((a.Y > p.Y) != (b.Y > p.Y) &&
                    p.X < (b.X - a.X) * (p.Y - a.Y) / (b.Y - a.Y) + a.X)
                    inside = !inside;
            }
            return inside;
        }

        static bool Cross(Point2d a, Point2d b, Point2d c, Point2d d)
        {
            double d1 = Side(c, d, a), d2 = Side(c, d, b), d3 = Side(a, b, c), d4 = Side(a, b, d);
            return ((d1 > 0) != (d2 > 0)) && ((d3 > 0) != (d4 > 0));
        }

        static double Side(Point2d a, Point2d b, Point2d p)
        {
            return (b.X - a.X) * (p.Y - a.Y) - (b.Y - a.Y) * (p.X - a.X);
        }

        public void Dispose()
        {
            _edge.Dispose();
        }
    }
}
