using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

namespace DwGangsta
{
    /// <summary>
    /// Настоящий контур объектов, у которых габариты врут: повёрнутая таблица, маска, растр,
    /// повёрнутый текст. Картограмма повёрнута вместе с чертежом на 59°, и рамка вокруг неё
    /// вдвое больше её самой — ею таблица и подпись «задевали» соседний кадр.
    /// </summary>
    public static class Outlines
    {
        static readonly Autodesk.AutoCAD.Runtime.RXClass[] Classes =
        {
            Autodesk.AutoCAD.Runtime.RXObject.GetClass(typeof(Table)),
            Autodesk.AutoCAD.Runtime.RXObject.GetClass(typeof(RasterImage)),
            Autodesk.AutoCAD.Runtime.RXObject.GetClass(typeof(MText)),
            Autodesk.AutoCAD.Runtime.RXObject.GetClass(typeof(DBText)),
        };

        /// <summary>Есть ли у объектов этого класса свой контур.</summary>
        public static bool Has(ObjectId id)
        {
            foreach (var c in Classes)
                if (id.ObjectClass.IsDerivedFrom(c)) return true;
            return false;
        }

        /// <summary>Контур в плане (замкнутый многоугольник); null — не прочитать, судить по рамке.</summary>
        public static Point2d[] Of(Entity e)
        {
            try
            {
                var table = e as Table;
                if (table != null)
                {
                    Vector3d along = table.Direction.GetNormal();
                    Vector3d down = table.Normal.CrossProduct(along).GetNormal().Negate();
                    if (table.FlowDirection == FlowDirection.BottomToTop) down = down.Negate();
                    Point3d p0 = table.Position, p1 = p0 + along * table.Width;
                    return Flat(p0, p1, p1 + down * table.Height, p0 + down * table.Height);
                }
                var image = e as RasterImage;      // и маска Wipeout
                if (image != null)
                {
                    var points = new List<Point3d>();
                    foreach (Point3d p in image.GetVertices()) points.Add(p);
                    return points.Count >= 3 ? Flat(points.ToArray()) : null;
                }
                // Текст — габариты того же текста без поворота, повёрнутые обратно: тот же
                // прямоугольник, что AutoCAD считает габаритами, только не раздутый поворотом.
                double angle; Point3d at; Vector3d normal;
                var mtext = e as MText;
                var text = e as DBText;
                if (mtext != null) { angle = mtext.Rotation; at = mtext.Location; normal = mtext.Normal; }
                else if (text != null) { angle = text.Rotation; at = text.Position; normal = text.Normal; }
                else return null;
                if (normal.Z > 0.9999)
                {
                    Matrix3d back = Matrix3d.Rotation(-angle, Vector3d.ZAxis, at);
                    using (var straight = e.GetTransformedCopy(back))
                    {
                        Extents3d x = straight.GeometricExtents;
                        Matrix3d turn = back.Inverse();
                        return Flat(new Point3d(x.MinPoint.X, x.MinPoint.Y, 0).TransformBy(turn),
                                    new Point3d(x.MaxPoint.X, x.MinPoint.Y, 0).TransformBy(turn),
                                    new Point3d(x.MaxPoint.X, x.MaxPoint.Y, 0).TransformBy(turn),
                                    new Point3d(x.MinPoint.X, x.MaxPoint.Y, 0).TransformBy(turn));
                    }
                }
            }
            catch (Exception) { }
            return null;
        }

        static Point2d[] Flat(params Point3d[] points)
        {
            var flat = new Point2d[points.Length];
            for (int i = 0; i < points.Length; i++) flat[i] = new Point2d(points[i].X, points[i].Y);
            return flat;
        }
    }
}
