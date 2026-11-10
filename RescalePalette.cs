using System;
using System.Drawing;
using System.Windows.Forms;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Windows;
using Microsoft.Win32;

using AcadApp = Autodesk.AutoCAD.ApplicationServices.Application;

namespace ZoomZoom
{
    // Настройки и их сохранение между запусками — в RescaleSettings.cs.

    /// <summary>Палитра AutoCAD с настройками масштабирования.</summary>
    public static class RescalePalette
    {
        private static PaletteSet _ps;
        private static RescaleControl _control;

        // Постоянный GUID: по нему AutoCAD запоминает положение и размер палитры
        // между сеансами. Менять нельзя — иначе палитра каждый раз вылезает заново.
        private static readonly Guid PaletteId =
            new Guid("8F3A1C2E-5B47-4D91-A6E0-2C7B9D4F1A63");

        /// <summary>Заголовок палитры — на её вертикальной полосе.</summary>
        private const string PaletteTitle = "ZoomZoom";

        public static void Show()
        {
            if (_ps == null)
            {
                _ps = new PaletteSet(PaletteTitle, PaletteId);
                _ps.Style = PaletteSetStyles.ShowPropertiesMenu
                          | PaletteSetStyles.ShowAutoHideButton
                          | PaletteSetStyles.ShowCloseButton;

                _control = new RescaleControl();
                _control.ApplyTheme(CurrentTheme());

                // Минимум — от шрифта, а не в пикселях: на мониторе с масштабом
                // 150% и 200% палитра не должна обрезать содержимое.
                int u = _control.Font.Height;
                _ps.MinimumSize = new Size(u * 16, u * 8);
                _ps.Add(Lang.T("Настройки", "Settings"), _control);

                // Двойной щелчок по пустому месту палитры — вернуть размер по
                // умолчанию, если окно растянули или сжали неудачно.
                _control.DoubleClick += (s, e) => ApplyDefaultSize();

                // Тема AutoCAD меняется на лету — палитра следом.
                AcadApp.SystemVariableChanged += (s, e) =>
                {
                    if (string.Equals(e.Name, "COLORTHEME", StringComparison.OrdinalIgnoreCase) && _control != null)
                        _control.ApplyTheme(CurrentTheme());
                };
            }
            _ps.Visible = true;

            // Заголовок AutoCAD хранит в профиле (Profile.aws) вместе с
            // положением палитры и при показе подставляет оттуда — у тех, кто
            // ставил плагин ещё под старым именем, там лежит «AcadRescale».
            // Поэтому имя задаём уже после показа, каждый раз.
            try
            {
                if (_ps.Name != PaletteTitle)
                    _ps.Name = PaletteTitle;
            }
            catch (System.Exception) { }

            // Первое открытие в этом профиле — плавающее окно размера по
            // умолчанию. Дальше размер и положение помнит AutoCAD, и
            // пользователь волен менять их как угодно.
            if (!IsSizedInProfile())
            {
                try { _ps.Dock = DockSides.None; }
                catch (System.Exception) { }
                ApplyDefaultSize();
                MarkSizedInProfile();
            }

            KeepOnScreen();
        }

        /// <summary>
        /// Размер по умолчанию: шириной в 20 строк шрифта, а высотой — по
        /// содержимому: переключатели и поле видны целиком. Всё считается от шрифта, поэтому
        /// на HD со 100% и на 4K с 200% окно выглядит одинаково.
        /// </summary>
        private static void ApplyDefaultSize()
        {
            if (!IsFloating()) return;
            try
            {
                int u = _control.Font.Height;
                Size frame = _ps.Size - _control.ClientSize;      // рамка и полоса заголовка
                int width = u * 20;
                int height = _control.PreferredSettingsHeight(width) + frame.Height;

                Rectangle area = Screen.FromPoint(_ps.Location).WorkingArea;
                _ps.Size = new Size(Math.Min(width + frame.Width, area.Width),
                                    Math.Min(height, area.Height));
                KeepOnScreen();
            }
            catch (System.Exception)
            {
                // Размер не дался — останется тот, что дал AutoCAD.
            }
        }

        /// <summary>
        /// Отметка «размер по умолчанию уже выставлен» хранится рядом с
        /// профилем AutoCAD, а не в settings.ini: размер палитры AutoCAD помнит
        /// по каждой версии и каждому профилю отдельно, и в новой версии или
        /// новом профиле окно тоже должно открыться аккуратным.
        /// </summary>
        private static string ProfileKeyPath()
        {
            string root = HostApplicationServices.Current.UserRegistryProductRootKey;
            string profile = Convert.ToString(AcadApp.GetSystemVariable("CPROFILE"));
            return root + "\\Profiles\\" + profile + "\\ZoomZoom";
        }

        private static bool IsSizedInProfile()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(ProfileKeyPath()))
                    return key != null && Convert.ToInt32(key.GetValue("PaletteSized", 0)) == 1;
            }
            catch (System.Exception)
            {
                return true;      // реестр недоступен — не трогаем размер
            }
        }

        private static void MarkSizedInProfile()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.CreateSubKey(ProfileKeyPath()))
                    key.SetValue("PaletteSized", 1, RegistryValueKind.DWord);
            }
            catch (System.Exception) { }
        }

        /// <summary>Плавающая палитра; прикреплённую к краю тянет AutoCAD.</summary>
        private static bool IsFloating()
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
        /// Положение палитры AutoCAD запоминает. Если она стояла на краю
        /// большого монитора, а теперь экран меньше (или монитор отключили),
        /// окно возвращается на видимую часть экрана целиком.
        /// </summary>
        private static void KeepOnScreen()
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

        /// <summary>COLORTHEME: 0 — тёмная тема интерфейса AutoCAD, 1 — светлая.</summary>
        private static Theme CurrentTheme()
        {
            try
            {
                return Convert.ToInt32(AcadApp.GetSystemVariable("COLORTHEME")) == 1 ? Theme.Light : Theme.Dark;
            }
            catch (System.Exception)
            {
                return Theme.Dark;
            }
        }
    }
}
