using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

namespace DwGangsta
{
    /// <summary>
    /// Область пространства модели, показанная одним видовым экраном листа:
    /// выпуклый четырёхугольник в WCS (четырёхугольник, а не рамка, потому что ВЭ может быть повёрнут).
    /// </summary>
    public class ViewRegion
    {
        readonly Point2d[] _corners;   // против часовой стрелки
        readonly double _minX, _minY, _maxX, _maxY;

        ViewRegion(Point2d[] corners)
        {
            _corners = corners;
            _minX = _minY = double.MaxValue;
            _maxX = _maxY = double.MinValue;
            foreach (Point2d p in corners)
            {
                if (p.X < _minX) _minX = p.X;
                if (p.Y < _minY) _minY = p.Y;
                if (p.X > _maxX) _maxX = p.X;
                if (p.Y > _maxY) _maxY = p.Y;
            }
        }

        /// <summary>Габаритный прямоугольник области — то, что этот экран может показать.</summary>
        public Extents3d Bounds
        {
            get
            {
                return new Extents3d(new Point3d(_minX, _minY, 0), new Point3d(_maxX, _maxY, 0));
            }
        }

        /// <summary>Пересекается ли габаритный контейнер объекта с этой областью (проверка в плане XY).</summary>
        public bool Intersects(Extents3d ext)
        {
            // Быстрая отбраковка по общим рамкам.
            if (ext.MaxPoint.X < _minX || ext.MinPoint.X > _maxX) return false;
            if (ext.MaxPoint.Y < _minY || ext.MinPoint.Y > _maxY) return false;

            // Точная проверка: разделяющая ось между прямоугольником объекта и четырёхугольником ВЭ.
            var box = new[]
            {
                new Point2d(ext.MinPoint.X, ext.MinPoint.Y),
                new Point2d(ext.MaxPoint.X, ext.MinPoint.Y),
                new Point2d(ext.MaxPoint.X, ext.MaxPoint.Y),
                new Point2d(ext.MinPoint.X, ext.MaxPoint.Y)
            };
            return !HasSeparatingAxis(_corners, box) && !HasSeparatingAxis(box, _corners);
        }

        static bool HasSeparatingAxis(Point2d[] a, Point2d[] b)
        {
            for (int i = 0; i < a.Length; i++)
            {
                Point2d p1 = a[i], p2 = a[(i + 1) % a.Length];
                // нормаль к ребру
                double ax = -(p2.Y - p1.Y), ay = p2.X - p1.X;
                double len = Math.Sqrt(ax * ax + ay * ay);
                if (len < 1e-12) continue;
                ax /= len; ay /= len;

                double aMin, aMax, bMin, bMax;
                Project(a, ax, ay, out aMin, out aMax);
                Project(b, ax, ay, out bMin, out bMax);
                if (aMax < bMin - 1e-9 || bMax < aMin - 1e-9) return true;
            }
            return false;
        }

        static void Project(Point2d[] pts, double ax, double ay, out double min, out double max)
        {
            min = double.MaxValue; max = double.MinValue;
            foreach (Point2d p in pts)
            {
                double d = p.X * ax + p.Y * ay;
                if (d < min) min = d;
                if (d > max) max = d;
            }
        }

        /// <summary>
        /// Контур видового экрана в координатах модели — то, что этот экран показывает,
        /// по своей настоящей форме: прямоугольник или контур подрезки `_VPCLIP`.
        /// Возвращает null, если ВЭ показывает не вид сверху.
        /// </summary>
        public static Point3dCollection OutlineInModel(Viewport vp, Transaction tr)
        {
            Vector3d dir;
            if (!Flat(vp, out dir)) return null;

            List<Point3d> paper = null;
            if (vp.NonRectClipOn && !vp.NonRectClipEntityId.IsNull)
            {
                var clip = tr.GetObject(vp.NonRectClipEntityId, OpenMode.ForRead) as Curve;
                paper = TracePoints(clip);
            }
            if (paper == null || paper.Count < 3)
            {
                double x0 = vp.CenterPoint.X - vp.Width / 2.0, x1 = vp.CenterPoint.X + vp.Width / 2.0;
                double y0 = vp.CenterPoint.Y - vp.Height / 2.0, y1 = vp.CenterPoint.Y + vp.Height / 2.0;
                paper = new List<Point3d>
                {
                    new Point3d(x0, y0, 0), new Point3d(x1, y0, 0),
                    new Point3d(x1, y1, 0), new Point3d(x0, y1, 0)
                };
            }

            Matrix3d toModel = PaperToModel(vp, dir);
            var model = new Point3dCollection();
            foreach (Point3d point in paper) model.Add(point.TransformBy(toModel));
            return model;
        }

        /// <summary>
        /// Контур подрезки точками. Полилиния без дуг отдаёт свои вершины, всё остальное —
        /// кривая любой формы (круг, эллипс, сплайн) — считается по точкам вдоль неё.
        /// </summary>
        static List<Point3d> TracePoints(Curve clip)
        {
            if (clip == null) return null;

            var points = new List<Point3d>();
            try
            {
                var polyline = clip as Polyline;
                if (polyline != null && polyline.NumberOfVertices >= 3 && !polyline.HasBulges)
                {
                    for (int i = 0; i < polyline.NumberOfVertices; i++)
                        points.Add(polyline.GetPoint3dAt(i));
                    return points;
                }

                // Скругления подрезки — точками по дуге, иначе кадр срезался бы по хордам.
                int Samples = polyline != null ? Math.Max(64, polyline.NumberOfVertices * 16) : 64;
                double start = clip.StartParam, end = clip.EndParam;
                if (end <= start) return null;

                for (int i = 0; i < Samples; i++)
                    points.Add(clip.GetPointAtParameter(start + (end - start) * i / Samples));
            }
            catch (Exception) { return null; }

            return points.Count >= 3 ? points : null;
        }

        /// <summary>Вид сверху? Заодно отдаёт направление взгляда.</summary>
        static bool Flat(Viewport vp, out Vector3d dir)
        {
            dir = Vector3d.ZAxis;
            if (vp.PerspectiveOn) return false;
            if (vp.Height <= 0.0 || vp.Width <= 0.0 || vp.ViewHeight <= 0.0) return false;

            dir = vp.ViewDirection;
            if (dir.Length < 1e-12) dir = Vector3d.ZAxis;
            dir = dir.GetNormal();
            return Math.Abs(dir.DotProduct(Vector3d.ZAxis)) >= 0.9999;   // не 3D-вид
        }

        /// <summary>Перевод из координат листа в координаты модели для этого экрана.</summary>
        static Matrix3d PaperToModel(Viewport vp, Vector3d dir)
        {
            double scale = vp.ViewHeight / vp.Height;
            Point2d vc = vp.ViewCenter;

            // Лист -> DCS: масштаб вида и смещение центра.
            Matrix3d paperToDcs =
                Matrix3d.Displacement(new Vector3d(vc.X - vp.CenterPoint.X * scale,
                                                   vc.Y - vp.CenterPoint.Y * scale, 0.0)) *
                Matrix3d.Scaling(scale, Point3d.Origin);

            // DCS -> WCS: плоскость вида, повёрнутая на угол закручивания. Закручивание
            // поворачивает вид, а не чертёж, поэтому обратный ход берёт угол со знаком минус:
            // чертёж под ПСК с углом -32.76° показан экраном с закручиванием +32.76°.
            Matrix3d dcsToWcs =
                Matrix3d.Rotation(-vp.TwistAngle, dir, vp.ViewTarget) *
                Matrix3d.PlaneToWorld(new Plane(vp.ViewTarget, dir));

            return dcsToWcs * paperToDcs;
        }

        /// <summary>
        /// Сдвигает вид экрана вслед за моделью: то, что сдвинули на <paramref name="move"/>
        /// в WCS, остаётся на листе на прежнем месте. Смещение переводится в координаты
        /// вида (DCS) — с учётом закручивания — и прибавляется к центру вида.
        /// Экран должен быть открыт на запись. false — экран показывает не вид сверху.
        /// </summary>
        public static bool ShiftView(Viewport vp, Vector3d move)
        {
            Vector3d dir;
            if (!Flat(vp, out dir)) return false;

            Matrix3d dcsToWcs =
                Matrix3d.Rotation(-vp.TwistAngle, dir, vp.ViewTarget) *
                Matrix3d.PlaneToWorld(new Plane(vp.ViewTarget, dir));
            Vector3d dcs = move.TransformBy(dcsToWcs.Inverse());

            vp.ViewCenter = new Point2d(vp.ViewCenter.X + dcs.X, vp.ViewCenter.Y + dcs.Y);
            return true;
        }

        /// <summary>
        /// Область модели, видимая через видовой экран. Возвращает null, если ВЭ
        /// показывает не вид сверху (перспектива либо 3D) — такой ВЭ обрезать по рамке нельзя.
        /// </summary>
        public static ViewRegion FromViewport(Viewport vp, Transaction tr, double marginFactor)
        {
            Vector3d dir;
            if (!Flat(vp, out dir)) return null;

            // Рамка ВЭ в координатах листа, при необходимости — по контуру подрезки.
            double pMinX = vp.CenterPoint.X - vp.Width / 2.0;
            double pMaxX = vp.CenterPoint.X + vp.Width / 2.0;
            double pMinY = vp.CenterPoint.Y - vp.Height / 2.0;
            double pMaxY = vp.CenterPoint.Y + vp.Height / 2.0;

            if (vp.NonRectClipOn && !vp.NonRectClipEntityId.IsNull)
            {
                var clip = tr.GetObject(vp.NonRectClipEntityId, OpenMode.ForRead) as Entity;
                if (clip != null)
                {
                    try
                    {
                        Extents3d ce = clip.GeometricExtents;
                        pMinX = Math.Max(pMinX, ce.MinPoint.X);
                        pMinY = Math.Max(pMinY, ce.MinPoint.Y);
                        pMaxX = Math.Min(pMaxX, ce.MaxPoint.X);
                        pMaxY = Math.Min(pMaxY, ce.MaxPoint.Y);
                        if (pMaxX <= pMinX || pMaxY <= pMinY) return null;
                    }
                    catch (Exception) { /* контур без габаритов — используем полную рамку ВЭ */ }
                }
            }

            if (marginFactor > 0.0)
            {
                double mx = (pMaxX - pMinX) * marginFactor / 2.0;
                double my = (pMaxY - pMinY) * marginFactor / 2.0;
                pMinX -= mx; pMaxX += mx; pMinY -= my; pMaxY += my;
            }

            // Лист -> DCS: масштаб вида и смещение центра.
            double scale = vp.ViewHeight / vp.Height;
            double cx = vp.CenterPoint.X, cy = vp.CenterPoint.Y;
            Point2d vc = vp.ViewCenter;

            // DCS -> WCS: плоскость вида, повёрнутая на угол закручивания. Закручивание
            // поворачивает вид, а не чертёж, поэтому обратный ход берёт угол со знаком минус:
            // чертёж под ПСК с углом -32.76° показан экраном с закручиванием +32.76°.
            Matrix3d dcsToWcs =
                Matrix3d.Rotation(-vp.TwistAngle, dir, vp.ViewTarget) *
                Matrix3d.PlaneToWorld(new Plane(vp.ViewTarget, dir));

            var paperCorners = new[]
            {
                new Point2d(pMinX, pMinY), new Point2d(pMaxX, pMinY),
                new Point2d(pMaxX, pMaxY), new Point2d(pMinX, pMaxY)
            };

            var wcs = new Point2d[4];
            for (int i = 0; i < 4; i++)
            {
                var dcs = new Point3d(
                    (paperCorners[i].X - cx) * scale + vc.X,
                    (paperCorners[i].Y - cy) * scale + vc.Y,
                    0.0);
                Point3d w = dcs.TransformBy(dcsToWcs);
                wcs[i] = new Point2d(w.X, w.Y);
            }
            return new ViewRegion(wcs);
        }

        /// <summary>
        /// Какой из видовых экранов блока — сам лист, а не окно в модель.
        ///
        /// У активного листа его видно по Number == 1, но в фоновой базе (а именно так
        /// сборка и читает чертежи) номера не проставлены — у всех «−1». Считать листом
        /// первый в блоке нельзя: он там далеко не всегда первый, и тогда за лист примут
        /// настоящий видовой экран, а его содержимое потеряется.
        ///
        /// Надёжный признак: лист показывает сам себя один к одному — высота вида равна
        /// его высоте, а центр вида совпадает с его центром на листе. У окна в модель
        /// вид смотрит совсем в другое место и с другим масштабом.
        ///
        /// А у листа, который ни разу не открывали, такого экрана в чертеже может не быть
        /// вовсе — либо он лежит пустой, без размеров. Тогда ответ −1: все экраны блока
        /// настоящие. Брать в этом случае первый попавшийся нельзя — именно так и пропадал
        /// видовой экран: его принимали за лист, не двигали вслед за моделью и не обводили,
        /// и на листе оставалась пустая рамка.
        /// </summary>
        /// <returns>Индекс экрана самого листа или −1, если его в блоке нет.</returns>
        public static int PaperSpaceIndex(IList<Viewport> viewports)
        {
            for (int i = 0; i < viewports.Count; i++)
            {
                if (viewports[i].Number == 1) return i;
            }

            int found = -1;
            double widest = 0.0;
            for (int i = 0; i < viewports.Count; i++)
            {
                Viewport vp = viewports[i];
                if (!ShowsItself(vp)) continue;

                double size = vp.Width * vp.Height;
                if (found < 0 || size > widest) { found = i; widest = size; }
            }
            if (found >= 0) return found;

            // Пустой экран без размеров — это заготовка листа, которую AutoCAD наполняет
            // при первом открытии. Окном в модель она не является.
            for (int i = 0; i < viewports.Count; i++)
            {
                Viewport vp = viewports[i];
                if (vp.Width <= 0.0 || vp.Height <= 0.0) return i;
            }
            return -1;
        }

        /// <summary>Вид этого экрана — он сам: тот же масштаб и тот же центр.</summary>
        static bool ShowsItself(Viewport vp)
        {
            if (vp.Height <= 0.0 || vp.Width <= 0.0) return false;

            double scale = Math.Abs(vp.ViewHeight - vp.Height);
            if (scale > vp.Height * 1e-6) return false;

            double dx = Math.Abs(vp.ViewCenter.X - vp.CenterPoint.X);
            double dy = Math.Abs(vp.ViewCenter.Y - vp.CenterPoint.Y);
            double slack = Math.Max(vp.Width, vp.Height) * 1e-6;
            return dx <= slack && dy <= slack;
        }

        /// <summary>Собирает области всех видовых экранов листа.</summary>
        /// <param name="unsupported">true, если хотя бы один ВЭ показывает 3D/перспективу — модель чистить нельзя.</param>
        public static List<ViewRegion> Collect(Transaction tr, Layout layout,
                                               SplitOptions opt, out bool unsupported)
        {
            var regions = new List<ViewRegion>();
            unsupported = false;

            // Layout.GetViewports() заполнен только у активного листа, поэтому идём по блоку листа.
            var viewports = new List<Viewport>();
            var btr = (BlockTableRecord)tr.GetObject(layout.BlockTableRecordId, OpenMode.ForRead);
            foreach (ObjectId id in btr)
            {
                var vp = tr.GetObject(id, OpenMode.ForRead) as Viewport;
                if (vp != null) viewports.Add(vp);
            }

            int paperSpaceIndex = PaperSpaceIndex(viewports);
            Trace.Write("    ВЭ в блоке листа: " + viewports.Count + ", лист — индекс " + paperSpaceIndex);

            for (int i = 0; i < viewports.Count; i++)
            {
                if (i == paperSpaceIndex) continue;
                Viewport vp = viewports[i];

                // Свойство Viewport.On достоверно только на активном листе; в фоновой базе оно
                // всегда false. Признак выключенного ВЭ по формату DWG — нулевой номер
                // (положительный = включён и активен, -1 = включён, но лист сейчас не показан).
                if (vp.Number == 0 && !opt.IncludeOffViewports) continue;

                ViewRegion r = FromViewport(vp, tr, opt.MarginFactor);
                if (r == null) unsupported = true;
                else regions.Add(r);
            }
            return regions;
        }
    }
}
