using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

namespace DwGangsta
{
    /// <summary>
    /// Габариты объекта модели — с поправкой на враньё в габаритном контейнере.
    /// Встречается многострочный текст, у которого AutoCAD показывает контейнер
    /// в десятки тысяч раз больше самого текста: надпись «100» размером 152x80
    /// отдаётся как 80x4512446. По такому габариту объект попадает в любой отбор
    /// и растягивает раскладку на километры, поэтому у текста, чей контейнер
    /// не сходится с содержимым, берётся прямоугольник по самому содержимому.
    /// </summary>
    public static class EntityBounds
    {
        /// <summary>Во сколько раз контейнер может превышать текст, чтобы ему ещё верили.</summary>
        const double TrustFactor = 10.0;

        /// <summary>
        /// Габариты описаний блоков — считаются один раз на чертёж. Блок с враньём внутри
        /// вставлен обычно не единожды, а перебирать его содержимое на каждую вставку дорого.
        /// </summary>
        static readonly Dictionary<ObjectId, Extents3d?> Blocks = new Dictionary<ObjectId, Extents3d?>();

        /// <summary>Забыть посчитанное: чертежи открываются по очереди, и номера объектов у них свои.</summary>
        public static void Reset()
        {
            Blocks.Clear();
        }

        /// <summary>
        /// Габариты объекта. false — объект их не знает (пустой блок, незагруженная
        /// внешняя ссылка): такой не влияет ни на отбор, ни на раскладку.
        /// </summary>
        public static bool TryGet(Entity ent, out Extents3d ext)
        {
            ext = new Extents3d();

            // Прямая и луч бесконечны: габаритов у них, по сути, нет. Мерить по ним
            // нельзя — одна забытая вспомогательная линия растянула бы и рамку участка,
            // и клетку в раскладке на всю модель. Сам объект при этом переносится как есть.
            if (ent is Xline || ent is Ray) return false;

            try { ext = ent.GeometricExtents; }
            catch (System.Exception) { return false; }

            var dimension = ent as Dimension;
            if (dimension != null) return DimensionBounds(dimension, ref ext);

            var insert = ent as BlockReference;
            if (insert != null) return BlockBounds(insert, ref ext);

            var mtext = ent as MText;
            if (mtext == null) return true;

            double width = ext.MaxPoint.X - ext.MinPoint.X;
            double height = ext.MaxPoint.Y - ext.MinPoint.Y;

            double actualWidth, actualHeight;
            try { actualWidth = mtext.ActualWidth; actualHeight = mtext.ActualHeight; }
            catch (System.Exception) { return true; }

            if (actualWidth <= 0.0 || actualHeight <= 0.0) return true;
            if (width <= actualWidth * TrustFactor && height <= actualHeight * TrustFactor) return true;

            // Точка вставки — угол или середина текста, смотря по выравниванию, поэтому
            // прямоугольник берётся с запасом в обе стороны: он всё равно в тысячи раз
            // меньше того, что отдал AutoCAD, и надёжно накрывает саму надпись.
            Point3d at = mtext.Location;
            ext = new Extents3d(
                new Point3d(at.X - actualWidth, at.Y - actualHeight, at.Z),
                new Point3d(at.X + actualWidth, at.Y + actualHeight, at.Z));
            return true;
        }

        /// <summary>
        /// Габариты вставки блока — по тому, что в блоке нарисовано. Собственный габаритный
        /// контейнер вставки наследует враньё изнутри: одна кривая надпись в описании блока
        /// растягивает каждую его вставку на километры.
        /// </summary>
        static bool BlockBounds(BlockReference insert, ref Extents3d ext)
        {
            if (insert.BlockTableRecord.IsNull || insert.Database == null) return true;

            try
            {
                Extents3d? local = LocalBounds(insert.BlockTableRecord, insert.Database);
                if (!local.HasValue) return true;

                Extents3d box = local.Value;
                var drawn = new Extents3d(box.MinPoint, box.MaxPoint);
                drawn.TransformBy(insert.BlockTransform);

                // Атрибуты живут в самой вставке, а не в описании блока.
                using (Transaction tr = insert.Database.TransactionManager.StartTransaction())
                {
                    foreach (ObjectId id in insert.AttributeCollection)
                    {
                        var attribute = tr.GetObject(id, OpenMode.ForRead) as Entity;
                        if (attribute == null) continue;

                        Extents3d part;
                        if (!TryGet(attribute, out part)) continue;
                        drawn.AddExtents(part);
                    }
                    tr.Commit();
                }
                ext = drawn;
            }
            catch (System.Exception)
            {
                // описание блока не прочитать — остаёмся с тем, что дал AutoCAD
            }
            return true;
        }

        /// <summary>Габариты содержимого блока в его собственных координатах.</summary>
        static Extents3d? LocalBounds(ObjectId blockId, Database db)
        {
            Extents3d? known;
            if (Blocks.TryGetValue(blockId, out known)) return known;

            Extents3d? total = null;
            Blocks[blockId] = null;    // от зацикливания: блок внутри самого себя

            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                var block = tr.GetObject(blockId, OpenMode.ForRead) as BlockTableRecord;
                if (block != null && !block.IsFromExternalReference && !block.IsFromOverlayReference)
                {
                    foreach (ObjectId id in block)
                    {
                        var piece = tr.GetObject(id, OpenMode.ForRead) as Entity;
                        if (piece == null || piece is AttributeDefinition) continue;

                        Extents3d part;
                        if (!TryGet(piece, out part)) continue;

                        if (total.HasValue) { Extents3d t = total.Value; t.AddExtents(part); total = t; }
                        else total = part;
                    }
                }
                tr.Commit();
            }

            Blocks[blockId] = total;
            return total;
        }

        /// <summary>
        /// Габариты размера — по блоку, которым он нарисован. Именно этот блок и виден
        /// на чертеже, а вот собственный габаритный контейнер размера бывает завиральным:
        /// попадается «250x4512446» у выноски с надписью «100».
        /// </summary>
        static bool DimensionBounds(Dimension dimension, ref Extents3d ext)
        {
            ObjectId blockId = dimension.DimBlockId;
            if (blockId.IsNull || dimension.Database == null) return true;

            try
            {
                Extents3d? drawn = null;
                using (Transaction tr = dimension.Database.TransactionManager.StartTransaction())
                {
                    var block = tr.GetObject(blockId, OpenMode.ForRead) as BlockTableRecord;
                    if (block == null) { tr.Commit(); return true; }

                    foreach (ObjectId id in block)
                    {
                        var piece = tr.GetObject(id, OpenMode.ForRead) as Entity;
                        if (piece == null) continue;

                        Extents3d part;
                        if (!TryGet(piece, out part)) continue;

                        if (drawn.HasValue) { Extents3d t = drawn.Value; t.AddExtents(part); drawn = t; }
                        else drawn = part;
                    }
                    tr.Commit();
                }
                if (!drawn.HasValue) return true;

                Vector3d at = dimension.DimBlockPosition.GetAsVector();
                Extents3d real = drawn.Value;
                var picture = new Extents3d(real.MinPoint + at, real.MaxPoint + at);

                // Картинка размера обязана лежать там же, где его надпись. После «Вставить
                // как блок» картинка остаётся в старых мировых координатах, и простым
                // сдвигом её на место не вернуть: AutoCAD переносит её по-своему. Мерить
                // по ней — значит увести размер за десятки километров и растянуть
                // за ним и блок, и участок.
                if (Near(picture, dimension.TextPosition)) { ext = picture; return true; }

                double width = picture.MaxPoint.X - picture.MinPoint.X;
                double height = picture.MaxPoint.Y - picture.MinPoint.Y;

                // Тогда верим AutoCAD — если и его габарит не врёт: стоит у надписи
                // и не раздут против картинки.
                if (Near(ext, dimension.TextPosition) &&
                    ext.MaxPoint.X - ext.MinPoint.X <= Math.Max(width, 1e-6) * TrustFactor &&
                    ext.MaxPoint.Y - ext.MinPoint.Y <= Math.Max(height, 1e-6) * TrustFactor)
                    return true;

                // Не верим никому — прямоугольник размером с картинку вокруг надписи.
                Point3d text = dimension.TextPosition;
                ext = new Extents3d(
                    new Point3d(text.X - width, text.Y - height, text.Z),
                    new Point3d(text.X + width, text.Y + height, text.Z));
            }
            catch (System.Exception)
            {
                // блок размера не прочитать — остаёмся с тем, что дал AutoCAD
            }
            return true;
        }

        /// <summary>
        /// Лежит ли точка у прямоугольника — внутри или рядом, не дальше его же размера.
        /// Надпись размера бывает вынесена в сторону, но не за километры.
        /// </summary>
        static bool Near(Extents3d box, Point3d point)
        {
            double width = box.MaxPoint.X - box.MinPoint.X;
            double height = box.MaxPoint.Y - box.MinPoint.Y;
            double reach = Math.Max(Math.Max(width, height), 1e-6);

            return point.X >= box.MinPoint.X - reach && point.X <= box.MaxPoint.X + reach &&
                   point.Y >= box.MinPoint.Y - reach && point.Y <= box.MaxPoint.Y + reach;
        }
    }
}
