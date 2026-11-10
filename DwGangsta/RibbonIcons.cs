using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DwGangsta
{
    /// <summary>
    /// Значки кнопок: один чертёж расходится на три (разделение) и три сходятся в один (сборка).
    /// Лист рисуется как альбомный формат с рамкой и штампом в правом нижнем углу,
    /// чтобы читался именно чертёж, а не тетрадный листок. Объём даётся по-старому:
    /// градиент бумаги, глянцевый блик сверху и мягкая тень снизу.
    /// </summary>
    public static class RibbonIcons
    {
        // Лист бумаги: сверху почти белый, снизу с холодным оттенком.
        static readonly Brush PaperFill = new LinearGradientBrush(
            Color.FromRgb(0xFF, 0xFF, 0xFF), Color.FromRgb(0xC4, 0xD1, 0xDE), 90);
        static readonly Pen PaperEdge = new Pen(new SolidColorBrush(Color.FromRgb(0x5F, 0x6E, 0x7B)), 0.8);

        // Зелёная стрелка.
        static readonly Brush ArrowFill = new LinearGradientBrush(
            Color.FromRgb(0xB8, 0xF2, 0x7A), Color.FromRgb(0x27, 0x87, 0x16), 90);
        static readonly Pen ArrowEdge = new Pen(new SolidColorBrush(Color.FromRgb(0x1F, 0x5E, 0x12)), 0.8);

        // Линии чертежа: рамка формата и штамп.
        static readonly Pen FramePen = new Pen(new SolidColorBrush(Color.FromRgb(0x22, 0x33, 0x45)), 0.65);
        static readonly Pen StampPen = new Pen(new SolidColorBrush(Color.FromRgb(0x22, 0x33, 0x45)), 0.5);
        static readonly Brush StampFill = new SolidColorBrush(Color.FromArgb(0x55, 0x4A, 0x90, 0xD9));

        static readonly Brush Shadow = new SolidColorBrush(Color.FromArgb(0x55, 0x0E, 0x16, 0x1E));
        static readonly Brush Gloss = new LinearGradientBrush(
            Color.FromArgb(0x8A, 0xFF, 0xFF, 0xFF), Color.FromArgb(0x06, 0xFF, 0xFF, 0xFF), 90);

        /// <summary>Разделение: один чертёж слева, три листа справа.</summary>
        public static ImageSource Create(int size)
        {
            return Render(size, false);
        }

        /// <summary>Сборка: три чертежа слева, один лист справа.</summary>
        public static ImageSource CreateMerge(int size)
        {
            return Render(size, true);
        }

        static ImageSource Render(int size, bool merge)
        {
            var visual = new DrawingVisual();
            using (DrawingContext dc = visual.RenderOpen())
            {
                // Рисуем в сетке 32x32 и масштабируем под нужный размер.
                dc.PushTransform(new ScaleTransform(size / 32.0, size / 32.0));
                Draw(dc, merge, size >= 24);
                dc.Pop();
            }

            var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(visual);
            bitmap.Freeze();
            return bitmap;
        }

        static void Draw(DrawingContext dc, bool merge, bool detailed)
        {
            if (!detailed) { DrawSmall(dc, merge); return; }

            // Листы занимают почти всё поле значка: поля по краям минимальные, а стрелка
            // сжата до узкой перемычки — иначе рамку и штамп не разглядеть.
            double singleX = merge ? 17.6 : 0.5;
            DrawSheet(dc, new Rect(singleX, 10.4, 13.2, 10.4), true);

            // Стопка из трёх листов — на противоположной стороне.
            double stackX = merge ? 0.5 : 19.4;
            for (int i = 0; i < 3; i++)
                DrawSheet(dc, new Rect(stackX + i * 1.0, 5.6 + i * 5.9, 9.9, 7.4), false);

            // Стрелка всегда указывает на результат — вправо.
            DrawArrow(dc, merge ? 13.1 : 14.4, merge ? 17.2 : 18.8);
        }

        /// <summary>
        /// Мелкий значок: три листа в 16 пикселях сливаются в кашу, поэтому оставляем два
        /// и рисуем всё крупнее — смысл «один против нескольких» сохраняется.
        /// </summary>
        static void DrawSmall(DrawingContext dc, bool merge)
        {
            double singleX = merge ? 16.6 : 0.5;
            DrawSheet(dc, new Rect(singleX, 9.8, 14.8, 11.6), true);

            double stackX = merge ? 0.5 : 17.0;
            for (int i = 0; i < 2; i++)
                DrawSheet(dc, new Rect(stackX + i * 1.5, 5.6 + i * 9.4, 13.0, 9.6), false);

            DrawArrow(dc, merge ? 15.2 : 15.6, merge ? 16.4 : 16.8);
        }

        /// <summary>
        /// Лист формата с рамкой и штампом. При <paramref name="detailed"/> внутри штампа
        /// прорисовываются графы — на мелких листах они всё равно не читаются.
        /// </summary>
        static void DrawSheet(DrawingContext dc, Rect rect, bool detailed)
        {
            // Мягкая тень со смещением вправо-вниз.
            dc.PushTransform(new TranslateTransform(0.5, 0.5));
            dc.DrawRectangle(Shadow, null, rect);
            dc.Pop();

            dc.DrawRectangle(PaperFill, PaperEdge, rect);

            // Рамка чертежа: слева широкое поле под подшивку, с остальных сторон узкое.
            double side = Math.Max(rect.Height * 0.10, 0.55);
            double bind = Math.Max(rect.Width * 0.13, 1.0);
            var frame = new Rect(rect.X + bind, rect.Y + side,
                                 rect.Width - bind - side, rect.Height - side * 2);
            if (frame.Width <= 0.6 || frame.Height <= 0.6) return;

            dc.DrawRectangle(null, FramePen, frame);

            // Штамп в правом нижнем углу рамки.
            double stampWidth = frame.Width * 0.48;
            double stampHeight = Math.Max(frame.Height * 0.26, 0.9);
            var stamp = new Rect(frame.Right - stampWidth, frame.Bottom - stampHeight,
                                 stampWidth, stampHeight);
            dc.DrawRectangle(StampFill, StampPen, stamp);

            if (detailed)
            {
                // Пара граф внутри штампа — чтобы угадывалась основная надпись.
                dc.DrawLine(StampPen,
                    new Point(stamp.X, stamp.Y + stamp.Height / 2),
                    new Point(stamp.Right, stamp.Y + stamp.Height / 2));
                dc.DrawLine(StampPen,
                    new Point(stamp.X + stamp.Width * 0.42, stamp.Y),
                    new Point(stamp.X + stamp.Width * 0.42, stamp.Bottom));
            }

            // Выпуклый глянец по верху — характерный блик тех самых иконок.
            dc.PushClip(new RectangleGeometry(rect));
            var glare = new StreamGeometry();
            using (StreamGeometryContext ctx = glare.Open())
            {
                ctx.BeginFigure(new Point(rect.X, rect.Y), true, true);
                ctx.LineTo(new Point(rect.Right, rect.Y), true, true);
                ctx.LineTo(new Point(rect.Right, rect.Y + rect.Height * 0.28), true, true);
                ctx.QuadraticBezierTo(new Point(rect.X + rect.Width / 2, rect.Y + rect.Height * 0.60),
                                      new Point(rect.X, rect.Y + rect.Height * 0.28), true, true);
            }
            glare.Freeze();
            dc.DrawGeometry(Gloss, null, glare);
            dc.Pop();
        }

        // ------------------------------------------------- планета в каске
        //
        // «Подсветить видовые экраны»: пиксельная планета в строительной каске.
        // Рисуется по пикселям сетки 16 или 32 — как старые игровые спрайты:
        // шар с океаном и материками, свет слева сверху, тень штрихуется
        // шахматкой с чёрным, вокруг — контур в пиксель.

        static readonly Color Black = Color.FromRgb(0x00, 0x00, 0x00);
        static readonly Color White = Color.FromRgb(0xFF, 0xFF, 0xFF);
        static readonly Color Ocean = Color.FromRgb(0x00, 0xB4, 0xFF);
        static readonly Color Land = Color.FromRgb(0x00, 0xD0, 0x00);
        // Каска белая, как у инженера; гребень и тени — светло-серые, блик — чисто белый.
        static readonly Color Hat = Color.FromRgb(0xE8, 0xEC, 0xF0);
        static readonly Color HatShade = Color.FromRgb(0xA4, 0xAE, 0xBC);

        /// <summary>Планета в каске — значок подсветки видовых экранов.</summary>
        public static ImageSource CreateHighlight(int size)
        {
            // Картинка Icons\Highlight*.png (планета в каске с козырьком назад) важнее
            // спрайта; нет файла — спрайт.
            ImageSource png = ZoomZoom.RibbonIcons.Picture("Highlight", size);
            if (png != null)
                return png;

            int n = size <= 16 ? 16 : 32;
            Color?[,] grid = PlanetSprite(n);
            return Pixels(grid, n, size);
        }

        static Color?[,] PlanetSprite(int n)
        {
            var grid = new Color?[n, n];
            var helmet = new bool[n, n];
            double tilt = -12 * Math.PI / 180;           // каска чуть набекрень
            double cos = Math.Cos(tilt), sin = Math.Sin(tilt);

            for (int py = 0; py < n; py++)
                for (int px = 0; px < n; px++)
                {
                    double x = (px + 0.5) / n, y = (py + 0.5) / n;
                    bool odd = ((px + py) & 1) == 1;

                    // Каска — в повёрнутых координатах.
                    double hx = 0.5 + (x - 0.5) * cos - (y - 0.5) * sin;
                    double hy = 0.5 + (x - 0.5) * sin + (y - 0.5) * cos;
                    const double cx = 0.47, cy = 0.42;
                    double dx = (hx - cx) / 0.24, dy = (hy - cy) / 0.25;
                    bool dome = hy <= cy && dx * dx + dy * dy <= 1;
                    double bx = (hx - cx) / 0.37, by = (hy - cy - 0.01) / 0.07;
                    bool brim = bx * bx + by * by <= 1;

                    if (dome || brim)
                    {
                        helmet[px, py] = true;
                        Color c = Hat;
                        if (dome && Math.Abs(hx - cx) < 0.04) c = HatShade;          // гребень
                        else if (dome && dx < -0.35 && dy < -0.35) c = odd ? White : Hat;   // блик
                        else if (dome && dx > 0.45) c = odd ? HatShade : Hat;         // тень справа
                        else if (brim && !dome && hy > cy + 0.03) c = HatShade;       // низ полей
                        grid[px, py] = c;
                        continue;
                    }

                    // Планета.
                    double ux = (x - 0.5) / 0.35, uy = (y - 0.615) / 0.35;
                    double r2 = ux * ux + uy * uy;
                    if (r2 > 1) continue;

                    double nz = Math.Sqrt(1 - r2);
                    double light = (-0.55 * ux - 0.6 * uy + 0.58 * nz);
                    bool land = Math.Sin(ux * 5.5 + 1.2) * Math.Cos(uy * 4.5 - 0.4) +
                                0.6 * Math.Sin((ux - uy) * 6.0) > 0.35;
                    Color body = land ? Land : (light > 0.75 && odd ? White : Ocean);

                    if (light < -0.3) grid[px, py] = Black;
                    else if (light < 0.1) grid[px, py] = odd ? Black : body;
                    else grid[px, py] = body;
                }

            // Поля каски отделены от планеты чёрной чертой — так каска «сидит».
            for (int py = 1; py < n; py++)
                for (int px = 0; px < n; px++)
                    if (grid[px, py] != null && !helmet[px, py] && helmet[px, py - 1])
                        grid[px, py] = Black;

            // Контур в пиксель снаружи.
            var outline = (Color?[,])grid.Clone();
            for (int py = 0; py < n; py++)
                for (int px = 0; px < n; px++)
                {
                    if (grid[px, py] != null) continue;
                    if ((px > 0 && grid[px - 1, py] != null) || (px < n - 1 && grid[px + 1, py] != null) ||
                        (py > 0 && grid[px, py - 1] != null) || (py < n - 1 && grid[px, py + 1] != null))
                        outline[px, py] = Black;
                }
            return outline;
        }

        /// <summary>Спрайт n×n в картинку size×size: каждый пиксель — целым квадратом, без размытия.</summary>
        static ImageSource Pixels(Color?[,] grid, int n, int size)
        {
            int k = Math.Max(1, size / n);
            int side = n * k;
            var bgra = new byte[side * side * 4];
            for (int py = 0; py < side; py++)
                for (int px = 0; px < side; px++)
                {
                    Color? c = grid[px / k, py / k];
                    if (c == null) continue;
                    int i = (py * side + px) * 4;
                    bgra[i] = c.Value.B; bgra[i + 1] = c.Value.G; bgra[i + 2] = c.Value.R; bgra[i + 3] = 255;
                }
            // DPI подобран так, чтобы картинка занимала ровно size логических точек.
            double dpi = 96.0 * side / size;
            BitmapSource bmp = BitmapSource.Create(side, side, dpi, dpi, PixelFormats.Bgra32, null, bgra, side * 4);
            bmp.Freeze();
            return bmp;
        }

        // ------------------------------------------------- радиотелескоп
        //
        // «Передвинуть с видовым»: квадрат ночного неба, в правом нижнем углу —
        // радиотелескоп, от тарелки в небо уходят сигналы-перекрестья.
        // Звёзды и перекрестья стоят ровно в пикселях: на 16 px шаг сетки — 2.

        /// <summary>Радиотелескоп под звёздами — значок переноса вместе с видовыми экранами.</summary>
        public static ImageSource CreateMoveViews(int size)
        {
            // Картинка Icons\MoveViews*.png (тарелка шлёт белые знаки «Перенести»; как её
            // сделать — Icons\README.txt) важнее рисунка ниже; нет файла — рисунок.
            ImageSource png = ZoomZoom.RibbonIcons.Picture("MoveViews", size);
            if (png != null)
                return png;

            var visual = new DrawingVisual();
            using (DrawingContext dc = visual.RenderOpen())
            {
                dc.PushTransform(new ScaleTransform(size / 32.0, size / 32.0));
                DrawTelescope(dc, size <= 16 ? 2.0 : 1.0);
                dc.Pop();
            }
            var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(visual);
            bitmap.Freeze();
            return bitmap;
        }

        static void DrawTelescope(DrawingContext dc, double px)
        {
            // Небо в рамке.
            var sky = new LinearGradientBrush(Color.FromRgb(0x03, 0x04, 0x0C), Color.FromRgb(0x1C, 0x1E, 0x40), 90);
            sky.Freeze();
            var frame = new Pen(new SolidColorBrush(Color.FromRgb(0x6A, 0x6C, 0x74)), px);
            dc.DrawRectangle(sky, frame, new Rect(px / 2, px / 2, 32 - px, 32 - px));

            // Звёзды.
            var star = new SolidColorBrush(Color.FromRgb(0xC8, 0xCC, 0xD8));
            int[] stars = px > 1
                ? new[] { 4, 4, 12, 6, 24, 4, 4, 14, 18, 12, 26, 10, 8, 24 }
                : new[] { 5, 4, 11, 7, 20, 3, 27, 6, 4, 12, 15, 13, 24, 11, 9, 18, 3, 25, 19, 8 };
            for (int i = 0; i < stars.Length; i += 2)
                dc.DrawRectangle(star, null, new Rect(stars[i], stars[i + 1], px, px));

            // Сигналы: перекрестья от тарелки в небо, дальше — крупнее и бледнее.
            var signals = px > 1
                ? new[] { new { X = 14.0, Y = 14.0, Arm = 1, A = 255 }, new { X = 8.0, Y = 8.0, Arm = 2, A = 200 } }
                : new[]
                {
                    new { X = 15.0, Y = 15.0, Arm = 1, A = 255 }, new { X = 11.0, Y = 11.0, Arm = 2, A = 230 },
                    new { X = 6.0, Y = 6.0, Arm = 3, A = 190 },
                };
            foreach (var s in signals)
            {
                var arm = new SolidColorBrush(Color.FromArgb((byte)s.A, 0x7F, 0xDF, 0xFF));
                double a = s.Arm * px;
                dc.DrawRectangle(arm, null, new Rect(s.X - a, s.Y, a * 2 + px, px));   // влево-вправо
                dc.DrawRectangle(arm, null, new Rect(s.X, s.Y - a, px, a * 2 + px));   // вверх-вниз
                dc.DrawRectangle(Brushes.White, null, new Rect(s.X, s.Y, px, px));
            }

            // Опора: стойка и треногa.
            var steel = new Pen(new SolidColorBrush(Color.FromRgb(0xB8, 0xC2, 0xD0)), 1.6)
                { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            dc.DrawLine(steel, new Point(24.5, 24.5), new Point(26.5, 28.5));
            dc.DrawLine(steel, new Point(26.5, 28.5), new Point(23.0, 30.6));
            dc.DrawLine(steel, new Point(26.5, 28.5), new Point(30.0, 30.6));

            // Тарелка смотрит влево вверх, в небо.
            var dishFill = new LinearGradientBrush(Color.FromRgb(0xFF, 0xFF, 0xFF), Color.FromRgb(0x8A, 0x9C, 0xB8),
                                                   new Point(0, 0), new Point(1, 1));
            dishFill.Freeze();
            var rim = new Pen(new SolidColorBrush(Color.FromRgb(0x10, 0x14, 0x1C)), 1.0);
            dc.PushTransform(new RotateTransform(-45, 22, 22));
            dc.DrawEllipse(dishFill, rim, new Point(22, 22), 7.6, 3.4);
            var bowl = new SolidColorBrush(Color.FromArgb(0x40, 0x30, 0x3C, 0x52));
            dc.DrawEllipse(bowl, null, new Point(21.6, 21.4), 5.6, 2.0);
            dc.Pop();

            // Облучатель: две растяжки от краёв тарелки к фокусу.
            var strut = new Pen(new SolidColorBrush(Color.FromRgb(0xF0, 0xF4, 0xFA)), 1.0);
            var focus = new Point(17.3, 17.3);
            dc.DrawLine(strut, new Point(16.9, 27.1), focus);
            dc.DrawLine(strut, new Point(27.1, 16.9), focus);
            dc.DrawEllipse(Brushes.White, rim, focus, 1.1, 1.1);
        }

        /// <summary>Зелёная объёмная стрелка вправо.</summary>
        static void DrawArrow(DrawingContext dc, double x0, double x1)
        {
            double tail = x1 - Math.Min(2.9, (x1 - x0) * 0.72);
            var arrow = new StreamGeometry();
            using (StreamGeometryContext ctx = arrow.Open())
            {
                ctx.BeginFigure(new Point(x0, 14.5), true, true);
                ctx.LineTo(new Point(tail, 14.5), true, true);
                ctx.LineTo(new Point(tail, 12.2), true, true);
                ctx.LineTo(new Point(x1, 16.0), true, true);
                ctx.LineTo(new Point(tail, 19.8), true, true);
                ctx.LineTo(new Point(tail, 17.5), true, true);
                ctx.LineTo(new Point(x0, 17.5), true, true);
            }
            arrow.Freeze();

            dc.PushTransform(new TranslateTransform(0.5, 0.5));
            dc.DrawGeometry(Shadow, null, arrow);
            dc.Pop();

            dc.DrawGeometry(ArrowFill, ArrowEdge, arrow);

            dc.PushClip(arrow);
            Rect bounds = arrow.Bounds;
            dc.DrawRectangle(Gloss, null,
                new Rect(bounds.X, bounds.Y, bounds.Width, bounds.Height * 0.45));
            dc.Pop();
        }
    }
}
