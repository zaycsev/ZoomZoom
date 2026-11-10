using System;
using System.Collections.Generic;
using System.IO;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

namespace DwGangsta
{
    public static partial class TableLayout
    {
        /// <summary>Размеры участка: место, которое он займёт, и рамка вокруг чертежа.</summary>
        public class Size
        {
            /// <summary>Клетка: вся геометрия вместе с кадрами видовых экранов.</summary>
            public Extents3d Cell;

            /// <summary>Рамка: сам чертёж. По ней участки и выравниваются друг с другом.</summary>
            public Extents3d Frame;
        }

        /// <summary>
        /// Считает сдвиги. Участки выравниваются рамками: левые края рамок одной колонки
        /// стоят на одной вертикали, верхние края рамок одной строки — на одной горизонтали.
        /// Клетка шире рамки — на этот выступ строки и колонки раздвигаются, чтобы участки
        /// всё равно не наложились.
        /// </summary>
        public static Dictionary<string, Vector3d> Offsets(List<TableCell> cells,
                                                           Dictionary<string, Size> sizes,
                                                           double gapFactor)
        {
            var offsets = new Dictionary<string, Vector3d>(StringComparer.OrdinalIgnoreCase);

            int columns = 0, rows = 0;
            foreach (TableCell cell in cells)
            {
                if (cell.Column + 1 > columns) columns = cell.Column + 1;
                if (cell.Row + 1 > rows) rows = cell.Row + 1;
            }
            if (columns == 0 || rows == 0) return offsets;

            // Насколько клетка выступает за рамку в каждую сторону.
            var leftOut = new double[columns];
            var rightOut = new double[columns];
            var upOut = new double[rows];
            var downOut = new double[rows];

            foreach (TableCell cell in cells)
            {
                Size size;
                if (!sizes.TryGetValue(cell.File, out size)) continue;

                Keep(leftOut, cell.Column, size.Frame.MinPoint.X - size.Cell.MinPoint.X);
                Keep(rightOut, cell.Column, size.Cell.MaxPoint.X - size.Frame.MinPoint.X);
                Keep(upOut, cell.Row, size.Cell.MaxPoint.Y - size.Frame.MaxPoint.Y);
                Keep(downOut, cell.Row, size.Frame.MaxPoint.Y - size.Cell.MinPoint.Y);
            }

            double gap = gapFactor > 0 ? gapFactor : 0.0;

            var x = new double[columns];
            for (int c = 1; c < columns; c++)
            {
                double step = rightOut[c - 1] + leftOut[c];
                x[c] = x[c - 1] + step + Math.Max(step * gap, 1.0);
            }

            var y = new double[rows];
            for (int r = 1; r < rows; r++)
            {
                double step = downOut[r - 1] + upOut[r];
                y[r] = y[r - 1] - step - Math.Max(step * gap, 1.0);
            }

            foreach (TableCell cell in cells)
            {
                Size size;
                if (!sizes.TryGetValue(cell.File, out size)) continue;

                offsets[cell.File] = new Vector3d(x[cell.Column] - size.Frame.MinPoint.X,
                                                  y[cell.Row] - size.Frame.MaxPoint.Y, 0.0);
            }
            return offsets;
        }

        static void Keep(double[] values, int at, double candidate)
        {
            if (candidate > values[at]) values[at] = candidate;
        }
    }
}
