using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Windows;
using Microsoft.Win32;
using AcAp = Autodesk.AutoCAD.ApplicationServices.Application;

namespace DwGangsta
{
    /// <summary>
    /// Палитра AutoCAD «ZoomZoom» (бывшая «DwGangsta») — две вкладки: «Разделить по листам»
    /// и «Собрать в один». Палитра широкая: блоки на вкладках стоят в две колонки.
    /// Палитра немодальная: её держат открытой, пристыковывают к краю, работают в чертеже
    /// рядом с ней. Поэтому вкладка разделения сама следит за активным чертежом — сменили
    /// чертёж, добавили лист, сохранили новый файл, — список листов обновится.
    ///
    /// Кнопки вкладок не трогают чертёж сами: они отдают настройки команде (DWGSPLITRUN,
    /// DWGMERGERUN), а уже команда работает с документом — с блокировкой и отменой, как
    /// положено в AutoCAD.
    /// </summary>
    public static class DwGangstaPalette
    {
        public enum Tab { Split = 0, Merge = 1 }

        static PaletteSet _ps;
        static SplitPanel _split;
        static MergePanel _merge;
        static readonly HashSet<Document> Hooked = new HashSet<Document>();

        /// <summary>Настройки, отданные кнопкой вкладки, — их забирает команда.</summary>
        internal static SplitOptions PendingSplit;
        internal static MergeOptions PendingMerge;

        // Постоянный GUID: по нему AutoCAD запоминает положение и размер палитры
        // между сеансами. Менять нельзя — иначе палитра каждый раз вылезает заново.
        static readonly Guid PaletteId = new Guid("3E6B1D2A-7C49-4F0E-9B85-D41A6C2E7F90");

        /// <summary>Заголовок — на вертикальной полосе палитры, как у палитры настроек.</summary>
        const string PaletteTitle = ThemedPanel.Title;

        public static void Show(Tab tab)
        {
            if (_ps == null) Create();
            _ps.Visible = true;
            try { _ps.Activate((int)tab); }
            catch (System.Exception) { }

            // Заголовок AutoCAD хранит в профиле вместе с положением палитры и при
            // показе подставляет оттуда — у тех, кто открывал её раньше, там лежит
            // «DwGangsta». Поэтому имя задаём уже после показа, каждый раз.
            try
            {
                if (_ps.Name != PaletteTitle)
                    _ps.Name = PaletteTitle;
            }
            catch (System.Exception) { }

            // Первое открытие в этом профиле — плавающее окно размера по умолчанию.
            // Дальше размер и положение помнит AutoCAD.
            if (!IsSizedInProfile())
            {
                try { _ps.Dock = DockSides.None; }
                catch (System.Exception) { }
                ApplyDefaultSize();
                MarkSizedInProfile();
            }
            KeepOnScreen();
            RefreshSplit();
        }

        static void Create()
        {
            _ps = new PaletteSet(PaletteTitle, PaletteId);
            _ps.Style = PaletteSetStyles.ShowPropertiesMenu
                      | PaletteSetStyles.ShowAutoHideButton
                      | PaletteSetStyles.ShowCloseButton
                      | PaletteSetStyles.ShowTabForSingle;

            _split = new SplitPanel();
            _merge = new MergePanel();
            Theme theme = Commands.DialogTheme();
            _split.ApplyTheme(theme);
            _merge.ApplyTheme(theme);
            _split.Run += (s, options) => Send(ref PendingSplit, options, "._DWGSPLITRUN ");
            _merge.Run += (s, options) => Send(ref PendingMerge, options, "._DWGMERGERUN ");

            // Минимум — от шрифта, а не в пикселях: на мониторе с масштабом 150% и 200%
            // палитра не должна обрезать содержимое.
            int u = _split.Font.Height;
            _ps.MinimumSize = new Size(u * 16, u * 12);
            _ps.Add(ZoomZoom.Lang.T("Разделить по листам", "Split by Layouts"), _split);
            _ps.Add(ZoomZoom.Lang.T("Собрать в один", "Merge into One"), _merge);

            // Двойной щелчок по пустому месту — вернуть размер по умолчанию.
            _split.DoubleClick += (s, e) => ApplyDefaultSize();
            _merge.DoubleClick += (s, e) => ApplyDefaultSize();

            // Тема AutoCAD меняется на лету — палитра следом.
            AcAp.SystemVariableChanged += (s, e) =>
            {
                if (!string.Equals(e.Name, "COLORTHEME", StringComparison.OrdinalIgnoreCase)) return;
                Theme t = Commands.DialogTheme();
                _split.ApplyTheme(t);
                _merge.ApplyTheme(t);
            };

            // Чертёж сменился — листы другие.
            AcAp.DocumentManager.DocumentActivated += (s, e) => { Hook(e.Document); RefreshSplit(); };
            AcAp.DocumentManager.DocumentDestroyed += (s, e) => RefreshSplit();
            Hook(AcAp.DocumentManager.MdiActiveDocument);
        }

        /// <summary>
        /// После любой команды в чертеже — сверить листы и путь: добавили или
        /// переименовали лист, сохранили новый чертёж. Если ничего не изменилось,
        /// вкладка не перерисовывается.
        /// </summary>
        static void Hook(Document doc)
        {
            if (doc == null || !Hooked.Add(doc)) return;
            doc.CommandEnded += (s, e) => RefreshSplit();
            doc.BeginDocumentClose += (s, e) => Hooked.Remove(doc);
        }

        static void RefreshSplit()
        {
            if (_split == null || _ps == null || !_ps.Visible) return;
            Document doc = AcAp.DocumentManager.MdiActiveDocument;
            if (doc == null) { _split.LoadDrawing(null, new List<string>()); return; }

            try
            {
                Database db = doc.Database;
                string path = System.IO.File.Exists(db.Filename) ? db.Filename : "";
                // Только чтение — блокировка документа не нужна, а внутри обработчика
                // окончания команды она могла бы и не даться.
                _split.LoadDrawing(path, LayoutSplitter.GetLayoutNames(db));
            }
            catch (System.Exception ex)
            {
                Trace.Write("палитра: листы не прочитаны: " + ex.Message);
            }
        }

        /// <summary>Отдаёт настройки команде активного чертежа.</summary>
        static void Send<T>(ref T pending, T options, string command)
        {
            Document doc = AcAp.DocumentManager.MdiActiveDocument;
            if (doc == null)
            {
                MessageBox.Show(ZoomZoom.Lang.T("Откройте чертёж.", "Open a drawing first."), PaletteTitle,
                                MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            pending = options;
            doc.SendStringToExecute(command, true, false, true);
        }

        // ------------------------------------------------------------- размер и место

        /// <summary>
        /// Размер по умолчанию: широкий, в 52 строки шрифта — блоки встают в две колонки,
        /// каждая шириной в 24 строки (узкая палитра); высота — по более высокой из вкладок,
        /// но не выше экрана. Всё от шрифта: на HD со 100% и на 4K с 200% окно выглядит
        /// одинаково.
        /// </summary>
        static void ApplyDefaultSize()
        {
            if (!IsFloating()) return;
            try
            {
                int u = _split.Font.Height;
                Size frame = _ps.Size - _split.ClientSize;      // рамка, заголовок и вкладки
                int width = u * 52;
                int height = Math.Max(_split.PreferredHeightFor(width),
                                      _merge.PreferredHeightFor(width)) + frame.Height;

                Rectangle area = Screen.FromPoint(_ps.Location).WorkingArea;
                _ps.Size = new Size(Math.Min(width + frame.Width, area.Width),
                                    Math.Min(height, area.Height * 9 / 10));
                KeepOnScreen();
            }
            catch (System.Exception)
            {
                // Размер не дался — останется тот, что дал AutoCAD.
            }
        }

        /// <summary>
        /// Отметка «размер по умолчанию уже выставлен» хранится рядом с профилем AutoCAD:
        /// размер палитры AutoCAD помнит по каждой версии и каждому профилю отдельно, и в
        /// новой версии или новом профиле окно тоже должно открыться аккуратным.
        /// </summary>
        static string ProfileKeyPath()
        {
            string root = HostApplicationServices.Current.UserRegistryProductRootKey;
            string profile = Convert.ToString(AcAp.GetSystemVariable("CPROFILE"));
            return root + "\\Profiles\\" + profile + "\\DwGangsta";
        }

        /// <summary>
        /// Отметка широкого размера. Прежняя (PaletteSized) стоит у тех, кто открывал
        /// узкую палитру: по новой отметке палитра один раз откроется уже широкой.
        /// </summary>
        const string SizedValue = "PaletteSizedWide";

        static bool IsSizedInProfile()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(ProfileKeyPath()))
                    return key != null && Convert.ToInt32(key.GetValue(SizedValue, 0)) == 1;
            }
            catch (System.Exception)
            {
                return true;      // реестр недоступен — не трогаем размер
            }
        }

        static void MarkSizedInProfile()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.CreateSubKey(ProfileKeyPath()))
                    key.SetValue(SizedValue, 1, RegistryValueKind.DWord);
            }
            catch (System.Exception) { }
        }

        /// <summary>Плавающая палитра; прикреплённую к краю тянет AutoCAD.</summary>
        static bool IsFloating()
        {
            try
            {
                return _ps != null && _ps.Visible && _ps.Dock == DockSides.None && !_ps.RolledUp;
            }
            catch (System.Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// Положение палитры AutoCAD запоминает. Если она стояла на краю большого
        /// монитора, а теперь экран меньше (или монитор отключили), окно возвращается
        /// на видимую часть экрана целиком.
        /// </summary>
        static void KeepOnScreen()
        {
            if (!IsFloating()) return;
            try
            {
                var r = new Rectangle(_ps.Location, _ps.Size);
                Rectangle area = Screen.FromRectangle(r).WorkingArea;
                int x = Math.Max(area.Left, Math.Min(r.Left, area.Right - r.Width));
                int y = Math.Max(area.Top, Math.Min(r.Top, area.Bottom - r.Height));
                if (x != r.Left || y != r.Top)
                    _ps.Location = new Point(x, y);
            }
            catch (System.Exception)
            {
            }
        }
    }
}
