using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace DwGangsta
{
    /// <summary>
    /// Основа вкладок палитры листов и чертежей — в духе палитры ZoomZoom. Раскладка
    /// пересчитывается при каждом изменении ширины. В широкой палитре блоки стоят в
    /// две колонки (мониторы горизонтальные, а палитры AutoCAD по привычке узкие и
    /// высокие); в узкой — например, пристыкованной к краю — одной стопкой. Подписи
    /// переносятся, кнопки перестраиваются рядами, горизонтальной прокрутки не бывает
    /// вовсе. Список файлов вкладки тянется по высоте вслед за окном (Stretch), а
    /// вертикальная прокрутка появляется, только когда он уже сжат до минимума. Все
    /// размеры — от высоты шрифта, поэтому при любом масштабе Windows палитра выглядит
    /// одинаково.
    ///
    /// Блоки обведены тонкой рамкой со скруглёнными углами, под подписями и
    /// переключателями — скруглённые плашки. Рамки и плашки рисует сама панель:
    /// элементы прозрачные и стоят прямо на ней.
    /// </summary>
    public abstract class ThemedPanel : UserControl
    {
        /// <summary>
        /// Заголовок палитры (на её вертикальной полосе) и окон сообщений — как у палитры
        /// настроек: это одна программа, ZoomZoom.
        /// </summary>
        internal const string Title = "ZoomZoom";

        internal Theme Theme { get; private set; } = Theme.Dark;

        readonly List<Rectangle> _groups = new List<Rectangle>();
        bool _apply;          // раскладка расставляет элементы, а не только считает высоту
        int _oy;              // сдвиг прокрутки
        bool _arranging;
        bool _ready;          // наследник создал свои элементы — раскладывать есть что
        int _stretch;         // на сколько палитра выше (+) или ниже (−) содержимого

        /// <summary>
        /// Высота, которую палитра даёт сверх обычной раскладки (или отнимает у неё):
        /// её получает список файлов вкладки, и список тянется вслед за окном. Список не
        /// бывает короче своего минимума — дальше появляется прокрутка. При замере
        /// размера по умолчанию — ноль.
        /// </summary>
        internal int Stretch => _stretch;

        protected ThemedPanel()
        {
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.ResizeRedraw, true);
            AutoScroll = true;
            // Размеры считаются от шрифта сами — масштабировать их ещё раз не нужно.
            AutoScaleMode = AutoScaleMode.None;
            Font = new Font("Segoe UI", 9f, FontStyle.Regular, GraphicsUnit.Point);
        }

        /// <summary>Единица раскладки — высота строки шрифта.</summary>
        internal int U => Font.Height;

        /// <summary>Тёмная тема — как у AutoCAD с COLORTHEME = 0, светлая — с 1.</summary>
        public void ApplyTheme(Theme theme)
        {
            Theme = theme ?? Theme.Dark;
            BackColor = Theme.Back;
            ForeColor = Theme.Text;
            Restyle(this);
            Invalidate(true);
        }

        void Restyle(Control parent)
        {
            foreach (Control c in parent.Controls)
            {
                if (c is IThemed themed) themed.Theme = Theme;
                Restyle(c);
            }
        }

        // ------------------------------------------------------------- раскладка

        /// <summary>
        /// Раскладка сверху вниз на заданную ширину; возвращает высоту содержимого.
        /// Элементы ставятся только через помощники ниже: они сами учитывают прокрутку
        /// и в режиме замера ничего не двигают.
        /// </summary>
        protected abstract int Arrange(int width);

        /// <summary>
        /// Наследник вызывает в конце своего конструктора: до этого его элементов ещё нет,
        /// а раскладку WinForms запускает уже при установке шрифта в конструкторе основы.
        /// </summary>
        protected void Ready()
        {
            _ready = true;
            PerformLayout();
        }

        /// <summary>Высота содержимого при заданной ширине — по ней палитра выбирает размер.</summary>
        public int PreferredHeightFor(int width)
        {
            if (!_ready) return 0;
            bool was = _apply;
            int stretch = _stretch;
            _apply = false;
            _stretch = 0;
            try { return Arrange(width); }
            finally { _apply = was; _stretch = stretch; }
        }

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            if (_arranging || !_ready) return;
            _arranging = true;
            try
            {
                int width = Math.Max(U * 14, ClientSize.Width);

                // Сначала обычная высота содержимого, потом разница с окном — спискам.
                _apply = false;
                _stretch = 0;
                _stretch = ClientSize.Height - Arrange(width);

                _apply = true;
                _oy = AutoScrollPosition.Y;
                _groups.Clear();
                int height = Arrange(width);
                AutoScrollMinSize = new Size(0, height);     // ширина — никогда
            }
            finally
            {
                _apply = false;
                _arranging = false;
            }
            Invalidate();                                  // рамки групп переехали вместе с содержимым
        }

        // Прокрутка двигает элементы сама, а рамки групп рисуются по координатам
        // из раскладки — пересчитываем.
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

        /// <summary>Ставит элемент — в координатах содержимого, с учётом прокрутки.</summary>
        internal void Place(Control c, int x, int y, int width, int height)
        {
            if (_apply) c.SetBounds(x, y + _oy, Math.Max(1, width), Math.Max(1, height));
        }

        // ------------------------------------------------------------- элементы

        internal ThemedLabel MakeLabel(string text, LabelRole role = LabelRole.Text)
        {
            var l = new ThemedLabel { Text = text, Role = role, AutoEllipsis = true };
            if (role == LabelRole.Heading) l.Font = new Font(Font, FontStyle.Bold);
            else l.Padding = new Padding(U / 4, 2, U / 4, 2);
            return l;
        }

        internal ToggleSwitch MakeToggle(string text, bool isChecked)
        {
            return new ToggleSwitch { Text = text, Checked = isChecked };
        }

        internal RadioOption MakeRadio(string text, bool isChecked)
        {
            return new RadioOption { Text = text, Checked = isChecked };
        }

        internal FlatButton MakeButton(string text)
        {
            return new FlatButton { Text = text };
        }

        /// <summary>Всплывающие подсказки панели — одна на всю панель, живёт вместе с ней.</summary>
        ToolTip _tips;

        /// <summary>Подсказка при наведении мыши.</summary>
        internal void Tip(Control control, string text)
        {
            if (_tips == null)
            {
                _tips = new ToolTip { AutoPopDelay = 20000, InitialDelay = 500, ReshowDelay = 200 };
                Disposed += (s, e) => _tips.Dispose();
            }
            _tips.SetToolTip(control, text);
        }

        /// <summary>Подпись во всю ширину с переносом по словам.</summary>
        internal void Stack(Label l, int x, ref int y, int width, int after)
        {
            // Высоту считает сам Label — тем же способом, каким он потом рисует.
            l.AutoEllipsis = false;
            Size s = l.GetPreferredSize(new Size(width, 0));
            Place(l, x, y, width, s.Height);
            y += s.Height + after;
        }

        /// <summary>Однострочная подпись: длинный текст обрезается многоточием.</summary>
        internal void Line(Label l, int x, ref int y, int width, int after)
        {
            int h = l.GetPreferredSize(new Size(int.MaxValue, 0)).Height;
            Place(l, x, y, width, h);
            y += h + after;
        }

        internal void Stack(ToggleSwitch t, int x, ref int y, int width, int after)
        {
            int h = t.PreferredHeight(width);
            Place(t, x, y, width, h);
            y += h + after;
        }

        internal void Stack(RadioOption r, int x, ref int y, int width, int after)
        {
            int h = r.PreferredHeight(width);
            Place(r, x, y, width, h);
            y += h + after;
        }

        /// <summary>Высота кнопки и однострочного поля.</summary>
        internal int RowH => U + U / 2 + 4;

        /// <summary>Ширина кнопки по подписи, но не уже min.</summary>
        internal int ButtonWidth(string text, int min)
        {
            return Math.Max(min, TextRenderer.MeasureText(text, Font).Width + U * 2);
        }

        /// <summary>
        /// Кнопки рядами поровну: сколько влезает по самой длинной подписи — столько
        /// в ряд. В широкой палитре все в одну строку, в узкой — по две, по одной.
        /// </summary>
        internal void Buttons(IList<FlatButton> buttons, int x, ref int y, int width, int after)
        {
            int need = 0;
            foreach (FlatButton b in buttons) need = Math.Max(need, ButtonWidth(b.Text, U * 5));
            int gap = U / 2;
            int perRow = Math.Max(1, Math.Min(buttons.Count, (width + gap) / (need + gap)));
            int bw = (width - gap * (perRow - 1)) / perRow;
            for (int i = 0; i < buttons.Count; i++)
                Place(buttons[i], x + (i % perRow) * (bw + gap), y + (i / perRow) * (RowH + gap), bw, RowH);
            int rows = (buttons.Count + perRow - 1) / perRow;
            y += rows * RowH + (rows - 1) * gap + after;
        }

        /// <summary>Поле во всю ширину и кнопка справа от него.</summary>
        internal void FieldWithButton(Control field, FlatButton button, int x, ref int y, int width, int after)
        {
            int bw = Math.Min(ButtonWidth(button.Text, U * 6), width / 2);
            Place(field, x, y, width - bw - U / 2, RowH);
            Place(button, x + width - bw, y, bw, RowH);
            y += RowH + after;
        }

        /// <summary>Подпись слева, числовое поле справа — в одну строку.</summary>
        internal void NumberRow(Label label, NumberField field, int x, ref int y, int width, int after)
        {
            int fieldW = U * 4;
            int labelW = width - fieldW - U / 2;
            int labelH = label.GetPreferredSize(new Size(labelW, 0)).Height;
            if (label is ThemedLabel tl) tl.AutoEllipsis = false;
            int h = Math.Max(RowH, labelH);
            Place(label, x, y + (h - labelH) / 2, labelW, labelH);
            Place(field, x + width - fieldW, y + (h - RowH) / 2, fieldW, RowH);
            y += h + after;
        }

        /// <summary>Главная кнопка вкладки — во всю ширину внизу.</summary>
        internal void MainButton(FlatButton button, int x, ref int y, int width, int after)
        {
            Place(button, x, y, width, RowH + 4);
            y += RowH + 4 + after;
        }

        /// <summary>Начало блока: возвращает верх рамки.</summary>
        internal int BeginGroup(ref int y, int pad)
        {
            int top = y;
            y += pad;
            return top;
        }

        internal void EndGroup(int x, int width, int top, ref int y, int pad, int after)
        {
            y += pad;
            if (_apply) _groups.Add(new Rectangle(x, top + _oy, width, y - top));
            y += after;
        }

        // ------------------------------------------------------------- колонки

        /// <summary>
        /// Блок в рамке, поставленный в точку (x, y) шириной width (вместе с рамкой).
        /// extra — сколько добавить ему в высоту, чтобы он сравнялся с соседом: блок со
        /// списком отдаёт её списку, остальные — просто удлиняют рамку снизу.
        /// Возвращает высоту блока вместе с рамкой.
        /// </summary>
        internal delegate int Block(int x, int y, int width, int extra);

        /// <summary>Самая узкая колонка, при которой блоки ещё ставятся в две колонки.</summary>
        const int MinColumnU = 19;

        /// <summary>Расстояние между колонками — то же, что между блоками по вертикали.</summary>
        internal int ColumnGap => U / 2;

        internal int ColumnWidth(int outer) => (outer - ColumnGap) / 2;

        /// <summary>Хватает ли ширины на две колонки.</summary>
        internal bool TwoColumns(int outer) => ColumnWidth(outer) >= U * MinColumnU;

        /// <summary>Высота блока без расстановки элементов — замер.</summary>
        internal int Measure(Block block, int width, int extra = 0)
        {
            bool was = _apply;
            _apply = false;
            try { return block(0, 0, width, extra); }
            finally { _apply = was; }
        }

        /// <summary>
        /// Два блока рядом, одной высоты: тот, что ниже, добирает высоту до соседа.
        /// stretch — добавка (или убавка) левому блоку со списком: ряд тянется вслед
        /// за окном, но не ниже правого соседа и не короче минимума списка.
        /// Возвращает высоту ряда.
        /// </summary>
        internal int Row(Block left, Block right, int x, int y, int outer, int stretch = 0)
        {
            int w = ColumnWidth(outer);
            int hl = Measure(left, w), hr = Measure(right, w);
            int h = Math.Max(hl, hr);
            if (stretch != 0)
                h = Math.Max(hr, Measure(left, w, h + stretch - hl));
            left(x, y, w, h - hl);
            right(x + outer - w, y, w, h - hr);
            return h;
        }

        /// <summary>Сообщение поверх палитры.</summary>
        internal void Say(string message)
        {
            MessageBox.Show(this, message, Title, MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        // ------------------------------------------------------------- рисование

        /// <summary>
        /// Фон — всегда цвет темы, а не свойство BackColor: AutoCAD, приняв содержимое
        /// в палитру, сбрасывает его фон на системный. Прозрачные подписи и переключатели
        /// берут фон отсюда же, вместе с плашками и рамками.
        /// </summary>
        protected override void OnPaintBackground(PaintEventArgs e)
        {
            using (var b = new SolidBrush(Theme.Back))
                e.Graphics.FillRectangle(b, ClientRectangle);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            int u = U;

            using (var plate = new SolidBrush(Theme.Plate))
                foreach (Control c in Controls)
                {
                    bool plated = c is ToggleSwitch || c is RadioOption ||
                                  (c is ThemedLabel l && l.Role != LabelRole.Heading);
                    if (!plated || !c.Visible || c.Width <= 0 || c.Height <= 0) continue;
                    if (c is Label && c.Text.Length == 0) continue;
                    using (GraphicsPath p = Draw.RoundRect(new RectangleF(c.Left, c.Top, c.Width, c.Height), u / 4f))
                        g.FillPath(plate, p);
                }

            using (var pen = new Pen(Theme.GroupBorder))
                foreach (Rectangle r in _groups)
                    using (GraphicsPath p = Draw.RoundRect(new RectangleF(r.X + 0.5f, r.Y + 0.5f, r.Width - 1, r.Height - 1), u / 3f))
                        g.DrawPath(pen, p);
        }
    }
}
