using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Windows.Forms;

// Содержимое палитры. Здесь нет ни одной ссылки на AutoCAD — только WinForms:
// так палитру можно отрисовать и проверить в картинке без запуска AutoCAD.
// Тему (тёмная/светлая) подаёт снаружи RescalePalette.

namespace ZoomZoom
{
    /// <summary>Цвета палитры в духе AutoCAD: тёмная и светлая тема.</summary>
    internal sealed class Theme
    {
        public Color Back, Field, FieldBorder, Text, Muted, Track, TrackOff, Knob, Accent, AccentText, Button, ButtonHot, GroupBorder, Plate;

        public static readonly Theme Dark = new Theme
        {
            Back = Color.FromArgb(46, 52, 64),
            Plate = Color.FromArgb(59, 68, 83),
            Field = Color.FromArgb(38, 45, 56),
            FieldBorder = Color.FromArgb(96, 106, 122),
            Text = Color.FromArgb(240, 240, 240),
            Muted = Color.FromArgb(166, 175, 189),
            Track = Color.FromArgb(25, 110, 191),
            TrackOff = Color.FromArgb(92, 102, 118),
            Knob = Color.FromArgb(250, 250, 250),
            Accent = Color.FromArgb(25, 110, 191),
            AccentText = Color.White,
            Button = Color.FromArgb(76, 86, 102),
            ButtonHot = Color.FromArgb(90, 101, 118),
            GroupBorder = Color.FromArgb(215, 222, 230),
        };

        public static readonly Theme Light = new Theme
        {
            Back = Color.FromArgb(240, 240, 240),
            Plate = Color.FromArgb(226, 228, 232),
            Field = Color.White,
            FieldBorder = Color.FromArgb(170, 170, 170),
            Text = Color.FromArgb(30, 30, 30),
            Muted = Color.FromArgb(105, 105, 105),
            Track = Color.FromArgb(6, 150, 215),
            TrackOff = Color.FromArgb(190, 190, 190),
            Knob = Color.White,
            Accent = Color.FromArgb(6, 150, 215),
            AccentText = Color.White,
            Button = Color.FromArgb(228, 228, 228),
            ButtonHot = Color.FromArgb(215, 215, 215),
            GroupBorder = Color.FromArgb(150, 150, 150),
        };
    }

    /// <summary>
    /// Содержимое палитры. Раскладка — своя, вертикальной стопкой: каждая строка
    /// занимает всю ширину палитры, текст переносится, кнопки масштабов делят
    /// ширину поровну. Поэтому горизонтальной прокрутки не бывает вовсе, а
    /// вертикальная появляется, только если палитру сжали ниже содержимого.
    /// Все размеры считаются от высоты шрифта — при любом разрешении и
    /// масштабе Windows палитра выглядит одинаково.
    /// </summary>
    internal sealed class RescaleControl : UserControl
    {
        private static readonly decimal[] Presets = { 100M, 250M, 500M, 1000M };

        private Theme _theme = Theme.Dark;

        private readonly ToggleSwitch _direction;
        private readonly Label _caption, _times, _hint;
        private readonly TextBox _value;
        private readonly Panel _valueFrame;
        private readonly List<FlatButton> _presets = new List<FlatButton>();
        private readonly ToggleSwitch _explode, _viewports;
        private readonly Label _explodeHint, _viewportsHint, _runHint;

        internal RescaleControl()
        {
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            AutoScroll = true;
            Font = new Font("Segoe UI", 9f, FontStyle.Regular, GraphicsUnit.Point);

            _direction = new ToggleSwitch
            {
                LeftText = Lang.T("Уменьшить", "Reduce"),
                Text = Lang.T("Увеличить", "Enlarge"),
                Checked = RescaleSettings.Enlarge,
                Bold = true
            };
            _direction.CheckedChanged += (s, e) =>
            {
                RescaleSettings.Enlarge = _direction.Checked;
                UpdateTexts();
            };

            _caption = MakeLabel(false);
            _times = MakeLabel(false);
            _times.Text = Lang.T("раз", "times");
            _hint = MakeLabel(true);

            _valueFrame = new Panel();
            _value = new TextBox
            {
                BorderStyle = BorderStyle.None,
                TextAlign = HorizontalAlignment.Right,
                Text = Format((decimal)RescaleSettings.Divisor)
            };
            _value.TextChanged += (s, e) => OnValueTyped();
            _value.Leave += (s, e) => _value.Text = Format((decimal)RescaleSettings.Divisor);
            _value.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { _value.Text = Format((decimal)RescaleSettings.Divisor); e.SuppressKeyPress = true; } };
            _valueFrame.Controls.Add(_value);
            _valueFrame.Paint += (s, e) =>
            {
                bool bad = !TryParse(_value.Text, out decimal _);
                using (var pen = new Pen(bad ? Color.FromArgb(220, 70, 70) : (_value.Focused ? _theme.Accent : _theme.FieldBorder)))
                    e.Graphics.DrawRectangle(pen, 0, 0, _valueFrame.Width - 1, _valueFrame.Height - 1);
            };
            _value.GotFocus += (s, e) => _valueFrame.Invalidate();
            _value.LostFocus += (s, e) => _valueFrame.Invalidate();

            foreach (decimal preset in Presets)
            {
                decimal v = preset;
                var b = new FlatButton { Tag = v };
                b.Click += (s, e) =>
                {
                    RescaleSettings.Divisor = (double)v;
                    _value.Text = Format(v);
                    UpdateTexts();
                };
                _presets.Add(b);
            }

            _explode = new ToggleSwitch
            {
                Text = Lang.T("Расчленять объекты-прокси", "Explode proxy objects"),
                Checked = RescaleSettings.ExplodeProxies
            };
            _explode.CheckedChanged += (s, e) => RescaleSettings.ExplodeProxies = _explode.Checked;
            _explodeHint = MakeLabel(true);
            _explodeHint.Text = Lang.T("СПДС и выгрузки из BIM иначе останутся в старом масштабе.",
                                       "Otherwise SPDS objects and BIM exports keep their old scale.");

            _viewports = new ToggleSwitch
            {
                Text = Lang.T("Масштабировать в видовых экранах", "Rescale layout viewports"),
                Checked = RescaleSettings.ScaleViewports
            };
            _viewports.CheckedChanged += (s, e) => RescaleSettings.ScaleViewports = _viewports.Checked;
            _viewportsHint = MakeLabel(true);

            _runHint = MakeLabel(true);
            _runHint.Text = Lang.T(
                "Команды запуска:\n"
                + "ZZALL — весь чертёж\n"
                + "ZZSEL — рамкой\n"
                + "ZZ1000 — уменьшить весь чертёж в 1000 раз\n"
                + "ZZREF — по опорному отрезку\n"
                + "ZZTEXT — с вопросами в командной строке",
                "Commands:\n"
                + "ZZALL — whole drawing\n"
                + "ZZSEL — window selection\n"
                + "ZZ1000 — reduce the whole drawing 1000 times\n"
                + "ZZREF — by a reference segment\n"
                + "ZZTEXT — with command-line prompts");


            Controls.Add(_direction);
            Controls.Add(_caption);
            Controls.Add(_valueFrame);
            Controls.Add(_times);
            foreach (FlatButton b in _presets) Controls.Add(b);
            Controls.AddRange(new Control[] { _hint, _explode, _explodeHint, _viewports, _viewportsHint, _runHint });

            ApplyTheme(Theme.Dark);
            UpdateTexts();
        }

        private static Label MakeLabel(bool muted)
        {
            return new Label { AutoSize = false, Tag = muted, UseMnemonic = false };
        }

        // ------------------------------------------------------------- тема

        /// <summary>
        /// Фон — всегда цвет темы, а не свойство BackColor: AutoCAD, приняв
        /// содержимое в палитру, сбрасывает его фон на системный светло-серый,
        /// и тёмные плашки оказывались на светлом. Прозрачные подписи и
        /// тумблеры берут фон отсюда же.
        /// </summary>
        protected override void OnPaintBackground(PaintEventArgs e)
        {
            using (var b = new SolidBrush(_theme.Back))
                e.Graphics.FillRectangle(b, ClientRectangle);
        }

        internal void ApplyTheme(Theme theme)
        {
            _theme = theme ?? Theme.Dark;
            BackColor = _theme.Back;
            ForeColor = _theme.Text;
            // Подписи, тумблеры и кнопки — прозрачные: под ними видны
            // скруглённые плашки, которые рисует сама палитра (OnPaint).
            foreach (Control c in Controls)
            {
                c.BackColor = c is Panel ? _theme.Back : Color.Transparent;
                var l = c as Label;
                if (l != null)
                    l.ForeColor = (bool)l.Tag ? _theme.Muted : _theme.Text;
                var ts = c as ToggleSwitch;
                if (ts != null) ts.Theme = _theme;
                var fb = c as FlatButton;
                if (fb != null) fb.Theme = _theme;
            }
            _valueFrame.BackColor = _theme.Field;
            _value.BackColor = _theme.Field;
            _value.ForeColor = _theme.Text;
            HighlightPresets();
            Invalidate(true);
        }

        // ------------------------------------------------------------- значения

        private void OnValueTyped()
        {
            decimal v;
            if (TryParse(_value.Text, out v))
            {
                RescaleSettings.Divisor = (double)v;
                HighlightPresets();
            }
            UpdateHint();
            _valueFrame.Invalidate();
        }

        /// <summary>Число в любом из привычных видов: «1000», «304,8», «304.8».</summary>
        private static bool TryParse(string s, out decimal v)
        {
            s = (s ?? "").Trim().Replace(" ", "").Replace(',', '.');
            return decimal.TryParse(s, NumberStyles.Number, CultureInfo.InvariantCulture, out v)
                   && v > 0 && v <= 1000000M && v != 1M;
        }

        /// <summary>Без хвостовых нулей: «1000», а не «1000.0000».</summary>
        private static string Format(decimal v)
        {
            return v.ToString("0.####", CultureInfo.CurrentCulture);
        }

        private void UpdateTexts()
        {
            bool up = RescaleSettings.Enlarge;
            _caption.Text = up ? Lang.T("Увеличить чертёж в", "Enlarge the drawing")
                               : Lang.T("Уменьшить чертёж в", "Reduce the drawing");
            foreach (FlatButton b in _presets)
            {
                int v = (int)(decimal)b.Tag;
                b.Text = up ? v + ":1" : "1:" + v;
            }
            _viewportsHint.Text = up
                ? Lang.T("Листы остаются как были: масштаб экранов уменьшается во столько же раз.",
                         "Layouts stay as they were: viewport scales decrease by the same factor.")
                : Lang.T("Листы остаются как были: масштаб экранов увеличивается во столько же раз.",
                         "Layouts stay as they were: viewport scales increase by the same factor.");
            UpdateHint();
            HighlightPresets();
            PerformLayout();
        }

        /// <summary>
        /// Подсказка всегда по тому числу, что сейчас в поле, — введено оно
        /// руками или кнопкой. Единица пишется с тремя знаками, «1.000»: так
        /// её показывает AutoCAD в свойствах, и сразу видно, что это длина в
        /// крупных единицах, а не опечатка.
        /// </summary>
        private void UpdateHint()
        {
            decimal v;
            if (!TryParse(_value.Text, out v))
            {
                _hint.Text = Lang.T("Введите число больше 0 и не равное 1, например 1000 или 304,8.",
                                    "Enter a number greater than 0 and not equal to 1, e.g. 1000 or 304.8.");
                PerformLayout();
                return;
            }
            // Все числа подсказки — с точкой, чтобы «304.8» и «1.000» в одной
            // строке не путались между собой.
            string n = v.ToString("0.####", CultureInfo.InvariantCulture);
            string len = Length(v);
            string one = Length(1M);
            // Без «из 1:N в 1:1»: такую запись легко прочитать и наоборот, как
            // масштаб бумаги. Прямо пишем, что станет с длиной. При уменьшении
            // пример всегда от 1000 — самой привычной длины на чертеже:
            // «1000.000 станет 4.000» нагляднее, чем «250.000 станет 1.000».
            const decimal sample = 1000M;
            string times = Times(v);
            _hint.Text = RescaleSettings.Enlarge
                ? Lang.T("Увеличить в " + n + " " + times + ": длина " + one + " станет длиной " + len + ".",
                         "Enlarge " + n + " times: a length of " + one + " becomes " + len + ".")
                : Lang.T("Уменьшить в " + n + " " + times + ": длина " + Length(sample) + " станет длиной " + Length(sample / v) + ".",
                         "Reduce " + n + " times: a length of " + Length(sample) + " becomes " + Length(sample / v) + ".");
            PerformLayout();
        }

        /// <summary>
        /// Длина в подсказке — с точкой и тремя знаками, как в свойствах
        /// AutoCAD: «250.000». Больше знаков — только если они есть в числе.
        /// </summary>
        /// <summary>«в 1000 раз», «в 3 раза», «в 304.8 раза».</summary>
        private static string Times(decimal v)
        {
            if (v != decimal.Truncate(v)) return "раза";
            long n = (long)v % 100;
            if (n >= 12 && n <= 14) return "раз";
            n %= 10;
            return n >= 2 && n <= 4 ? "раза" : "раз";
        }

        private static string Length(decimal v)
        {
            return v.ToString("0.000##", CultureInfo.InvariantCulture);
        }

        private void HighlightPresets()
        {
            foreach (FlatButton b in _presets)
                b.Active = (double)(decimal)b.Tag == RescaleSettings.Divisor;
        }

        // ------------------------------------------------------------- раскладка

        /// <summary>
        /// Высота двух первых блоков (масштаб и параметры) при заданной ширине —
        /// по ней палитра выбирает высоту окна по умолчанию.
        /// </summary>
        internal int PreferredSettingsHeight(int width)
        {
            if (_direction == null) return 0;
            Arrange(width, false);
            return _settingsBottom;
        }

        private int _settingsBottom;

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            if (_direction == null) return;

            int total = Arrange(ClientSize.Width, true);
            AutoScrollMinSize = new Size(0, total);       // ширина — никогда
            Invalidate();                                 // рамки групп переехали вместе с содержимым
        }

        // Прокрутка двигает дочерние элементы сама, а рамки групп рисуются по
        // координатам из раскладки — пересчитываем.
        protected override void OnScroll(ScrollEventArgs se)
        {
            base.OnScroll(se);
            PerformLayout();
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            PerformLayout();
        }

        /// <summary>
        /// Раскладка сверху вниз. apply = false — только посчитать высоту,
        /// ничего не двигая.
        /// </summary>
        private int Arrange(int clientWidth, bool apply)
        {
            int u = Font.Height;                         // единица раскладки
            int pad = (int)(u * 0.6);
            int gap = u / 2;
            int gp = (int)(u * 0.7);                     // отступ внутри рамки группы
            int outerW = Math.Max(u * 10, clientWidth - 2 * pad);
            int width = outerW - 2 * gp;
            int ox = apply ? AutoScrollPosition.X : 0;
            int oy = apply ? AutoScrollPosition.Y : 0;
            int x0 = pad + ox;
            int x = x0 + gp;
            int y = pad + oy;
            if (apply) _groups.Clear();

            int top = BeginGroup(ref y, gp);

            int dirH = _direction.PreferredHeight(width);
            if (apply) _direction.SetBounds(x, y, width, dirH);
            y += dirH + u;

            Stack(_caption, x, ref y, width, gap / 2, apply);

            int fieldH = u + u / 2 + 2;
            int timesW = TextRenderer.MeasureText(_times.Text, Font).Width + 2;
            int fieldW = Math.Min(width - timesW - gap, u * 9);
            if (apply)
            {
                _valueFrame.SetBounds(x, y, fieldW, fieldH);
                int inner = Math.Max(2, (fieldH - _value.PreferredHeight) / 2);
                _value.SetBounds(gap / 2, inner, fieldW - gap, _value.PreferredHeight);
                _times.SetBounds(x + fieldW + gap, y + (fieldH - u) / 2, timesW, u);
            }
            y += fieldH + gap;

            // Кнопки масштабов: делят ширину поровну. Если в одну строку не
            // помещаются подписи — по две в ряд.
            int perRow = _presets.Count;
            int need = 0;
            foreach (FlatButton b in _presets)
                need = Math.Max(need, TextRenderer.MeasureText(b.Text, Font).Width + u);
            if (need * perRow + gap * (perRow - 1) > width) perRow = 2;
            int bw = (width - gap * (perRow - 1)) / perRow;
            int bh = u + u / 2 + 2;
            if (apply)
                for (int i = 0; i < _presets.Count; i++)
                {
                    int col = i % perRow, row = i / perRow;
                    _presets[i].SetBounds(x + col * (bw + gap), y + row * (bh + gap), bw, bh);
                }
            y += ((_presets.Count + perRow - 1) / perRow) * (bh + gap);

            Stack(_hint, x, ref y, width, 0, apply);
            EndGroup(x0, outerW, top, ref y, gp, gap, apply);

            top = BeginGroup(ref y, gp);
            int exH = _explode.PreferredHeight(width);
            if (apply) _explode.SetBounds(x, y, width, exH);
            y += exH + gap / 2;
            Stack(_explodeHint, x + _explode.TextIndent, ref y, width - _explode.TextIndent, u, apply);

            int vpH = _viewports.PreferredHeight(width);
            if (apply) _viewports.SetBounds(x, y, width, vpH);
            y += vpH + gap / 2;
            Stack(_viewportsHint, x + _viewports.TextIndent, ref y, width - _viewports.TextIndent, u, apply);
            Stack(_runHint, x, ref y, width, 0, apply);
            EndGroup(x0, outerW, top, ref y, gp, pad, apply);

            _settingsBottom = y - oy;
            return y - oy;
        }

        // ------------------------------------------------------------- группы
        //
        // Блоки палитры обведены тонкой светлой рамкой со скруглёнными углами —
        // как группы в WPF-палитрах «Отклонения в допуске», только без
        // заголовков. Рамки рисует сама палитра (OnPaint): у WinForms такого
        // вида из коробки нет.

        private readonly List<Rectangle> _groups = new List<Rectangle>();

        /// <summary>Начало блока: отступ от рамки; возвращает верх рамки.</summary>
        private int BeginGroup(ref int y, int gp)
        {
            int top = y;
            y += gp;
            return top;
        }

        private void EndGroup(int x0, int outerW, int top, ref int y, int gp, int after, bool apply)
        {
            y += gp;
            if (apply)
                _groups.Add(new Rectangle(x0, top, outerW, y - top));
            y += after;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            int u = Font.Height;

            // Плашки под подписями и тумблерами — со слегка скруглёнными углами.
            using (var plate = new SolidBrush(_theme.Plate))
                foreach (Control c in Controls)
                {
                    if (!(c is Label) && !(c is ToggleSwitch)) continue;
                    if (!c.Visible || c.Width <= 0 || c.Height <= 0) continue;
                    using (GraphicsPath p = RoundRect(new RectangleF(c.Left, c.Top, c.Width, c.Height), u / 4f))
                        g.FillPath(plate, p);
                }

            using (var pen = new Pen(_theme.GroupBorder))
                foreach (Rectangle r in _groups)
                    using (GraphicsPath p = RoundRect(new RectangleF(r.X + 0.5f, r.Y + 0.5f, r.Width - 1, r.Height - 1), u / 3f))
                        g.DrawPath(pen, p);
        }

        private static GraphicsPath RoundRect(RectangleF r, float rad)
        {
            var p = new GraphicsPath();
            float d = rad * 2;
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        /// <summary>Подпись во всю ширину с переносом по словам.</summary>
        private void Stack(Label l, int x, ref int y, int width, int after, bool apply)
        {
            // Высоту считает сам Label — тем же способом, каким он потом рисует:
            // свой замер расходился на строку и наезжал на соседнюю подпись.
            if (!Equals(l.Font, Font)) l.Font = Font;
            Size s = l.GetPreferredSize(new Size(width, 0));
            if (apply) l.SetBounds(x, y, width, s.Height);
            y += s.Height + after;
        }
    }

    /// <summary>
    /// Переключатель в духе iOS: дорожка со скользящим кругляшом. Если задан
    /// LeftText — это выбор из двух («Уменьшить ◯ Увеличить»), активная сторона
    /// подсвечена; иначе обычный вкл/выкл с подписью справа.
    /// </summary>
    internal sealed class ToggleSwitch : Control
    {
        private bool _checked;
        private float _pos;               // 0 — слева, 1 — справа (для анимации)
        private readonly Timer _anim = new Timer { Interval = 15 };
        private bool _hot;

        public Theme Theme = Theme.Dark;
        public string LeftText;
        public bool Bold;
        public event EventHandler CheckedChanged;

        public ToggleSwitch()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.Selectable |
                     ControlStyles.SupportsTransparentBackColor, true);
            Cursor = Cursors.Hand;
            TabStop = true;
            _anim.Tick += (s, e) =>
            {
                float target = _checked ? 1f : 0f;
                _pos += Math.Sign(target - _pos) * 0.2f;
                if (Math.Abs(target - _pos) < 0.2f) { _pos = target; _anim.Stop(); }
                Invalidate();
            };
        }

        public bool Checked
        {
            get { return _checked; }
            set
            {
                if (_checked == value) return;
                _checked = value;
                if (IsHandleCreated && Visible) _anim.Start(); else _pos = value ? 1f : 0f;
                Invalidate();
                if (CheckedChanged != null) CheckedChanged(this, EventArgs.Empty);
            }
        }

        private int TrackH { get { return (int)(Font.Height * 1.15); } }
        private int TrackW { get { return (int)(TrackH * 1.85); } }
        private int Gap { get { return Font.Height / 2; } }

        /// <summary>Отступ, с которого начинается подпись, — для пояснений под ней.</summary>
        public int TextIndent { get { return LeftText == null ? TrackW + Gap : 0; } }

        private Font TextFont { get { return Bold ? new Font(Font, FontStyle.Bold) : Font; } }

        public int PreferredHeight(int width)
        {
            using (Font f = Bold ? new Font(Font, FontStyle.Bold) : null)
            {
                Font ff = f ?? Font;
                if (LeftText != null)
                    return Math.Max(TrackH, ff.Height) + 4;
                Size s = TextRenderer.MeasureText(Text, ff, new Size(Math.Max(10, width - TrackW - Gap), int.MaxValue),
                    TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);
                return Math.Max(TrackH, s.Height) + 4;
            }
        }

        protected override void OnClick(EventArgs e) { Focus(); Checked = !Checked; base.OnClick(e); }
        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Space) { Checked = !Checked; e.Handled = true; }
            base.OnKeyDown(e);
        }
        protected override void OnMouseEnter(EventArgs e) { _hot = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hot = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
        protected override void OnTextChanged(EventArgs e) { Invalidate(); base.OnTextChanged(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            Font f = TextFont;
            try
            {
                int th = TrackH, tw = TrackW, gap = Gap;
                int ty = (Height - th) / 2;
                int tx;

                if (LeftText != null)
                {
                    // «Уменьшить [◯] Увеличить», по центру.
                    Size l = TextRenderer.MeasureText(LeftText, f);
                    Size r = TextRenderer.MeasureText(Text, f);
                    int total = l.Width + gap + tw + gap + r.Width;
                    int x0 = Math.Max(0, (Width - total) / 2);
                    TextRenderer.DrawText(g, LeftText, f, new Point(x0, (Height - l.Height) / 2),
                        _checked ? Theme.Muted : Theme.Text, TextFormatFlags.NoPadding);
                    tx = x0 + l.Width + gap;
                    TextRenderer.DrawText(g, Text, f, new Point(tx + tw + gap, (Height - r.Height) / 2),
                        _checked ? Theme.Text : Theme.Muted, TextFormatFlags.NoPadding);
                }
                else
                {
                    tx = 0;
                    var rect = new Rectangle(tw + gap, 0, Width - tw - gap, Height);
                    TextRenderer.DrawText(g, Text, f, rect, Theme.Text,
                        TextFormatFlags.WordBreak | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                }

                // Дорожка — титан, как на значках ленты. Обычный вкл/выкл в
                // выключенном положении серый, при включении титан проявляется;
                // выбор из двух — всегда титан: обе стороны «включены».
                float on = LeftText != null ? 1f : _pos;
                var trackRect = new RectangleF(tx, ty, tw, th);
                using (GraphicsPath p = Pill(trackRect))
                {
                    using (var b = new SolidBrush(Theme.TrackOff))
                        g.FillPath(b, p);
                    if (on > 0)
                        using (LinearGradientBrush b = TitaniumBrush(trackRect, (int)(255 * on)))
                            g.FillPath(b, p);
                    if (_hot)
                        using (var b = new SolidBrush(Color.FromArgb(36, 255, 255, 255)))
                            g.FillPath(b, p);
                    using (var pen = new Pen(Color.FromArgb(200, 0x2B, 0x30, 0x36)))
                        g.DrawPath(pen, p);
                }

                float k = th - 4;
                float kx = tx + 2 + _pos * (tw - th);
                using (var b = new SolidBrush(Color.FromArgb(70, 0, 0, 0)))
                    g.FillEllipse(b, kx, ty + 3, k, k);
                using (var b = new SolidBrush(Theme.Knob))
                    g.FillEllipse(b, kx, ty + 2, k, k);
                using (var pen = new Pen(Color.FromArgb(120, 0x2B, 0x30, 0x36)))
                    g.DrawEllipse(pen, kx, ty + 2, k, k);

                if (Focused && ShowFocusCues)
                    using (var pen = new Pen(Theme.Accent) { DashStyle = DashStyle.Dot })
                    using (GraphicsPath p = Pill(new RectangleF(tx - 2, ty - 2, tw + 3, th + 3)))
                        g.DrawPath(pen, p);
            }
            finally
            {
                if (!ReferenceEquals(f, Font)) f.Dispose();
            }
        }

        /// <summary>
        /// Титан, как на значках ленты: блик — холодный серый — тёмная кромка,
        /// по диагонали. alpha — для плавного проявления при включении.
        /// </summary>
        private static LinearGradientBrush TitaniumBrush(RectangleF r, int alpha)
        {
            var b = new LinearGradientBrush(r, Color.White, Color.Black, 60f);
            b.InterpolationColors = new ColorBlend
            {
                Colors = new[]
                {
                    Color.FromArgb(alpha, 0xF6, 0xF8, 0xFA), Color.FromArgb(alpha, 0xCE, 0xD4, 0xDA),
                    Color.FromArgb(alpha, 0x97, 0x9F, 0xA8), Color.FromArgb(alpha, 0x6C, 0x74, 0x7D),
                    Color.FromArgb(alpha, 0x41, 0x47, 0x4E),
                },
                Positions = new[] { 0f, 0.26f, 0.54f, 0.78f, 1f },
            };
            return b;
        }

        private static GraphicsPath Pill(RectangleF r)
        {
            var p = new GraphicsPath();
            float d = r.Height;
            p.AddArc(r.X, r.Y, d, d, 90, 180);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 180);
            p.CloseFigure();
            return p;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _anim.Dispose();
            base.Dispose(disposing);
        }
    }

    /// <summary>Плоская кнопка: обычная и выбранная (акцентная) — как в палитрах AutoCAD.</summary>
    internal sealed class FlatButton : Control
    {
        public Theme Theme = Theme.Dark;
        private bool _active, _hot;

        public FlatButton()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint | ControlStyles.ResizeRedraw |
                     ControlStyles.SupportsTransparentBackColor, true);
            Cursor = Cursors.Hand;
        }

        public bool Active { get { return _active; } set { _active = value; Invalidate(); } }

        protected override void OnMouseEnter(EventArgs e) { _hot = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hot = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnTextChanged(EventArgs e) { Invalidate(); base.OnTextChanged(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Color fill = _active ? Theme.Accent : (_hot ? Theme.ButtonHot : Theme.Button);
            using (var b = new SolidBrush(fill))
            using (GraphicsPath p = Rounded(new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f), Font.Height / 4f))
                g.FillPath(b, p);
            TextRenderer.DrawText(g, Text, Font, ClientRectangle, _active ? Theme.AccentText : Theme.Text,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }

        private static GraphicsPath Rounded(RectangleF r, float rad)
        {
            var p = new GraphicsPath();
            float d = rad * 2;
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }
    }
}
