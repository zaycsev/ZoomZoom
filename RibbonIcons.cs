using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

// Значки ленты. Здесь нет ни одной ссылки на AutoCAD — только WPF: так значки
// можно отрисовать в картинки и посмотреть без запуска AutoCAD.

namespace ZoomZoom
{
    internal enum IconKind { Whole, Frame, Settings, Ref }

    internal static class RibbonIcons
    {
        /// <summary>
        /// Значки рисуются кодом, чтобы не таскать за сборкой файлы картинок.
        ///
        /// Подложки нет: на ленте AutoCAD плитка со скруглением смотрелась бы
        /// иконкой с телефона, поэтому фон прозрачный, а знак лежит прямо на
        /// панели и занимает всё поле.
        ///
        /// Материал — титан: диагональный градиент от почти белого блика через
        /// холодный серый к тёмной кромке. Такой размах по светлоте нужен, чтобы
        /// знак читался и на тёмной теме AutoCAD, и на светлой: на тёмной вытягивает
        /// блик, на светлой — тёмная кромка и обводка.
        ///
        /// «Настройки» — скрещённые молотки, старый маркшейдерский знак: как раз
        /// про то, чем плагин занят. «Весь чертёж» — сплошная рамка с ужавшимся
        /// квадратом внутри, «Рамка» — она же пунктиром, как рамка выделения.
        ///
        /// На 16 px тени не рисуются: на таком размере они только замыливают знак.
        /// </summary>
        internal static ImageSource Icon(int size, IconKind kind)
        {
            // Картинка из Icons\*.png, вшитая в сборку, важнее нарисованной:
            // её можно поправить в любом редакторе и просто пересобрать.
            ImageSource png = Picture(kind.ToString(), size);
            if (png != null)
                return png;

            var visual = new DrawingVisual();
            using (DrawingContext dc = visual.RenderOpen())
            {
                // Прозрачный прямоугольник задаёт границы значка: без него WPF
                // обрежет картинку по нарисованному, и знак поедет в кнопке.
                dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, size, size));

                if (kind == IconKind.Settings)
                    DrawHammers(dc, size);
                else if (kind == IconKind.Whole)
                    DrawWhole(dc, size, WholeStyle);
                else
                    DrawFrame(dc, size, FrameStyle);
            }

            // Рисуем в 4 раза плотнее, с DPI 384: логический размер остаётся
            // size, а на мониторах с масштабом 150–400% значок не мылится —
            // как у значка openkartogramma, проверено там на лентах 2015–2026.
            const int Supersample = 4;
            var bmp = new RenderTargetBitmap(size * Supersample, size * Supersample,
                                             96 * Supersample, 96 * Supersample, PixelFormats.Pbgra32);
            bmp.Render(visual);
            bmp.Freeze();
            return bmp;
        }

        /// <summary>
        /// Значок из вшитого PNG (ресурс ZoomZoom.Icons.&lt;name&gt;.png) или null,
        /// если такого нет. Пиксели не пересчитываем: картинке лишь ставится
        /// DPI, при котором она занимает ровно size логических точек, а
        /// масштабирует уже сама лента — так 48-пиксельный рисунок на 32-точечной
        /// кнопке монитора с масштабом 150% выводится без потерь.
        /// </summary>
        internal static ImageSource Picture(string name, int size)
        {
            try
            {
                // Есть заранее уменьшенная под этот размер (Icons\<name>_32.png,
                // _16.png — вдвое крупнее кнопки) — берём её: лента ужимает
                // картинку грубо, и мелкие детали из 256 px в 32 рассыпаются.
                var asm = typeof(RibbonIcons).Assembly;
                var sized = asm.GetManifestResourceStream("ZoomZoom.Icons." + name + "_" + size + ".png");
                using (var stream = sized ?? asm.GetManifestResourceStream("ZoomZoom.Icons." + name + ".png"))
                {
                    if (stream == null)
                        return null;
                    var decoder = new PngBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat,
                                                       BitmapCacheOption.OnLoad);
                    BitmapSource src = decoder.Frames[0];
                    if (src.Format != PixelFormats.Bgra32 && src.Format != PixelFormats.Pbgra32)
                        src = new FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0);

                    int stride = src.PixelWidth * 4;
                    var pixels = new byte[stride * src.PixelHeight];
                    src.CopyPixels(pixels, stride, 0);
                    double dpi = 96.0 * src.PixelWidth / size;
                    BitmapSource result = BitmapSource.Create(src.PixelWidth, src.PixelHeight, dpi,
                        96.0 * src.PixelHeight / size, src.Format, null, pixels, stride);
                    result.Freeze();
                    return result;
                }
            }
            catch (Exception)
            {
                return null;      // битый файл — значок нарисуется кодом
            }
        }

        // Варианты значков. Прежние не удаляются — переключаются здесь.
        // Действуют, только если в сборке нет картинки Icons\Whole.png / Frame.png.
        //   «Весь чертёж»: 0 — рамка с ужавшимся квадратом, 1 — чемодан,
        //                   2 — палитра масляных красок с кистью,
        //                   3 — силуэт палитры из vescherchosh.svg, отражённый:
        //                       угол обращён в сторону, противоположную кирпичу.
        //   «Рамка»:       0 — пунктирная рамка выделения, 1 — кирпич со шпателем,
        //                   2 — силуэт кирпича со шпателем из ramka.svg,
        //                   3 — кирпич с кельмой в изометрии openkartogramma.
        internal static int WholeStyle = 3;
        internal static int FrameStyle = 3;

        internal static void DrawWhole(DrawingContext dc, int size, int style)
        {
            if (style == 1) DrawSuitcase(dc, size);
            else if (style == 2) DrawPalette(dc, size);
            else if (style == 3) DrawSilhouette(dc, size, WholePaths, true);
            else DrawSheet(dc, size, false);
        }

        internal static void DrawFrame(DrawingContext dc, int size, int style)
        {
            if (style == 1) DrawBrick(dc, size);
            else if (style == 2) DrawSilhouette(dc, size, FramePaths, false);
            else if (style == 3) DrawBrickKelma(dc, size);
            else DrawSheet(dc, size, true);
        }

        // -------------------------------------------- кирпич с кельмой
        //
        // Изометрия та же, что у значка openkartogramma: на сетке 32 px шаг
        // по длине кирпича (3.4, 1.5) — вправо-вниз, по ширине (−2.8, 1.5) —
        // влево-вниз. Оси чуть разного наклона, поэтому кирпич стоит не строго
        // симметрично, а немного повёрнутым — ровно как плоскость картограммы.
        // Высота откладывается вертикально, без сокращения.
        //
        // Всё — титаном с чёткой тёмной кромкой в пиксель: грани разной
        // светлоты (верх приглушён, чтобы на нём читалась светлая кельма,
        // торец темнее всего), кельма лежит полотном на
        // кирпиче остриём к дальнему углу, ручка поднимается вверх вправо.

        private static readonly Vector KartoAlong = new Vector(3.4, 1.5);
        private static readonly Vector KartoAcross = new Vector(-2.8, 1.5);

        private static Point Karto(double x, double y, double z)
        {
            return new Point(0, 0) + KartoAlong * x + KartoAcross * y + new Vector(0, -z);
        }

        private static void DrawBrickKelma(DrawingContext dc, int size)
        {
            const double L = 5.0, W = 2.5, H = 7.0;
            Func<double, double, double, Point> P = Karto;

            Geometry top = Poly(P(0, 0, H), P(L, 0, H), P(L, W, H), P(0, W, H));
            Geometry front = Poly(P(0, W, 0), P(L, W, 0), P(L, W, H), P(0, W, H));   // длинная грань, к зрителю слева
            Geometry end = Poly(P(L, 0, 0), P(L, W, 0), P(L, W, H), P(L, 0, H));     // торец, справа

            // Кельма: каплевидное полотно остриём к дальнему (левому верхнему)
            // краю, пятка ближе к торцу; шейка и ручка уходят вверх вправо.
            double bz = H + 0.15;
            Geometry blade = Poly(P(0.2, 1.25, bz), P(2.4, 0.25, bz), P(3.45, 0.7, bz),
                                  P(3.6, 1.25, bz), P(3.45, 1.8, bz), P(2.4, 2.25, bz));
            Point neck0 = P(3.3, 1.25, bz + 0.3), neck1 = P(3.8, 1.1, bz + 5.5);
            Point grip0 = P(3.95, 1.05, bz + 6.5), grip1 = P(5.3, 0.6, bz + 16.0);

            var all = new GeometryGroup();
            all.Children.Add(top);
            all.Children.Add(front);
            all.Children.Add(end);
            all.Children.Add(blade);
            Rect box = all.Bounds;
            box.Union(grip1);
            box.Inflate(1.6, 1.6);
            MatrixTransform fit = Fit(size, box);
            double k = fit.Matrix.M11;

            // Кромка — ровно в пиксель на любом размере.
            Pen rim = Stroke(Frozen(Color.FromRgb(0x2B, 0x30, 0x36)), Math.Max(1.0, size / 32.0) / k);
            double px = 1.0 / k;

            Shadow(dc, size, fit, all);

            dc.PushTransform(fit);
            Brush ti = Titanium();
            dc.DrawGeometry(ShadeBrush(ti, -0.18), rim, top);
            dc.DrawGeometry(ShadeBrush(ti, -0.05), rim, front);
            dc.DrawGeometry(ShadeBrush(ti, -0.40), rim, end);

            // Тень кельмы на кирпиче — только на крупном значке.
            if (size >= 24)
            {
                dc.PushTransform(new TranslateTransform(0, 0.9));
                dc.DrawGeometry(Frozen(Color.FromArgb(0x55, 0, 0, 0)), null, blade);
                dc.Pop();
            }
            dc.DrawGeometry(ShadeBrush(ti, 0.45), rim, blade);

            // Шейка и ручка: тёмная подложка шире на пиксель с каждой стороны,
            // сверху титан — так контур чёткий и на светлой ленте.
            double neckW = Math.Max(1.0, size / 22.0) / k, gripW = Math.Max(1.6, size / 13.0) / k;
            dc.DrawLine(RoundPen(rim.Brush, neckW + 2 * px), neck0, neck1);
            dc.DrawLine(RoundPen(ti, neckW), neck0, neck1);
            dc.DrawLine(RoundPen(rim.Brush, gripW + 2 * px), grip0, grip1);
            dc.DrawLine(RoundPen(ShadeBrush(ti, 0.05), gripW), grip0, grip1);
            dc.Pop();
        }

        // ------------------------------------------------------ силуэты из SVG
        //
        // Контуры взяты один в один из исходных рисунков значков (vescherchosh.svg
        // и ramka.svg, 48×48 pt, трассировка potrace). У potrace своя система
        // координат: сотые доли с осью Y вверх, поэтому при разборе каждая
        // точка переводится как x·0.1, 48 − y·0.1 — так же, как это делает
        // transform в самих файлах. Залито тем же титаном, что и молотки, с
        // тёмной кромкой по контуру.

        /// <summary>«Весь чертёж»: vescherchosh.svg.</summary>
        internal static readonly string[] WholePaths =
        {
            "M151 395 c-69 -46 -129 -92 -134 -100 -10 -19 9 -136 27 -159 9 -12 31 2 131 86 153 128 157 130 189 122 22 -5 24 -9 15 -21 -9 -9 -15 -11 -23 -3 -15 15 -46 12 -46 -5 0 -9 7 -18 15 -21 8 -4 12 -11 8 -18 -3 -6 10 4 29 22 31 29 40 33 72 27 20 -4 36 -9 36 -12 0 -2 -35 -44 -77 -93 -75 -88 -78 -90 -121 -90 -24 0 -52 -5 -64 -11 -40 -21 -31 3 23 59 30 31 48 51 40 45 -11 -10 -17 -9 -26 4 -7 10 -19 14 -29 10 -22 -9 -20 -26 5 -35 20 -8 20 -8 1 -25 -17 -16 -20 -16 -25 -2 -6 15 -38 21 -50 9 -11 -11 5 -34 25 -34 10 0 16 -4 13 -10 -8 -13 -48 -13 -61 0 -7 7 -16 6 -29 -3 -21 -16 -12 -37 16 -37 13 0 18 -4 13 -12 -6 -10 -4 -10 7 -1 14 14 149 18 149 5 0 -5 -13 -21 -30 -37 l-29 -28 -73 33 c-100 44 -98 44 -98 28 0 -16 120 -70 163 -73 22 -1 36 8 68 47 l41 48 71 2 72 2 -62 3 c-35 2 -63 6 -63 9 0 2 32 42 70 87 39 45 69 88 68 96 -3 17 -114 53 -140 47 -10 -2 -18 0 -18 6 0 21 -32 118 -38 117 -4 0 -63 -38 -131 -84z m149 2 c11 -35 10 -41 -11 -63 -13 -14 -29 -22 -37 -19 -7 2 -11 1 -8 -4 3 -5 -39 -46 -94 -91 -64 -52 -102 -77 -104 -68 -3 7 -10 37 -16 67 -8 43 -8 57 2 68 28 27 243 162 249 155 4 -4 12 -24 19 -45z",
            "M252 268 c2 -6 13 -17 25 -25 17 -12 22 -11 34 4 9 11 10 14 2 9 -8 -4 -13 -1 -13 8 0 11 -9 16 -26 16 -15 0 -24 -5 -22 -12z",
            "M342 233 c4 -22 43 -25 43 -3 0 8 -11 16 -23 18 -19 3 -23 0 -20 -15z",
            "M292 183 c4 -22 43 -25 43 -3 0 8 -11 16 -23 18 -19 3 -23 0 -20 -15z",
        };

        /// <summary>«Рамка»: ramka.svg.</summary>
        internal static readonly string[] FramePaths =
        {
            "M387 432 c-26 -26 -47 -53 -47 -59 0 -6 -7 -16 -16 -24 -10 -8 -14 -25 -12 -46 2 -31 -1 -36 -37 -53 l-40 -18 33 -12 c17 -7 32 -16 32 -21 0 -5 -3 -8 -7 -7 -19 4 -69 -17 -85 -36 -9 -12 -18 -39 -19 -61 -1 -22 -3 -31 -6 -20 -3 11 -9 24 -13 28 -12 14 -50 -11 -50 -33 0 -10 -5 -22 -10 -25 -5 -3 -10 1 -10 10 0 9 -5 25 -11 36 -10 19 -12 18 -40 -17 -16 -20 -28 -39 -26 -41 3 -2 16 1 31 7 15 5 33 7 39 3 7 -4 25 -8 40 -9 15 -1 36 -9 47 -19 18 -16 66 -16 208 1 l43 5 -3 91 -3 90 -45 17 c-44 16 -45 17 -48 65 -3 49 -3 49 64 110 43 39 65 66 60 74 -11 19 -19 15 -69 -36z",
            "M180 291 c0 -14 30 -41 45 -41 27 0 25 19 -4 35 -35 17 -41 18 -41 6z",
            "M8 208 c3 -53 8 -76 20 -85 25 -18 28 -17 42 27 14 43 34 66 65 73 11 2 6 3 -12 1 -21 -1 -33 3 -37 12 -3 9 -10 12 -16 9 -5 -3 -10 -2 -10 3 0 5 -8 12 -17 16 -10 3 -22 8 -28 11 -6 3 -9 -21 -7 -67z",
        };

        /// <summary>Силуэт из путей potrace: титан, тёмная кромка, тень на крупном значке.</summary>
        internal static void DrawSilhouette(DrawingContext dc, int size, string[] paths, bool mirror)
        {
            // potrace → обычные координаты: x·0.1, 48 − y·0.1 (как transform в файле).
            // mirror — ещё и отражение слева направо: x → 48 − x.
            var flip = mirror
                ? new MatrixTransform(-0.1, 0, 0, -0.1, 48, 48)
                : new MatrixTransform(0.1, 0, 0, -0.1, 0, 48);
            var group = new GeometryGroup { FillRule = FillRule.EvenOdd };
            foreach (string d in paths)
            {
                Geometry g = Geometry.Parse(d).Clone();
                g.Transform = flip;
                group.Children.Add(g);
            }
            group.Freeze();

            Rect box = group.Bounds;
            box.Inflate(1, 1);
            MatrixTransform fit = Fit(size, box);
            double k = fit.Matrix.M11;

            if (size >= 24)
            {
                dc.PushTransform(new TranslateTransform(0, size * 0.04));
                dc.PushTransform(fit);
                dc.DrawGeometry(Frozen(Color.FromArgb(0x4D, 0x00, 0x00, 0x00)), null, group);
                dc.Pop();
                dc.Pop();
            }

            // Кромка — под заливкой, а не поверх: рисунок в основном из тонких
            // линий, и кромка поверх съела бы их целиком. Снизу она лишь
            // выглядывает по краям, а сами линии остаются титановыми.
            dc.PushTransform(fit);
            dc.DrawGeometry(null, Stroke(Frozen(Color.FromArgb(0xC0, 0x2B, 0x30, 0x36)), 2 * Math.Max(0.6, size / 40.0) / k), group);
            dc.DrawGeometry(Titanium(), null, group);
            dc.Pop();
        }

        // ------------------------------------------------- изометрия: палитра, кирпич
        //
        // Оба предмета строятся в мировых координатах (x — на восток, y — на
        // север, z — вверх) и проецируются честной изометрией: оси под 120°,
        // высота без сокращения. Сторона, с которой смотрим, меняет проекцию:
        // палитру видно с юго-запада (к зрителю — южная и западная грани),
        // кирпич — с юго-востока (южная и восточная). Так кисть и шпатель
        // ложатся правильно относительно граней, а не просто «как получилось».
        //
        // Металл — тот же титан, что и на остальных значках; цвет — только там,
        // где он и есть суть предмета: краски, кирпич, дерево.

        private const double Cos30 = 0.8660254;

        private enum View { SouthWest, SouthEast }

        private static Point Iso(View view, double x, double y, double z)
        {
            return view == View.SouthWest
                ? new Point((x - y) * Cos30, -(x + y) * 0.5 - z)
                : new Point((x + y) * Cos30, (x - y) * 0.5 - z);
        }

        private static readonly Brush RimBrush = Frozen(Color.FromArgb(0xC0, 0x2B, 0x30, 0x36));

        /// <summary>Вписывает рисунок с габаритами box в поле значка.</summary>
        private static MatrixTransform Fit(int size, Rect box, double fill = 0.96)
        {
            double k = size * fill / Math.Max(box.Width, box.Height);
            double ox = (size - box.Width * k) / 2.0 - box.X * k;
            double oy = (size - box.Height * k) / 2.0 - box.Y * k;
            var m = new MatrixTransform(k, 0, 0, k, ox, oy);
            m.Freeze();
            return m;
        }

        private static Pen RimFor(int size, double k)
        {
            return Stroke(RimBrush, Math.Max(0.7, size / 36.0) / k);
        }

        private static Pen RoundPen(Brush brush, double thickness)
        {
            var pen = new Pen(brush, thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            pen.Freeze();
            return pen;
        }

        private static void Shadow(DrawingContext dc, int size, Transform fit, Geometry g)
        {
            if (size < 24) return;
            dc.PushTransform(new TranslateTransform(0, size * 0.04));
            dc.PushTransform(fit);
            dc.DrawGeometry(Frozen(Color.FromArgb(0x4D, 0x00, 0x00, 0x00)), null, g);
            dc.Pop();
            dc.Pop();
        }

        /// <summary>Окружность в горизонтальной плоскости z — в проекции эллипс.</summary>
        private static Geometry IsoDisc(View view, double cx, double cy, double z, double r)
        {
            const int n = 28;
            var pts = new Point[n];
            for (int i = 0; i < n; i++)
            {
                double a = 2 * Math.PI * i / n;
                pts[i] = Iso(view, cx + r * Math.Cos(a), cy + r * Math.Sin(a), z);
            }
            var fig = new PathFigure { StartPoint = pts[0], IsClosed = true, IsFilled = true };
            fig.Segments.Add(new PolyBezierSegment(SmoothThrough(pts), true));
            var g = new PathGeometry();
            g.Figures.Add(fig);
            g.Freeze();
            return g;
        }

        /// <summary>Замкнутый сглаженный контур через точки (Catmull-Rom → Безье).</summary>
        private static Point[] SmoothThrough(Point[] p)
        {
            int n = p.Length;
            var res = new Point[n * 3];
            for (int i = 0; i < n; i++)
            {
                Point p0 = p[(i - 1 + n) % n], p1 = p[i], p2 = p[(i + 1) % n], p3 = p[(i + 2) % n];
                res[i * 3] = p1 + (p2 - p0) / 6.0;
                res[i * 3 + 1] = p2 - (p3 - p1) / 6.0;
                res[i * 3 + 2] = p2;
            }
            return res;
        }

        /// <summary>Тот же градиент, светлее (shift &gt; 0) или темнее (&lt; 0) — для граней.</summary>
        private static Brush ShadeBrush(Brush brush, double shift)
        {
            var b = (LinearGradientBrush)((LinearGradientBrush)brush).CloneCurrentValue();
            foreach (GradientStop s in b.GradientStops)
                s.Color = Shade(s.Color, shift);
            b.Freeze();
            return b;
        }

        private static Color Shade(Color c, double shift)
        {
            Func<byte, byte> f = v => (byte)Math.Max(0, Math.Min(255,
                shift >= 0 ? v + (255 - v) * shift : v * (1 + shift)));
            return Color.FromArgb(c.A, f(c.R), f(c.G), f(c.B));
        }

        private static Brush Gradient(Color light, Color dark)
        {
            var b = new LinearGradientBrush(light, dark, new Point(0.15, 0), new Point(0.85, 1));
            b.Freeze();
            return b;
        }

        /// <summary>
        /// «Весь чертёж»: прямоугольная палитра масляных красок, вид с юго-запада.
        /// Вдоль дальних краёв — мазки красок, у западного края — отверстие
        /// под большой палец, наискосок лежит кисть, концом к красной краске.
        /// </summary>
        private static void DrawPalette(DrawingContext dc, int size)
        {
            const View v = View.SouthWest;
            const double X = 72, Y = 50, Z = 5;
            Func<double, double, double, Point> P = (x, y, z) => Iso(v, x, y, z);

            Geometry top = Poly(P(0, 0, Z), P(X, 0, Z), P(X, Y, Z), P(0, Y, Z));
            Geometry south = Poly(P(0, 0, 0), P(X, 0, 0), P(X, 0, Z), P(0, 0, Z));
            Geometry west = Poly(P(0, 0, 0), P(0, 0, Z), P(0, Y, Z), P(0, Y, 0));

            // Кисть: от конца черенка (свисает за южный край) к кончику у краски.
            Point hEnd = P(80, -4, Z + 40), tip = P(44, 36, Z + 2.5);

            Rect box = Rect.Union(Rect.Union(top.Bounds, south.Bounds), west.Bounds);
            box.Union(hEnd);
            box.Inflate(3, 3);
            MatrixTransform fit = Fit(size, box);
            double k = fit.Matrix.M11;
            Pen rim = RimFor(size, k);

            var body = new GeometryGroup();
            body.Children.Add(top);
            body.Children.Add(south);
            body.Children.Add(west);
            Shadow(dc, size, fit, body);

            dc.PushTransform(fit);
            Brush ti = Titanium();
            dc.DrawGeometry(ShadeBrush(ti, -0.15), rim, south);
            dc.DrawGeometry(ShadeBrush(ti, -0.35), rim, west);
            dc.DrawGeometry(ShadeBrush(ti, 0.10), rim, top);

            // Отверстие под палец.
            dc.DrawGeometry(RimBrush, null, IsoDisc(v, 10, 26, Z, 5.0));

            // Мазки красок: дальние рисуются первыми.
            var paints = new[]
            {
                new { X = 24.0, Y = 41.0, C = Color.FromRgb(0xF2, 0xC6, 0x1F) },
                new { X = 42.0, Y = 41.0, C = Color.FromRgb(0xD9, 0x2B, 0x22) },
                new { X = 60.0, Y = 40.0, C = Color.FromRgb(0x2F, 0x5B, 0xD6) },
                new { X = 61.0, Y = 23.0, C = Color.FromRgb(0x22, 0x9A, 0x44) },
                new { X = 60.0, Y = 8.0, C = Color.FromRgb(0xF4, 0xF2, 0xEA) },
            };
            Array.Sort(paints, (a, b) => (b.X + b.Y).CompareTo(a.X + a.Y));
            bool large = size >= 24;
            double rad = large ? 6.8 : 8.0;
            foreach (var p in paints)
            {
                if (!large && p.C.R > 0xF0 && p.C.G > 0xF0) continue;   // белила на 16 px сливаются с металлом
                dc.DrawGeometry(Frozen(Shade(p.C, -0.35)), rim, IsoDisc(v, p.X, p.Y, Z, rad));
                dc.DrawGeometry(Frozen(p.C), null, IsoDisc(v, p.X - 0.6, p.Y - 0.6, Z + 1.6, rad * 0.78));
                if (large)
                    dc.DrawGeometry(Frozen(Color.FromArgb(0xB0, 0xFF, 0xFF, 0xFF)), null, IsoDisc(v, p.X - 2.2, p.Y - 0.5, Z + 2.2, rad * 0.22));
            }

            // Кисть: черенок — дерево, обойма — титан, щетина — тёмная с краской на кончике.
            Point ferrule0 = Lerp(hEnd, tip, 0.62), ferrule1 = Lerp(hEnd, tip, 0.78);
            double w = large ? 5.0 : 6.5;
            dc.DrawLine(RoundPen(Frozen(Color.FromArgb(0x55, 0, 0, 0)), w), hEnd + new Vector(0, 2.5), tip + new Vector(0, 2.5));
            dc.DrawLine(RoundPen(RimBrush, w + 2), hEnd, tip);
            dc.DrawLine(RoundPen(Gradient(Color.FromRgb(0xD8, 0x4A, 0x2A), Color.FromRgb(0x7A, 0x1E, 0x12)), w), hEnd, ferrule0);
            dc.DrawLine(Stroke(Titanium(), w + 0.6), ferrule0, ferrule1);
            dc.DrawLine(RoundPen(Frozen(Color.FromRgb(0x3A, 0x2A, 0x20)), w * 0.9), ferrule1, Lerp(ferrule1, tip, 0.6));
            dc.DrawLine(RoundPen(Frozen(Color.FromRgb(0xD9, 0x2B, 0x22)), w * 0.7), Lerp(ferrule1, tip, 0.6), tip);
            dc.Pop();
        }

        /// <summary>
        /// «Рамка»: кирпич со шпателем, вид с юго-востока. Кирпич лежит длинной
        /// стороной на запад-восток, к зрителю — южная (длинная) и восточная
        /// (торец) грани. Полотно шпателя лежит на кирпиче, ручка свисает за
        /// восточный край к зрителю.
        /// </summary>
        private static void DrawBrick(DrawingContext dc, int size)
        {
            const View v = View.SouthEast;
            const double X = 60, Y = 29, Z = 24;
            Func<double, double, double, Point> P = (x, y, z) => Iso(v, x, y, z);

            Geometry top = Poly(P(0, 0, Z), P(X, 0, Z), P(X, Y, Z), P(0, Y, Z));
            Geometry south = Poly(P(0, 0, 0), P(X, 0, 0), P(X, 0, Z), P(0, 0, Z));
            Geometry east = Poly(P(X, 0, 0), P(X, Y, 0), P(X, Y, Z), P(X, 0, Z));

            // Шпатель: трапеция полотна рабочей кромкой на запад, шейка и ручка — на восток.
            double bz = Z + 0.8;
            Geometry blade = Poly(P(6, 3, bz), P(32, 9, bz), P(32, 21, bz), P(6, 27, bz));
            Point neck0 = P(31, 15, bz + 0.5), neck1 = P(40, 15, bz + 8);
            Point grip0 = P(41, 15, bz + 10), grip1 = P(58, 15, bz + 40);

            Rect box = Rect.Union(Rect.Union(top.Bounds, south.Bounds), east.Bounds);
            box.Union(grip1);
            box.Inflate(4, 4);
            MatrixTransform fit = Fit(size, box);
            double k = fit.Matrix.M11;
            Pen rim = RimFor(size, k);
            bool large = size >= 24;

            var body = new GeometryGroup();
            body.Children.Add(top);
            body.Children.Add(south);
            body.Children.Add(east);
            Shadow(dc, size, fit, body);

            dc.PushTransform(fit);
            Brush brick = Gradient(Color.FromRgb(0xCF, 0x62, 0x40), Color.FromRgb(0x8C, 0x2E, 0x1E));
            dc.DrawGeometry(ShadeBrush(brick, 0.14), rim, top);
            dc.DrawGeometry(brick, rim, south);
            dc.DrawGeometry(ShadeBrush(brick, -0.30), rim, east);

            // Тень шпателя на кирпиче и само полотно.
            if (large)
            {
                dc.PushTransform(new TranslateTransform(0, 1.6));
                dc.DrawGeometry(Frozen(Color.FromArgb(0x50, 0, 0, 0)), null, blade);
                dc.Pop();
            }
            dc.DrawGeometry(ShadeBrush(Titanium(), 0.05), rim, blade);

            // Шейка — титан, ручка — дерево.
            double w = large ? 3.4 : 4.5, gw = large ? 7.5 : 9.0;
            dc.DrawLine(RoundPen(RimBrush, w + 2), neck0, neck1);
            dc.DrawLine(RoundPen(Titanium(), w), neck0, neck1);
            dc.DrawLine(RoundPen(RimBrush, gw + 2), grip0, grip1);
            dc.DrawLine(RoundPen(Gradient(Color.FromRgb(0xE3, 0x9A, 0x5A), Color.FromRgb(0x8E, 0x4A, 0x1C)), gw), grip0, grip1);
            dc.Pop();
        }

        private static Point Lerp(Point a, Point b, double t)
        {
            return new Point(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t);
        }

        // ------------------------------------------------------------ чемодан
        //
        // Чемодан в косоугольной проекции: передняя стенка, крышка уходит назад
        // вверх вправо, справа боковина, на крышке ручка. Только контуры, без
        // заливки: объём держится на рёбрах, уходящих вглубь, и на ручке.
        // Координаты — в сетке 2000×2000, в той же, в какой нарисован образец.
        //
        // Рёбра — титаном, тем же, что и рамки соседних значков, поверх более
        // широкой тёмной кромки: как у «Весь чертёж» в прежнем виде. Все рёбра
        // и ручка — одна геометрия, поэтому градиент идёт по всему знаку
        // целиком, а не по каждой линии отдельно.

        private const double SuitLeft = 200, SuitTop = 400, SuitWidth = 1600, SuitHeight = 1250;

        private static void DrawSuitcase(DrawingContext dc, int size)
        {
            double k = size * 0.94 / SuitWidth;
            double ox = (size - SuitWidth * k) / 2.0 - SuitLeft * k;
            double oy = (size - SuitHeight * k) / 2.0 - SuitTop * k;
            var fit = new MatrixTransform(k, 0, 0, k, ox, oy);

            // Передняя стенка и глубина: крышка и боковина уходят на (dx, -dy).
            const double l = 230, r = 1400, t = 880, b = 1630;
            const double dx = 360, dy = 330;

            var edges = new GeometryGroup();
            edges.Children.Add(Poly(new Point(l, t), new Point(r, t), new Point(r, b), new Point(l, b)));
            edges.Children.Add(Line(new Point(l, t), new Point(l + dx, t - dy), new Point(r + dx, t - dy), new Point(r, t)));
            edges.Children.Add(Line(new Point(r + dx, t - dy), new Point(r + dx, b - dy), new Point(r, b)));

            // Ручка стоит посередине крышки: ножки опираются на её среднюю линию.
            double hx = (l + r) / 2.0 + dx / 2.0;
            double hBase = t - dy / 2.0;
            edges.Children.Add(HandleArch(hx, 185, hBase, 420, 90));
            edges.Freeze();

            // Толщина — в пикселях значка, как у прежней рамки, а в сетку
            // переводится делением на масштаб.
            double metal = Math.Max(1.0, size / 16.0) / k;
            double rimW = Math.Max(0.5, size / 48.0) / k;

            if (size >= 24)
            {
                dc.PushTransform(new TranslateTransform(0, size * 0.04));
                dc.PushTransform(fit);
                dc.DrawGeometry(null, Stroke(Frozen(Color.FromArgb(0x4D, 0x00, 0x00, 0x00)), metal), edges);
                dc.Pop();
                dc.Pop();
            }

            dc.PushTransform(fit);
            dc.DrawGeometry(null, Stroke(Frozen(Color.FromArgb(0xA0, 0x2B, 0x30, 0x36)), metal + 2 * rimW), edges);
            dc.DrawGeometry(null, Stroke(Titanium(), metal), edges);
            dc.Pop();
        }

        /// <summary>Ручка: ножка вверх, скруглённая перекладина, ножка вниз.</summary>
        private static Geometry HandleArch(double cx, double half, double baseY, double topY, double radius)
        {
            double x0 = cx - half, x1 = cx + half;
            var fig = new PathFigure { StartPoint = new Point(x0, baseY), IsClosed = false, IsFilled = false };
            fig.Segments.Add(new LineSegment(new Point(x0, topY + radius), true));
            fig.Segments.Add(new ArcSegment(new Point(x0 + radius, topY), new Size(radius, radius), 0, false, SweepDirection.Clockwise, true));
            fig.Segments.Add(new LineSegment(new Point(x1 - radius, topY), true));
            fig.Segments.Add(new ArcSegment(new Point(x1, topY + radius), new Size(radius, radius), 0, false, SweepDirection.Clockwise, true));
            fig.Segments.Add(new LineSegment(new Point(x1, baseY), true));
            var g = new PathGeometry();
            g.Figures.Add(fig);
            g.Freeze();
            return g;
        }

        /// <summary>Ломаная без замыкания — ребро, уходящее вглубь.</summary>
        private static Geometry Line(params Point[] pts)
        {
            var fig = new PathFigure { StartPoint = pts[0], IsClosed = false, IsFilled = false };
            for (int i = 1; i < pts.Length; i++)
                fig.Segments.Add(new LineSegment(pts[i], true));
            var g = new PathGeometry();
            g.Figures.Add(fig);
            g.Freeze();
            return g;
        }

        private static Geometry Poly(params Point[] pts)
        {
            var fig = new PathFigure { StartPoint = pts[0], IsClosed = true, IsFilled = true };
            for (int i = 1; i < pts.Length; i++)
                fig.Segments.Add(new LineSegment(pts[i], true) { IsSmoothJoin = false });
            var g = new PathGeometry();
            g.Figures.Add(fig);
            g.Freeze();
            return g;
        }

        /// <summary>Титан: блик, тело, тёмная кромка — по диагонали, как на литой детали.</summary>
        private static Brush Titanium()
        {
            var b = new LinearGradientBrush
            {
                StartPoint = new Point(0.15, 0.0),
                EndPoint = new Point(0.85, 1.0)
            };
            b.GradientStops.Add(new GradientStop(Color.FromRgb(0xF6, 0xF8, 0xFA), 0.00));
            b.GradientStops.Add(new GradientStop(Color.FromRgb(0xCE, 0xD4, 0xDA), 0.26));
            b.GradientStops.Add(new GradientStop(Color.FromRgb(0x97, 0x9F, 0xA8), 0.54));
            b.GradientStops.Add(new GradientStop(Color.FromRgb(0x6C, 0x74, 0x7D), 0.78));
            b.GradientStops.Add(new GradientStop(Color.FromRgb(0x41, 0x47, 0x4E), 1.00));
            b.Freeze();
            return b;
        }

        private static SolidColorBrush Frozen(Color color)
        {
            var b = new SolidColorBrush(color);
            b.Freeze();
            return b;
        }

        /// <summary>
        /// Тёмная кромка знака. Без неё почти белый верхний край титана пропадает
        /// на светлой теме AutoCAD — панель там сама светло-серая.
        /// </summary>
        private static Pen Rim(int size)
        {
            var pen = new Pen(Frozen(Color.FromArgb(0xA0, 0x2B, 0x30, 0x36)),
                              Math.Max(0.7, size / 36.0));
            pen.Freeze();
            return pen;
        }

        /// <summary>
        /// Скрещённые молот и кирка — контур снят с векторного рисунка (SVG),
        /// координаты один в один. Рисовать такое кривыми на глаз бесполезно:
        /// форма держится на десятках управляющих точек, и подобрать их вслепую
        /// не выходит — проверено.
        ///
        /// В исходнике фигура нарисована обводкой: внешний контур задаёт границу
        /// всего знака, внутренние — области заливки. Поэтому внешний заливается
        /// тёмным, а внутренние поверх него титаном: тёмным остаётся ровно та
        /// полоска, что и была обводкой, включая линии на перекрестье, где один
        /// инструмент проходит над другим.
        /// </summary>
        private static void DrawHammers(DrawingContext dc, int size)
        {
            Geometry outline = Geometry.Parse(EmblemOutline);
            var inner = new GeometryGroup();
            foreach (string path in EmblemInner)
                inner.Children.Add(Geometry.Parse(path));

            // Ширина знака нормирована в единицу, поэтому вписывание — это одно
            // умножение плюс сдвиг к середине поля. Трансформация вешается на
            // контекст, а не на саму геометрию: Geometry.Parse отдаёт замороженный
            // объект, и присвоить ему Transform уже нельзя.
            double k = size * 0.96;
            var fit = new MatrixTransform(k, 0, 0, k,
                                          (size - k) / 2.0,
                                          (size - k * EmblemHeight) / 2.0);

            // Тень только на крупном значке: на 16 px она замыливает знак.
            if (size >= 24)
            {
                dc.PushTransform(new TranslateTransform(0, size * 0.04));
                dc.PushTransform(fit);
                dc.DrawGeometry(Frozen(Color.FromArgb(0x4D, 0x00, 0x00, 0x00)), null, outline);
                dc.Pop();
                dc.Pop();
            }

            dc.PushTransform(fit);
            dc.DrawGeometry(Frozen(Color.FromRgb(0x2B, 0x30, 0x36)), null, outline);
            if (SettingsStyle == 1)
                PaintToolMaterials(dc);
            else
                dc.DrawGeometry(Titanium(), null, inner);
            dc.Pop();
        }

        /// <summary>
        /// «Настройки»: 0 — весь знак титаном, 1 — черенки деревом, молот и клюв
        /// кирки сталью, как у палитры и кирпича.
        /// </summary>
        internal static int SettingsStyle = 1;

        /// <summary>
        /// Тот же контур знака, но по материалам. Внутренние области знака —
        /// это инструмент целиком (боёк вместе с черенком), поэтому они
        /// разрезаются по линии, где черенок входит в боёк: у молота это
        /// x + y = 0.488 (черенок идёт по диагонали y ≈ x), у кирки x − y = 0.552
        /// (черенок по x + y ≈ 0.98). Сам контур не меняется ни на точку.
        ///
        /// Дерево — с перепадом поперёк черенка, как у круглой палки: светлая
        /// сторона к свету (вверх), тёмная — от него. Сталь — та же, что у
        /// полотна кельмы.
        /// </summary>
        private static void PaintToolMaterials(DrawingContext dc)
        {
            Geometry hammer = Geometry.Parse(EmblemInner[0]);   // молот: боёк + верх черенка
            Geometry pick = Geometry.Parse(EmblemInner[1]);     // кирка: клюв + весь черенок
            Geometry hammerTail = Geometry.Parse(EmblemInner[2]); // низ черенка молота за перекрестьем

            // Полуплоскости-ножи: всё, что до линии, — боёк.
            Geometry hammerSide = Poly(new Point(-1, -1), new Point(1.488, -1), new Point(-1, 1.488));
            Geometry pickSide = Poly(new Point(0.552 - 1, -1), new Point(2, -1), new Point(2, 2 - 0.552));

            Geometry hammerHead = Geometry.Combine(hammer, hammerSide, GeometryCombineMode.Intersect, null);
            Geometry hammerShaft = Geometry.Combine(hammer, hammerSide, GeometryCombineMode.Exclude, null);
            Geometry pickHead = Geometry.Combine(pick, pickSide, GeometryCombineMode.Intersect, null);
            Geometry pickShaft = Geometry.Combine(pick, pickSide, GeometryCombineMode.Exclude, null);

            Color woodLight = Color.FromRgb(0xE8, 0xA8, 0x62), woodDark = Color.FromRgb(0x8E, 0x4A, 0x1E);
            Color steelLight = Color.FromRgb(0xE4, 0xE2, 0xF0), steelDark = Color.FromRgb(0x6E, 0x6C, 0x84);

            // Черенок молота идёт по y ≈ x: поперёк него — направление (1, −1).
            Brush hammerWood = Absolute(new Point(0.535, 0.465), new Point(0.465, 0.535), woodLight, woodDark);
            // Черенок кирки идёт по x + y ≈ 0.98: поперёк — (1, 1).
            Brush pickWood = Absolute(new Point(0.465, 0.465), new Point(0.515, 0.515), woodLight, woodDark);
            Brush steel = new LinearGradientBrush(steelLight, steelDark, new Point(0.15, 0), new Point(0.85, 1));
            steel.Freeze();

            dc.DrawGeometry(pickWood, null, pickShaft);
            dc.DrawGeometry(hammerWood, null, hammerShaft);
            dc.DrawGeometry(hammerWood, null, hammerTail);
            dc.DrawGeometry(steel, null, hammerHead);
            dc.DrawGeometry(steel, null, pickHead);
        }

        private static Brush Absolute(Point a, Point b, Color c0, Color c1)
        {
            var g = new LinearGradientBrush(c0, c1, a, b) { MappingMode = BrushMappingMode.Absolute };
            g.Freeze();
            return g;
        }

        /// <summary>Высота знака в долях его ширины.</summary>
        private const double EmblemHeight = 0.838;

        /// <summary>Внешний контур знака — им заливается тёмная обводка.</summary>
        private const string EmblemOutline =
            "M0.2646,0.0012C0.2622,0.0021 0.2591,0.0032 0.2576,0.004C0.2536,0.0062 0.0067,0.2528 0.0031,0.2583C0.0001,0.263 0,0.2637 0,0.2754C0,0.2855 0.0004,0.2885 0.0021,0.2918C0.0032,0.2939 0.0234,0.3148 0.0468,0.3382C0.0936,0.385 0.0943,0.3854 0.1066,0.3855C0.1114,0.3855 0.1148,0.3847 0.1196,0.3825C0.1253,0.3799 0.131,0.3746 0.1699,0.3352L0.2137,0.2909L0.2246,0.3002C0.2744,0.3434 0.3267,0.3922 0.3884,0.4528L0.4235,0.4873L0.3706,0.5387C0.3415,0.567 0.3011,0.6063 0.2808,0.6259C0.2606,0.6456 0.2303,0.675 0.2134,0.6913L0.1828,0.7211L0.1739,0.7245C0.1626,0.7286 0.1628,0.7285 0.16,0.7339C0.1582,0.7371 0.1576,0.7403 0.1576,0.7449C0.1577,0.7566 0.1642,0.7673 0.1841,0.7881C0.2088,0.8141 0.2236,0.8245 0.2369,0.8252C0.2429,0.8255 0.2449,0.8252 0.2488,0.8232C0.2543,0.8202 0.2544,0.8201 0.2587,0.8087L0.262,0.7999L0.339,0.7194C0.3813,0.6751 0.433,0.6209 0.4539,0.599L0.4919,0.5592L0.4968,0.5645C0.4995,0.5675 0.5153,0.5847 0.5319,0.6028C0.581,0.656 0.6436,0.7266 0.6971,0.7889C0.7177,0.813 0.7182,0.8135 0.7213,0.8228C0.7241,0.8312 0.7251,0.8327 0.7288,0.8352C0.7325,0.8376 0.7342,0.838 0.7412,0.838C0.7521,0.838 0.7593,0.8344 0.7711,0.8233C0.7759,0.8188 0.7838,0.812 0.7887,0.8083C0.8027,0.7978 0.8267,0.7702 0.833,0.7574C0.8372,0.749 0.8377,0.7392 0.8342,0.7326C0.8314,0.7271 0.8314,0.7271 0.8195,0.7226C0.8109,0.7195 0.8092,0.7181 0.7827,0.6953C0.6977,0.6223 0.5582,0.4976 0.5565,0.4931C0.5561,0.4922 0.5639,0.4839 0.6185,0.4267C0.6328,0.4118 0.6647,0.3785 0.6894,0.3526C0.7837,0.2539 0.7848,0.2526 0.7917,0.2485C0.8015,0.2423 0.8103,0.2397 0.8197,0.2403C0.8271,0.2406 0.8281,0.241 0.8356,0.2459C0.8587,0.2613 0.8925,0.3013 0.9316,0.3594C0.9461,0.3808 0.9798,0.4342 0.9807,0.4371C0.9812,0.4385 0.9989,0.4317 0.9997,0.4298C1,0.429 0.9983,0.4205 0.9958,0.4108C0.9727,0.3188 0.9282,0.2326 0.8629,0.1529C0.8294,0.1121 0.7833,0.0663 0.7348,0.0259C0.714,0.0083 0.7156,0.0092 0.6981,0.0079L0.6889,0.0071L0.67,0.0261L0.6513,0.0449L0.653,0.0589L0.6548,0.0728L0.6936,0.1113C0.715,0.1324 0.7331,0.1509 0.7339,0.1524C0.737,0.1588 0.7346,0.1779 0.7294,0.1882C0.7282,0.1905 0.7047,0.2142 0.6735,0.2445C0.6438,0.2733 0.6104,0.3058 0.599,0.3169C0.5797,0.3357 0.5182,0.3953 0.4959,0.417L0.4857,0.4267L0.458,0.3986C0.4021,0.3418 0.3557,0.2923 0.3122,0.2425L0.2889,0.2159L0.3337,0.1709C0.3756,0.1288 0.3787,0.1255 0.3804,0.12C0.384,0.1092 0.3826,0.0988 0.3765,0.0902C0.3749,0.0878 0.3554,0.0677 0.3331,0.0456C0.2891,0.0017 0.2882,0.001 0.2755,0.0002C0.2719,0 0.267,0.0004 0.2646,0.0012Z";

        /// <summary>Внутренние области: их заливает металл.</summary>
        private static readonly string[] EmblemInner =
        {
            "M0.2812,0.0122C0.2856,0.0141 0.3669,0.0944 0.3697,0.0997C0.3724,0.1047 0.3719,0.1137 0.3686,0.1192C0.3672,0.1216 0.3452,0.1444 0.32,0.1696L0.274,0.2154L0.2882,0.2318C0.3345,0.2853 0.3795,0.3335 0.4439,0.3986L0.4772,0.4323L0.4535,0.456L0.4297,0.4797L0.3889,0.4391C0.3374,0.3881 0.3056,0.3579 0.2592,0.3162C0.2396,0.2987 0.2141,0.2768 0.213,0.2768C0.2124,0.2768 0.1908,0.2979 0.1649,0.3236C0.1391,0.3494 0.1171,0.3709 0.1161,0.3714C0.1122,0.3736 0.106,0.3742 0.1009,0.3731C0.0961,0.3719 0.0929,0.3689 0.0542,0.3305C0.0266,0.303 0.0121,0.2879 0.0107,0.285C0.008,0.2792 0.0081,0.2722 0.0109,0.2663C0.0141,0.26 0.2594,0.0155 0.2657,0.0127C0.271,0.0102 0.2759,0.0101 0.2812,0.0122Z",
            "M0.7063,0.0193C0.7092,0.0193 0.7122,0.0212 0.7232,0.0302C0.7728,0.0709 0.8266,0.1247 0.8616,0.1686C0.9111,0.231 0.9475,0.2962 0.9709,0.3644C0.9792,0.389 0.9906,0.4303 0.9882,0.4277C0.9877,0.4272 0.9825,0.4189 0.9767,0.4094C0.9215,0.3194 0.8731,0.2576 0.841,0.2366C0.8318,0.2306 0.8268,0.229 0.8174,0.229C0.8088,0.2291 0.8004,0.2315 0.7896,0.2371C0.7797,0.2423 0.7786,0.2433 0.7456,0.2779C0.6869,0.3394 0.6288,0.4002 0.6061,0.424C0.5926,0.438 0.5615,0.4707 0.5368,0.4965C0.5121,0.5224 0.4755,0.5606 0.4556,0.5815C0.4173,0.6216 0.3782,0.6624 0.3056,0.7384C0.2478,0.7989 0.2527,0.7934 0.2488,0.804C0.247,0.809 0.2449,0.8134 0.2443,0.8139C0.2412,0.8158 0.2339,0.8146 0.2277,0.8115C0.2118,0.803 0.1779,0.7687 0.1708,0.7536C0.1652,0.7416 0.1665,0.7384 0.1791,0.7341L0.1885,0.7308L0.2153,0.7049C0.2299,0.6907 0.2587,0.6627 0.2792,0.6427C0.2998,0.6228 0.3251,0.5982 0.3355,0.588C0.3459,0.5778 0.379,0.5457 0.4091,0.5166C0.4392,0.4874 0.4731,0.4545 0.4843,0.4435C0.518,0.4107 0.5387,0.3906 0.5969,0.3342C0.7168,0.2177 0.7355,0.1994 0.7378,0.1951C0.7449,0.1823 0.7479,0.1628 0.7448,0.1508C0.7434,0.1457 0.7409,0.143 0.7042,0.1066L0.6652,0.0679L0.664,0.0587L0.6628,0.0495L0.6779,0.0337L0.6931,0.018L0.6978,0.0186C0.7005,0.0189 0.7043,0.0193 0.7063,0.0193Z",
            "M0.578,0.5279C0.6393,0.5845 0.699,0.6377 0.7731,0.7013C0.8049,0.7287 0.8054,0.7291 0.8147,0.7325C0.8199,0.7343 0.8245,0.7365 0.8249,0.7371C0.8266,0.7398 0.8258,0.7477 0.8231,0.753C0.8163,0.7661 0.7882,0.7968 0.7669,0.8139C0.754,0.8242 0.7466,0.828 0.74,0.8275C0.7348,0.8272 0.7338,0.8256 0.7293,0.8133C0.7274,0.808 0.7233,0.8025 0.7031,0.7792C0.642,0.7081 0.5785,0.6371 0.5229,0.5775L0.501,0.554L0.5256,0.5285C0.5392,0.5145 0.5506,0.5031 0.551,0.5032C0.5514,0.5035 0.5635,0.5145 0.578,0.5279Z",
        };

        /// <summary>
        /// «Весь чертёж» — сплошная рамка с ужавшимся квадратом внутри.
        /// «Рамка» — квадрат и пунктирный контур вокруг него, без внешней рамки:
        /// это и есть рамка выделения, второй контур поверх неё был бы маслом
        /// масляным и на мелком размере превращал знак в рябь.
        /// </summary>
        private static void DrawSheet(DrawingContext dc, int size, bool marqueeMode)
        {
            Brush metal = Titanium();
            Brush shadow = Frozen(Color.FromArgb(0x4D, 0x00, 0x00, 0x00));
            Pen rim = Rim(size);
            double drop = size >= 24 ? size * 0.04 : 0.0;

            Geometry outline;
            Rect square;
            double stroke;

            if (marqueeMode)
            {
                stroke = Math.Max(0.9, size / 16.0);
                var band = new Rect(size * 0.105, size * 0.105, size * 0.790, size * 0.790);
                outline = DashedOutline(band, size * 0.075, size * 0.025);
                square = new Rect(size * 0.265, size * 0.265, size * 0.470, size * 0.470);
            }
            else
            {
                stroke = Math.Max(1.0, size / 11.0);
                double m = size * 0.13;
                outline = RoundedOutline(new Rect(m, m, size - 2 * m, size - 2 * m), size * 0.05);
                double i = size * 0.355;
                square = new Rect(i, i, size - 2 * i, size - 2 * i);
            }

            // Тень только на крупном значке: на 16 px она замыливает штрихи.
            if (drop > 0)
            {
                dc.PushTransform(new TranslateTransform(0, drop));
                dc.DrawGeometry(null, Stroke(shadow, stroke), outline);
                dc.DrawRectangle(shadow, null, square);
                dc.Pop();
            }

            // Кромка — той же формой, но шире, подложенная снизу: её края торчат
            // с обеих сторон и работают обводкой. С разобранным на куски контуром
            // это честно работает и на пунктире: подгонять штрихи под другую
            // толщину пера, как приходилось со DashStyle, больше не нужно.
            //
            // На мелком значке у пунктира кромку снимаем: она шире просветов между
            // штрихами и слепляет их в сплошную кашу.
            if (!marqueeMode || size >= 48)
                dc.DrawGeometry(null, Stroke(rim.Brush, stroke + rim.Thickness * 2), outline);
            dc.DrawGeometry(null, Stroke(metal, stroke), outline);

            dc.DrawRectangle(metal, rim, square);
        }

        private static Pen Stroke(Brush brush, double thickness)
        {
            var pen = new Pen(brush, thickness) { LineJoin = PenLineJoin.Round };
            pen.Freeze();
            return pen;
        }

        private static Geometry RoundedOutline(Rect rect, double radius)
        {
            var geometry = new RectangleGeometry(rect, radius, radius);
            geometry.Freeze();
            return geometry;
        }

        /// <summary>Минимальный разрыв между штрихами, в долях штриха.</summary>
        private const double DashGapRatio = 0.40;

        /// <summary>
        /// Пунктирный контур со скруглёнными углами, у которого штрих всегда
        /// ложится НА угол, а не проваливается в разрыв, и все штрихи одной длины.
        ///
        /// Штатный DashStyle так не умеет: он гонит один ритм по всему периметру,
        /// фаза на углу выходит какая придётся, и угол то съедается разрывом,
        /// то обрубается пополам. Поэтому контур собирается сам — сначала четыре
        /// угловых куска, дуга плюс короткий вывод на обе прилегающие стороны,
        /// а уже между ними прямые штрихи.
        ///
        /// Длину задаёт угол: она равна дуге со скруглением плюс два вывода, и
        /// ровно такой же длины делаются все прямые штрихи. Дальше на сторону
        /// берётся столько штрихов, сколько влезает при разрыве не уже
        /// DashGapRatio от штриха, а остаток раскладывается в равные разрывы —
        /// в том числе по краям, у самых углов. Стороны у значка одинаковые,
        /// поэтому ритм на всех четырёх выходит один и тот же.
        /// </summary>
        private static Geometry DashedOutline(Rect rect, double radius, double lead)
        {
            double l = rect.Left, t = rect.Top, rt = rect.Right, b = rect.Bottom;
            double q = radius;
            double dash = 2 * lead + Math.PI / 2.0 * radius;

            var geometry = new PathGeometry();

            // Углы, по часовой стрелке с левого верхнего.
            Corner(geometry, new Point(l, t + q + lead), new Point(l, t + q),
                             new Point(l + q, t), new Point(l + q + lead, t), q);
            Corner(geometry, new Point(rt - q - lead, t), new Point(rt - q, t),
                             new Point(rt, t + q), new Point(rt, t + q + lead), q);
            Corner(geometry, new Point(rt, b - q - lead), new Point(rt, b - q),
                             new Point(rt - q, b), new Point(rt - q - lead, b), q);
            Corner(geometry, new Point(l + q + lead, b), new Point(l + q, b),
                             new Point(l, b - q), new Point(l, b - q - lead), q);

            // Прямые участки между угловыми кусками.
            Dashes(geometry, new Point(l + q + lead, t), new Point(rt - q - lead, t), dash);
            Dashes(geometry, new Point(rt, t + q + lead), new Point(rt, b - q - lead), dash);
            Dashes(geometry, new Point(rt - q - lead, b), new Point(l + q + lead, b), dash);
            Dashes(geometry, new Point(l, b - q - lead), new Point(l, t + q + lead), dash);

            geometry.Freeze();
            return geometry;
        }

        /// <summary>Угловой кусок: вывод — дуга — вывод, одним росчерком.</summary>
        private static void Corner(PathGeometry geometry, Point from, Point arcFrom,
                                   Point arcTo, Point to, double radius)
        {
            var figure = new PathFigure { StartPoint = from, IsClosed = false, IsFilled = false };
            figure.Segments.Add(new LineSegment(arcFrom, true));
            figure.Segments.Add(new ArcSegment(arcTo, new Size(radius, radius), 0,
                                               false, SweepDirection.Clockwise, true));
            figure.Segments.Add(new LineSegment(to, true));
            geometry.Figures.Add(figure);
        }

        /// <summary>
        /// Штрихи заданной длины на отрезке, с одинаковыми разрывами — включая
        /// разрывы по краям, у самых углов.
        /// </summary>
        private static void Dashes(PathGeometry geometry, Point from, Point to, double dash)
        {
            Vector span = to - from;
            double length = span.Length;
            if (length <= 0 || dash <= 0)
                return;

            int count = (int)Math.Floor((length - DashGapRatio * dash)
                                        / (dash * (1.0 + DashGapRatio)));
            if (count < 1)
                return;                       // места нет — лучше пусто, чем огрызок

            double gap = (length - count * dash) / (count + 1);
            Vector step = span / length;
            double pos = gap;

            for (int n = 0; n < count; n++)
            {
                var figure = new PathFigure
                {
                    StartPoint = from + step * pos,
                    IsClosed = false,
                    IsFilled = false
                };
                figure.Segments.Add(new LineSegment(from + step * (pos + dash), true));
                geometry.Figures.Add(figure);
                pos += dash + gap;
            }
        }
    }
}
