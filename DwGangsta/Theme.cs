using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows.Forms;

// Оформление диалогов — то же, что у палитры ZoomZoom: тёмная и светлая тема
// под интерфейс AutoCAD, тумблеры с титановой дорожкой, плоские кнопки,
// плашки под подписями. Ссылок на AutoCAD здесь нет — только WinForms:
// так диалоги можно отрисовать в картинку без запуска AutoCAD.

namespace DwGangsta
{
    /// <summary>Цвета диалогов в духе AutoCAD: тёмная и светлая тема.</summary>
    public sealed class Theme
    {
        public Color Back, Field, FieldBorder, Text, Muted, Track, TrackOff, Knob, Accent, AccentText,
                     Button, ButtonHot, GroupBorder, Plate, Selection, Danger;
        public bool IsDark;

        public static readonly Theme Dark = new Theme
        {
            IsDark = true,
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
            Selection = Color.FromArgb(44, 78, 118),
            Danger = Color.FromArgb(220, 70, 70),
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
            Selection = Color.FromArgb(204, 232, 247),
            Danger = Color.FromArgb(200, 50, 50),
        };

        /// <summary>COLORTHEME: 0 — тёмная тема интерфейса AutoCAD, 1 — светлая.</summary>
        public static Theme FromColorTheme(int colorTheme)
        {
            return colorTheme == 1 ? Light : Dark;
        }

        /// <summary>Цвет для недоступного элемента: наполовину растворён в фоне.</summary>
        internal Color Faded(Color c)
        {
            return Color.FromArgb((c.R + Back.R) / 2, (c.G + Back.G) / 2, (c.B + Back.B) / 2);
        }
    }

    /// <summary>Элемент, который сам рисует себя в цветах темы.</summary>
    internal interface IThemed
    {
        Theme Theme { get; set; }
    }

    /// <summary>Общие кисти и контуры: титан, скруглённый прямоугольник, «таблетка».</summary>
    internal static class Draw
    {
        /// <summary>
        /// Титан, как на значках ленты ZoomZoom: блик — холодный серый — тёмная
        /// кромка, по диагонали. alpha — для плавного проявления при включении.
        /// </summary>
        public static LinearGradientBrush Titanium(RectangleF r, int alpha)
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

        public static readonly Color Rim = Color.FromArgb(200, 0x2B, 0x30, 0x36);

        public static GraphicsPath RoundRect(RectangleF r, float rad)
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

        /// <summary>
        /// Полосы прокрутки списка — в цвет темы. Тёмные есть только в Windows 10
        /// 1809 и новее; на старых остаются системными.
        /// </summary>
        public static void ScrollBars(Control c, Theme theme)
        {
            if (!c.IsHandleCreated) return;
            try { SetWindowTheme(c.Handle, theme.IsDark ? "DarkMode_Explorer" : "Explorer", null); }
            catch (Exception) { }
        }

        [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
        static extern int SetWindowTheme(IntPtr hwnd, string appName, string idList);

        public static GraphicsPath Pill(RectangleF r)
        {
            var p = new GraphicsPath();
            float d = r.Height;
            p.AddArc(r.X, r.Y, d, d, 90, 180);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 180);
            p.CloseFigure();
            return p;
        }
    }

    /// <summary>Как выглядит подпись: на плашке, приглушённая или заголовок блока.</summary>
    internal enum LabelRole { Text, Hint, Heading }

    /// <summary>
    /// Подпись в цветах темы. Рисует текст сама: недоступный обычный Label
    /// WinForms выводит серым «выдавленным» текстом, на тёмной теме это грязь.
    /// </summary>
    internal sealed class ThemedLabel : Label, IThemed
    {
        public Theme Theme { get; set; } = Theme.Dark;
        public LabelRole Role;

        public ThemedLabel()
        {
            AutoSize = false;
            UseMnemonic = false;
            BackColor = Color.Transparent;
        }

        protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Color c = Role == LabelRole.Text ? Theme.Text : Theme.Muted;
            if (!Enabled) c = Theme.Faded(Theme.Muted);
            var flags = TextFormatFlags.NoPrefix | (AutoEllipsis
                ? TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.VerticalCenter
                : TextFormatFlags.WordBreak);
            TextRenderer.DrawText(e.Graphics, Text, Font, ClientRectangle.Shrink(Padding), c, flags);
        }
    }

    static class RectangleExtensions
    {
        public static Rectangle Shrink(this Rectangle r, Padding p)
        {
            return new Rectangle(r.X + p.Left, r.Y + p.Top, r.Width - p.Horizontal, r.Height - p.Vertical);
        }
    }

    /// <summary>
    /// Переключатель в духе iOS: дорожка со скользящим кругляшом и подпись
    /// справа, с переносом по словам. Заменяет флажок.
    /// </summary>
    internal sealed class ToggleSwitch : Control, IThemed
    {
        bool _checked;
        float _pos;                 // 0 — слева, 1 — справа (для анимации)
        readonly Timer _anim = new Timer { Interval = 15 };
        bool _hot;

        public Theme Theme { get; set; } = Theme.Dark;
        public event EventHandler CheckedChanged;

        public ToggleSwitch()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.Selectable |
                     ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Transparent;
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
                CheckedChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        int TrackH => (int)(Font.Height * 1.15);
        int TrackW => (int)(TrackH * 1.85);
        int Gap => Font.Height / 2;

        /// <summary>Отступ, с которого начинается подпись, — для вложенных настроек.</summary>
        public int TextIndent => TrackW + Gap;

        public int PreferredHeight(int width)
        {
            Size s = TextRenderer.MeasureText(Text, Font, new Size(Math.Max(10, width - TextIndent - 4), int.MaxValue),
                TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);
            return Math.Max(TrackH, s.Height) + 6;
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
        protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            int th = TrackH, tw = TrackW;
            int ty = (Height - th) / 2;
            bool on = Enabled;

            var rect = new Rectangle(TextIndent, 0, Width - TextIndent - 4, Height);
            TextRenderer.DrawText(g, Text, Font, rect, on ? Theme.Text : Theme.Faded(Theme.Muted),
                TextFormatFlags.WordBreak | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);

            // Дорожка — титан, как на значках ленты: в выключенном положении
            // серая, при включении титан проявляется.
            var trackRect = new RectangleF(0, ty, tw, th);
            using (GraphicsPath p = Draw.Pill(trackRect))
            {
                using (var b = new SolidBrush(on ? Theme.TrackOff : Theme.Faded(Theme.TrackOff)))
                    g.FillPath(b, p);
                if (_pos > 0)
                    using (LinearGradientBrush b = Draw.Titanium(trackRect, (int)((on ? 255 : 110) * _pos)))
                        g.FillPath(b, p);
                if (_hot && on)
                    using (var b = new SolidBrush(Color.FromArgb(36, 255, 255, 255)))
                        g.FillPath(b, p);
                using (var pen = new Pen(Draw.Rim))
                    g.DrawPath(pen, p);
            }

            float k = th - 4;
            float kx = 2 + _pos * (tw - th);
            using (var b = new SolidBrush(Color.FromArgb(on ? 70 : 30, 0, 0, 0)))
                g.FillEllipse(b, kx, ty + 3, k, k);
            using (var b = new SolidBrush(on ? Theme.Knob : Theme.Faded(Theme.Knob)))
                g.FillEllipse(b, kx, ty + 2, k, k);
            using (var pen = new Pen(Color.FromArgb(120, 0x2B, 0x30, 0x36)))
                g.DrawEllipse(pen, kx, ty + 2, k, k);

            if (Focused && ShowFocusCues)
                using (var pen = new Pen(Theme.Accent) { DashStyle = DashStyle.Dot })
                using (GraphicsPath p = Draw.Pill(new RectangleF(-1, ty - 2, tw + 3, th + 3)))
                    g.DrawPath(pen, p);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _anim.Dispose();
            base.Dispose(disposing);
        }
    }

    /// <summary>
    /// Выбор одного из нескольких: титановое гнездо с белой точкой. Соседи с тем
    /// же родителем гасятся сами, как у обычной радиокнопки.
    /// </summary>
    internal sealed class RadioOption : Control, IThemed
    {
        bool _checked, _hot;

        public Theme Theme { get; set; } = Theme.Dark;
        public event EventHandler CheckedChanged;

        public RadioOption()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.Selectable |
                     ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Transparent;
            Cursor = Cursors.Hand;
            TabStop = true;
        }

        public bool Checked
        {
            get { return _checked; }
            set
            {
                if (_checked == value) return;
                _checked = value;
                if (value && Parent != null)
                    foreach (Control c in Parent.Controls)
                        if (c != this && c is RadioOption other) other.Checked = false;
                Invalidate();
                CheckedChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        int Dot => (int)(Font.Height * 1.15);
        int Gap => Font.Height / 2;

        public int PreferredHeight(int width)
        {
            Size s = TextRenderer.MeasureText(Text, Font, new Size(Math.Max(10, width - Dot - Gap - 4), int.MaxValue),
                TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);
            return Math.Max(Dot, s.Height) + 6;
        }

        protected override void OnClick(EventArgs e) { Focus(); Checked = true; base.OnClick(e); }
        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Space) { Checked = true; e.Handled = true; }
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

            int d = Dot;
            var rect = new Rectangle(d + Gap, 0, Width - d - Gap - 4, Height);
            TextRenderer.DrawText(g, Text, Font, rect, Theme.Text,
                TextFormatFlags.WordBreak | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);

            var ring = new RectangleF(0.5f, (Height - d) / 2f, d - 1, d - 1);
            using (var b = new SolidBrush(Theme.TrackOff))
                g.FillEllipse(b, ring);
            if (_checked)
                using (LinearGradientBrush b = Draw.Titanium(ring, 255))
                    g.FillEllipse(b, ring);
            if (_hot)
                using (var b = new SolidBrush(Color.FromArgb(36, 255, 255, 255)))
                    g.FillEllipse(b, ring);
            using (var pen = new Pen(Draw.Rim))
                g.DrawEllipse(pen, ring);

            if (_checked)
            {
                float k = d * 0.42f;
                var dot = new RectangleF(ring.X + (ring.Width - k) / 2, ring.Y + (ring.Height - k) / 2, k, k);
                using (var b = new SolidBrush(Color.FromArgb(70, 0, 0, 0)))
                    g.FillEllipse(b, dot.X, dot.Y + 1, k, k);
                using (var b = new SolidBrush(Theme.Knob))
                    g.FillEllipse(b, dot);
                using (var pen = new Pen(Color.FromArgb(120, 0x2B, 0x30, 0x36)))
                    g.DrawEllipse(pen, dot);
            }

            if (Focused && ShowFocusCues)
                using (var pen = new Pen(Theme.Accent) { DashStyle = DashStyle.Dot })
                    g.DrawEllipse(pen, ring.X - 1.5f, ring.Y - 1.5f, ring.Width + 3, ring.Height + 3);
        }
    }

    /// <summary>
    /// Плоская кнопка: обычная и главная (акцентная) — как в палитрах AutoCAD.
    /// Умеет быть кнопкой «ОК» и «Отмена» формы: Enter и Esc работают как обычно.
    /// </summary>
    internal sealed class FlatButton : Control, IThemed, IButtonControl
    {
        bool _hot, _down;

        public Theme Theme { get; set; } = Theme.Dark;

        public DialogResult DialogResult { get; set; }

        public FlatButton()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.Selectable |
                     ControlStyles.SupportsTransparentBackColor | ControlStyles.StandardClick, true);
            BackColor = Color.Transparent;
            Cursor = Cursors.Hand;
            TabStop = true;
        }

        public void NotifyDefault(bool value) { }

        public void PerformClick()
        {
            if (CanSelect) OnClick(EventArgs.Empty);
        }

        /// <summary>Как у обычной кнопки: результат формы ставится до обработчиков Click.</summary>
        protected override void OnClick(EventArgs e)
        {
            Form form = FindForm();
            if (form != null && DialogResult != DialogResult.None) form.DialogResult = DialogResult;
            base.OnClick(e);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Space || e.KeyCode == Keys.Enter) { PerformClick(); e.Handled = true; }
            base.OnKeyDown(e);
        }

        protected override void OnMouseEnter(EventArgs e) { _hot = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hot = false; _down = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { _down = true; Invalidate(); base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { _down = false; Invalidate(); base.OnMouseUp(e); }
        protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
        protected override void OnTextChanged(EventArgs e) { Invalidate(); base.OnTextChanged(e); }
        protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            // Все кнопки серые, и главная («Собрать», «Разделить») тоже: палитра в одном
            // спокойном тоне, без цветного акцента.
            Color fill = _hot ? Theme.ButtonHot : Theme.Button;
            if (_down) fill = Theme.IsDark ? ControlPaint.Light(fill, 0.1f) : ControlPaint.Dark(fill, 0.05f);
            if (!Enabled) fill = Theme.Faded(fill);
            Color text = Theme.Text;
            if (!Enabled) text = Theme.Faded(text);

            var r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
            using (var b = new SolidBrush(fill))
            using (GraphicsPath p = Draw.RoundRect(r, Font.Height / 4f))
                g.FillPath(b, p);

            TextRenderer.DrawText(g, Text, Font, ClientRectangle, text,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
                TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);

            if (Focused && ShowFocusCues)
                using (var pen = new Pen(Theme.Accent) { DashStyle = DashStyle.Dot })
                using (GraphicsPath p = Draw.RoundRect(new RectangleF(2.5f, 2.5f, Width - 6, Height - 6), Font.Height / 5f))
                    g.DrawPath(pen, p);
        }
    }

    /// <summary>
    /// Рамка поля: тонкая обводка, при фокусе — акцентная, при ошибке — красная.
    /// Внутри лежит само поле без своей рамки: системная рамка на тёмной теме
    /// светится белым и выпадает из палитры.
    /// </summary>
    internal class FieldFrame : Panel, IThemed
    {
        readonly Control _inner;
        Theme _theme = Theme.Dark;

        /// <summary>Значение в поле негодное — рамка красная.</summary>
        public bool Invalid;

        public FieldFrame(Control inner, bool centerText)
        {
            _inner = inner;
            _centerText = centerText;
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            if (inner is TextBox tb) tb.BorderStyle = BorderStyle.None;
            if (inner is ListBox lb) lb.BorderStyle = BorderStyle.None;
            Controls.Add(inner);
            inner.GotFocus += (s, e) => Invalidate();
            inner.LostFocus += (s, e) => Invalidate();
        }

        readonly bool _centerText;

        public Theme Theme
        {
            get { return _theme; }
            set
            {
                _theme = value;
                BackColor = value.Field;
                _inner.BackColor = value.Field;
                _inner.ForeColor = value.Text;
                Invalidate(true);
            }
        }

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            int pad = Font.Height / 4;
            if (_centerText)
            {
                int h = _inner.PreferredSize.Height;
                _inner.SetBounds(pad, Math.Max(1, (Height - h) / 2), Width - pad * 2, h);
            }
            else
            {
                _inner.SetBounds(1, 1, Width - 2, Height - 2);
            }
        }

        protected override void OnEnabledChanged(EventArgs e)
        {
            base.OnEnabledChanged(e);
            _inner.BackColor = Enabled ? _theme.Field : _theme.Back;
            BackColor = _inner.BackColor;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Color c = Invalid ? _theme.Danger
                    : _inner.Focused ? _theme.Accent
                    : Enabled ? _theme.FieldBorder : _theme.Faded(_theme.FieldBorder);
            using (var pen = new Pen(c))
                e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
        }
    }

    /// <summary>
    /// Целое число в рамке — вместо счётчика со стрелками: у того стрелки
    /// системные и на тёмной теме светятся. Негодное значение подсвечивается,
    /// при уходе из поля возвращается последнее годное.
    /// </summary>
    internal sealed class NumberField : FieldFrame
    {
        readonly TextBox _box;
        decimal _value;

        public decimal Minimum, Maximum = 100;

        public NumberField() : this(new TextBox { TextAlign = HorizontalAlignment.Right }) { }

        NumberField(TextBox box) : base(box, true)
        {
            _box = box;
            _box.TextChanged += (s, e) =>
            {
                decimal v;
                Invalid = !TryParse(_box.Text, out v);
                if (!Invalid) _value = v;
                Invalidate();
            };
            _box.Leave += (s, e) => _box.Text = Format(_value);
            _box.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Up) { Value = Math.Min(Maximum, _value + 1); e.SuppressKeyPress = true; }
                if (e.KeyCode == Keys.Down) { Value = Math.Max(Minimum, _value - 1); e.SuppressKeyPress = true; }
            };
        }

        public decimal Value
        {
            get { return _value; }
            set { _value = Math.Max(Minimum, Math.Min(Maximum, value)); _box.Text = Format(_value); }
        }

        bool TryParse(string s, out decimal v)
        {
            s = (s ?? "").Trim().Replace(" ", "").Replace(',', '.');
            return decimal.TryParse(s, NumberStyles.Number, CultureInfo.InvariantCulture, out v)
                   && v >= Minimum && v <= Maximum;
        }

        static string Format(decimal v)
        {
            return v.ToString("0.##", CultureInfo.CurrentCulture);
        }
    }

    /// <summary>
    /// Список с флажками в цветах темы. Стандартный CheckedListBox рисует
    /// флажки системными и не даёт рисовать строки самому.
    /// </summary>
    internal sealed class CheckList : ListBox, IThemed
    {
        Theme _theme = Theme.Dark;

        public Theme Theme
        {
            get { return _theme; }
            set { _theme = value; Draw.ScrollBars(this, value); Invalidate(); }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Draw.ScrollBars(this, _theme);
        }

        public CheckList()
        {
            DrawMode = DrawMode.OwnerDrawFixed;
            IntegralHeight = false;
            SelectionMode = SelectionMode.One;
        }

        sealed class Entry
        {
            public string Text;
            public bool Checked;
            public override string ToString() { return Text; }
        }

        protected override void OnFontChanged(EventArgs e)
        {
            base.OnFontChanged(e);
            ItemHeight = Font.Height + Font.Height / 2;
        }

        public void Add(string text, bool isChecked)
        {
            Items.Add(new Entry { Text = text, Checked = isChecked });
        }

        public void SetItemChecked(int index, bool value)
        {
            ((Entry)Items[index]).Checked = value;
            Invalidate(GetItemRectangle(index));
        }

        public System.Collections.Generic.List<string> CheckedNames()
        {
            var names = new System.Collections.Generic.List<string>();
            foreach (Entry entry in Items) if (entry.Checked) names.Add(entry.Text);
            return names;
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            int index = IndexFromPoint(e.Location);
            if (index >= 0 && e.Button == MouseButtons.Left)
                SetItemChecked(index, !((Entry)Items[index]).Checked);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Space && SelectedIndex >= 0)
            {
                SetItemChecked(SelectedIndex, !((Entry)Items[SelectedIndex]).Checked);
                e.Handled = true;
            }
            base.OnKeyDown(e);
        }

        protected override void OnDrawItem(DrawItemEventArgs e)
        {
            if (e.Index < 0) return;
            Graphics g = e.Graphics;
            var entry = (Entry)Items[e.Index];
            bool selected = (e.State & DrawItemState.Selected) != 0 && Focused;

            using (var b = new SolidBrush(selected ? _theme.Selection : _theme.Field))
                g.FillRectangle(b, e.Bounds);

            int u = Font.Height;
            int box = u - 2;
            var r = new RectangleF(e.Bounds.X + u / 2, e.Bounds.Y + (e.Bounds.Height - box) / 2f, box, box);

            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (GraphicsPath p = Draw.RoundRect(r, u / 6f))
            {
                if (entry.Checked)
                    using (LinearGradientBrush b = Draw.Titanium(r, 255))
                        g.FillPath(b, p);
                else
                    using (var b = new SolidBrush(Theme.Field))
                        g.FillPath(b, p);
                using (var pen = new Pen(entry.Checked ? Draw.Rim : Theme.FieldBorder))
                    g.DrawPath(pen, p);
            }
            if (entry.Checked)
                using (var pen = new Pen(Color.FromArgb(0x2B, 0x30, 0x36), Math.Max(1.6f, u / 8f))
                       { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
                    g.DrawLines(pen, new[]
                    {
                        new PointF(r.X + r.Width * 0.24f, r.Y + r.Height * 0.52f),
                        new PointF(r.X + r.Width * 0.43f, r.Y + r.Height * 0.72f),
                        new PointF(r.X + r.Width * 0.78f, r.Y + r.Height * 0.30f),
                    });
            g.SmoothingMode = SmoothingMode.None;

            var text = new Rectangle((int)r.Right + u / 2, e.Bounds.Y, e.Bounds.Right - (int)r.Right - u, e.Bounds.Height);
            TextRenderer.DrawText(g, entry.Text, Font, text, Theme.Text,
                TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        }
    }

    /// <summary>
    /// Список путей в цветах темы. Длинный путь сокращается в середине —
    /// начало и имя файла видны всегда, горизонтальная прокрутка не нужна.
    /// </summary>
    internal sealed class PathList : ListBox, IThemed
    {
        Theme _theme = Theme.Dark;

        public Theme Theme
        {
            get { return _theme; }
            set { _theme = value; Draw.ScrollBars(this, value); Invalidate(); }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Draw.ScrollBars(this, _theme);
        }

        public PathList()
        {
            DrawMode = DrawMode.OwnerDrawFixed;
            IntegralHeight = false;
        }

        protected override void OnFontChanged(EventArgs e)
        {
            base.OnFontChanged(e);
            ItemHeight = Font.Height + Font.Height / 3;
        }

        protected override void OnDrawItem(DrawItemEventArgs e)
        {
            if (e.Index < 0) return;
            bool selected = (e.State & DrawItemState.Selected) != 0;
            using (var b = new SolidBrush(selected ? _theme.Selection : _theme.Field))
                e.Graphics.FillRectangle(b, e.Bounds);

            int pad = Font.Height / 3;
            var r = new Rectangle(e.Bounds.X + pad, e.Bounds.Y, e.Bounds.Width - pad * 2, e.Bounds.Height);
            TextRenderer.DrawText(e.Graphics, Items[e.Index].ToString(), Font, r, Theme.Text,
                TextFormatFlags.VerticalCenter | TextFormatFlags.PathEllipsis | TextFormatFlags.NoPrefix);
        }
    }
}
