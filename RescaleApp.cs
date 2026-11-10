using System;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Autodesk.AutoCAD.Ribbon;
using Autodesk.AutoCAD.Runtime;
using Autodesk.Windows;

using AcadApp = Autodesk.AutoCAD.ApplicationServices.Application;
using CoreApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;

[assembly: ExtensionApplication(typeof(ZoomZoom.RescaleApp))]
#if GANGSTA
// Команды DwGangsta (разделение и сборка чертежей, перенос с видовыми экранами,
// подсветка экранов) — теперь часть Зум-зума. Классы с командами AutoCAD ищет
// только по этому списку.
[assembly: CommandClass(typeof(DwGangsta.Commands))]
[assembly: CommandClass(typeof(DwGangsta.MergeCommands))]
[assembly: CommandClass(typeof(DwGangsta.MoveCommands))]
#endif

namespace ZoomZoom
{
    /// <summary>
    /// Точка входа плагина: строит панель на ленте при запуске AutoCAD.
    /// </summary>
    public class RescaleApp : IExtensionApplication
    {
        /// <summary>Имя плагина в командной строке и отчётах (они всегда по-русски).</summary>
        internal const string PluginName = "Зум-зум";

        /// <summary>Имя плагина в интерфейсе: на ленте и в строке состояния.</summary>
        internal static string UiName => Lang.T("Зум-зум", "ZoomZoom");

        private const string PanelId = "ZoomZoom.Panel";
        private const string TabId = "ZoomZoom.Tab";

        public void Initialize()
        {
            // Язык — раньше всего остального: по нему строятся лента и палитры.
            Lang.English = DetectEnglish();

            // Ленты может не быть вовсе: AutoCAD Core Console (accoreconsole.exe)
            // не грузит ни интерфейс, ни AdWindows.dll. Обращение к ленте вынесено
            // в отдельный метод не просто так: .NET разрешает типы при компиляции
            // метода, и упоминание ComponentManager прямо здесь уронило бы
            // Initialize раньше, чем сработал бы try. А без ленты команды обязаны
            // работать — на этом держится проверка плагина без интерфейса.
            //
            // try спасает не всегда: в Core Console попытка подгрузить
            // AcWindows.dll (а за ней acmgd.dll, которой там нет) уходит
            // в бесконечный поиск сборки и роняет процесс переполнением стека,
            // которое не ловится. Поэтому без полного интерфейса ленту не трогаем.
            if (!HasFullUi())
                return;
            try { HookRibbon(); }
            catch (System.Exception) { }
        }

        /// <summary>
        /// Английский ли интерфейс. По порядку:
        ///   1) переменная окружения ZOOMZOOM_LANG (en или ru) — принудительно,
        ///      для проверки интерфейса;
        ///   2) язык, выбранный в установщике (InstallerLanguage);
        ///   3) язык самого AutoCAD — он записан в имени раздела реестра продукта:
        ///      …\R24.3\ACAD-7101:409, после двоеточия код языка Windows в
        ///      шестнадцатеричном виде (409 — английский, 419 — русский); если
        ///      разобрать не вышло — системная переменная LOCALE («EN», «RU»).
        ///
        /// Только acdbmgd и accoremgd: метод вызывается и в Core Console, где
        /// acmgd нет (см. Initialize).
        /// </summary>
        internal static bool DetectEnglish()
        {
            string forced = Environment.GetEnvironmentVariable("ZOOMZOOM_LANG");
            if (!string.IsNullOrEmpty(forced))
                return forced.Trim().StartsWith("en", StringComparison.OrdinalIgnoreCase);

            bool? chosen = InstallerLanguage();
            if (chosen.HasValue)
                return chosen.Value;

            try
            {
                string key = Autodesk.AutoCAD.DatabaseServices.HostApplicationServices.Current
                                 .UserRegistryProductRootKey ?? "";
                int colon = key.LastIndexOf(':');
                int lcid;
                if (colon >= 0 && int.TryParse(key.Substring(colon + 1), NumberStyles.HexNumber,
                                               CultureInfo.InvariantCulture, out lcid))
                    return (lcid & 0x3FF) == 0x09;      // основной язык 0x09 — английский
            }
            catch (System.Exception) { }

            try
            {
                string locale = Convert.ToString(CoreApp.GetSystemVariable("LOCALE"));
                return locale.StartsWith("EN", StringComparison.OrdinalIgnoreCase);
            }
            catch (System.Exception) { }
            return false;
        }

        /// <summary>
        /// Язык, выбранный в установщике. Установщик пишет его в ZoomZoom.ini в
        /// корне бандла, рядом с PackageContents.xml:
        ///   [ZoomZoom]
        ///   Language=english      (или russian)
        /// Сама сборка лежит в Contents\&lt;платформа&gt;\ZoomZoom.dll — корень на два
        /// уровня выше. null — файла нет: плагин поставлен не установщиком
        /// (deploy.ps1, NETLOAD), тогда язык — по AutoCAD.
        /// </summary>
        private static bool? InstallerLanguage()
        {
            try
            {
                string dir = Path.GetDirectoryName(typeof(RescaleApp).Assembly.Location);
                string root = Path.GetDirectoryName(Path.GetDirectoryName(dir));
                string ini = Path.Combine(root, "ZoomZoom.ini");
                if (!File.Exists(ini))
                    return null;

                foreach (string line in File.ReadAllLines(ini))
                {
                    int eq = line.IndexOf('=');
                    if (eq < 0 || !line.Substring(0, eq).Trim().Equals("Language", StringComparison.OrdinalIgnoreCase))
                        continue;
                    string value = line.Substring(eq + 1).Trim();
                    if (value.StartsWith("en", StringComparison.OrdinalIgnoreCase)) return true;
                    if (value.StartsWith("ru", StringComparison.OrdinalIgnoreCase)) return false;
                }
            }
            catch (System.Exception) { }
            return null;
        }

        /// <summary>acmgd.dll — .NET-часть интерфейса AutoCAD. В acad.exe она
        /// загружена раньше любого плагина, в accoreconsole.exe её нет.</summary>
        private static bool HasFullUi()
        {
            foreach (System.Reflection.Assembly a in AppDomain.CurrentDomain.GetAssemblies())
                if (string.Equals(a.GetName().Name, "acmgd", StringComparison.OrdinalIgnoreCase))
                    return true;
            return false;
        }

        [System.Runtime.CompilerServices.MethodImpl(
            System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static void HookRibbon()
        {
            // При старте AutoCAD лента может быть ещё не построена — тогда ждём
            // события о её появлении. Иначе панель просто некуда добавить.
            if (ComponentManager.Ribbon != null)
                BuildRibbon();
            else
                ComponentManager.ItemInitialized += OnItemInitialized;

            // Ленту, собранную из кода, AutoCAD не хранит в CUI: при смене
            // рабочего пространства он строит вкладки заново из CUI, и всё
            // добавленное программно пропадает. Autodesk для этого предлагает
            // слушать загрузку рабочего пространства и добавлять панель заново.
            // Лента ещё может закрыться командой RIBBONCLOSE и открыться снова
            // (RIBBON) — тогда создаётся новая палитра ленты, подписываемся и на неё.
            RibbonServices.RibbonPaletteSetCreated += OnRibbonPaletteSetCreated;
            if (RibbonServices.RibbonPaletteSet != null)
                RibbonServices.RibbonPaletteSet.WorkspaceLoaded += OnWorkspaceLoaded;

            // Запасной путь, если событие ленты не пришло (бывает в старых
            // релизах): WSCURRENT меняется при любом переключении пространства.
            AcadApp.SystemVariableChanged += OnSystemVariableChanged;
        }

        public void Terminate()
        {
        }

        private static void OnItemInitialized(object sender, RibbonItemEventArgs e)
        {
            if (ComponentManager.Ribbon == null)
                return;
            ComponentManager.ItemInitialized -= OnItemInitialized;
            BuildRibbon();
        }

        private static void OnRibbonPaletteSetCreated(object sender, EventArgs e)
        {
            try
            {
                if (RibbonServices.RibbonPaletteSet != null)
                {
                    RibbonServices.RibbonPaletteSet.WorkspaceLoaded -= OnWorkspaceLoaded;
                    RibbonServices.RibbonPaletteSet.WorkspaceLoaded += OnWorkspaceLoaded;
                }
            }
            catch (System.Exception) { }
            RebuildWhenIdle();
        }

        private static void OnWorkspaceLoaded(object sender, EventArgs e)
        {
            RebuildWhenIdle();
        }

        private static void OnSystemVariableChanged(object sender,
            Autodesk.AutoCAD.ApplicationServices.SystemVariableChangedEventArgs e)
        {
            if (string.Equals(e.Name, "WSCURRENT", StringComparison.OrdinalIgnoreCase))
                RebuildWhenIdle();
        }

        private static bool _rebuildQueued;

        /// <summary>
        /// Панель добавляется, когда AutoCAD закончит разбирать новое рабочее
        /// пространство: в момент события вкладки ещё перестраиваются, и
        /// добавленное туда тут же затёрлось бы.
        /// </summary>
        private static void RebuildWhenIdle()
        {
            if (_rebuildQueued)
                return;
            _rebuildQueued = true;
            AcadApp.Idle += OnIdleRebuild;
        }

        private static void OnIdleRebuild(object sender, EventArgs e)
        {
            AcadApp.Idle -= OnIdleRebuild;
            _rebuildQueued = false;
            BuildRibbon();
        }

        /// <summary>
        /// Ставит панель на место. Можно звать сколько угодно раз: если панель
        /// уже стоит где надо — ничего не делает, если стоит не там (вкладку
        /// «Надстройки» в этом пространстве скрыли) — переносит.
        /// </summary>
        private static void BuildRibbon()
        {
            try
            {
                RibbonControl ribbon = ComponentManager.Ribbon;
                if (ribbon == null)
                {
                    // Ленту закрыли (RIBBONCLOSE, «Классический» интерфейс) —
                    // ждём, когда её откроют снова.
                    ComponentManager.ItemInitialized -= OnItemInitialized;
                    ComponentManager.ItemInitialized += OnItemInitialized;
                    return;
                }

                RibbonTab own = null;
                foreach (RibbonTab t in ribbon.Tabs)
                    if (t.Id == TabId) { own = t; break; }

                RibbonTab tab = FindAddInsTab(ribbon) ?? own;
                if (tab == null)
                {
                    tab = new RibbonTab { Title = UiName, Id = TabId };
                    ribbon.Tabs.Add(tab);
                }

                // Панель — ровно одна и только на выбранной вкладке.
                bool present = false;
                foreach (RibbonTab t in ribbon.Tabs)
                {
                    for (int i = t.Panels.Count - 1; i >= 0; i--)
                    {
                        RibbonPanel p = t.Panels[i];
                        if (p.Source == null || p.Source.Id != PanelId)
                            continue;
                        if (t == tab && !present)
                            present = true;
                        else
                            t.Panels.RemoveAt(i);
                    }
                }
                if (own != null && own != tab)
                    ribbon.Tabs.Remove(own);
                if (present)
                    return;

                var source = new RibbonPanelSource { Title = UiName, Id = PanelId };

                // Кнопки — колонками по две, одни значки: сетка 4 x 2, как у
                // встроенных панелей. Название — в подсказке при наведении.
                source.Items.Add(Column(
                    Button(Lang.T("Весь чертёж", "Whole Drawing"), "ZOOMZOOMALL", IconKind.Whole,
                        Lang.T("Масштабирует всё текущее пространство по настройкам палитры.\n" +
                               "Берёт и то, что лежит на замороженных и выключенных слоях.\n" +
                               "Команда: ZZALL",
                               "Rescales the whole current space with the palette settings,\n" +
                               "including objects on frozen and turned-off layers.\n" +
                               "Command: ZZALL")),
                    Button(Lang.T("Рамка", "Window"), "ZOOMZOOMSEL", IconKind.Frame,
                        Lang.T("То же самое, но только для объектов, выбранных рамкой\n" +
                               "(и для скрытых слоёв в той же рамке).\n" +
                               "Команда: ZZSEL",
                               "The same, but only for objects selected with a window\n" +
                               "(and for hidden layers inside the same window).\n" +
                               "Command: ZZSEL"))));

                source.Items.Add(Column(
                    Button(Lang.T("Отрезок", "Reference"), "ZOOMZOOMREF", IconKind.Ref,
                        Lang.T("Масштаб по опорному отрезку: две точки отрезка, затем новое\n" +
                               "положение второй (первая остаётся на месте) или длина числом.\n" +
                               "Команда: ZZREF",
                               "Scale by a reference segment: pick its two points, then the new\n" +
                               "position of the second one (the first stays put) or type a length.\n" +
                               "Command: ZZREF")),
                    Button(Lang.T("Настройки", "Settings"), "ZOOMZOOM", IconKind.Settings,
                        Lang.T("Палитра: коэффициент масштабирования и расчленение прокси.\n" +
                               "Команда: ZZ",
                               "Palette: scale factor and proxy object explosion.\n" +
                               "Command: ZZ"))));
#if GANGSTA
                source.Items.Add(Column(
                    Button(Lang.T("Подсветить экраны", "Highlight Viewports"), "DWGVIEWS",
                        DwGangsta.RibbonIcons.CreateHighlight,
                        Lang.T("Обвести в модели всё, что показывают видовые экраны листов.\n" +
                               "Повторное нажатие стирает прежние рамки и обводит заново.\n" +
                               "Команда: DWGVIEWS",
                               "Outline in model space everything the layout viewports show.\n" +
                               "Clicking again erases the previous outlines and draws new ones.\n" +
                               "Command: DWGVIEWS")),
                    Button(Lang.T("Передвинуть с видовым", "Move with Viewport"), "DWGMOVEVP",
                        DwGangsta.RibbonIcons.CreateMoveViews,
                        Lang.T("Перенести объекты модели с сохранением отображения в видовых экранах,\n" +
                               "аналогично команде ПЕРЕНЕСТИ, и соответственно сдвинуть видовые\n" +
                               "экраны, в которых отображается перенесённое.\n" +
                               "Команда: DWGMOVEVP",
                               "Move model objects like the MOVE command and shift the viewports\n" +
                               "that show them by the same distance, so the layouts keep\n" +
                               "showing the same thing.\n" +
                               "Command: DWGMOVEVP"))));

                source.Items.Add(Column(
                    Button(Lang.T("Разделить по листам", "Split by Layouts"), "DWGSPLIT",
                        DwGangsta.RibbonIcons.Create,
                        Lang.T("Сохранить каждый лист чертежа в отдельный файл DWG\n" +
                               "Команда: DWGSPLIT",
                               "Save every layout of the drawing to a separate DWG file\n" +
                               "Command: DWGSPLIT")),
                    Button(Lang.T("Собрать в один", "Merge into One"), "DWGMERGE",
                        DwGangsta.RibbonIcons.CreateMerge,
                        Lang.T("Сборка нескольких многолистовых чертежей в один DWG\n" +
                               "Команда: DWGMERGE",
                               "Merge several multi-layout drawings into one DWG\n" +
                               "Command: DWGMERGE"))));
#endif
                tab.Panels.Add(new RibbonPanel { Source = source });
            }
            catch (System.Exception)
            {
                // Лента — украшение. Если её собрать не удалось, команды всё равно
                // работают из командной строки, и падать из-за этого незачем.
            }
        }

        /// <summary>
        /// Ищет встроенную вкладку «Надстройки». Идентификатор у неё в разных
        /// версиях и локализациях разный, поэтому проверяем и Id, и заголовок.
        /// </summary>
        private static RibbonTab FindAddInsTab(RibbonControl ribbon)
        {
            foreach (RibbonTab tab in ribbon.Tabs)
            {
                // Вкладка есть, но в этом рабочем пространстве скрыта — панель
                // на ней не увидеть, лучше своя вкладка.
                if (!tab.IsVisible)
                    continue;
                if (tab.Id == "ACAD.ID_TabAddIns")
                    return tab;

                string title = tab.Title ?? string.Empty;
                if (title.IndexOf("Надстройк", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    title.IndexOf("Add-in", StringComparison.OrdinalIgnoreCase) >= 0)
                    return tab;
            }
            return null;
        }

        private static RibbonButton Button(string text, string command, IconKind icon, string tooltip)
        {
            return Button(text, command, size => RibbonIcons.Icon(size, icon), tooltip);
        }

        /// <summary>
        /// Кнопка в колонке: крупный значок 32 без надписи (Large + Horizontal —
        /// две такие встают одна над другой; Standard с картинкой 32 лента
        /// обрезает). Без надписей панель занимает на ленте мало места, а
        /// название кнопки — жирный заголовок подсказки при наведении. Значок не
        /// построился — надпись возвращается, чтобы кнопку было видно.
        /// </summary>
        private static RibbonButton Button(string text, string command, Func<int, ImageSource> icon, string tooltip)
        {
            var b = new RibbonButton
            {
                Text = text,
                ShowText = false,
                ShowImage = true,
                Size = RibbonItemSize.Large,
                Orientation = System.Windows.Controls.Orientation.Horizontal,
                CommandHandler = new SendCommand(),
                CommandParameter = command,
                ToolTip = new RibbonToolTip
                {
                    Title = text,
                    Content = tooltip,
                    IsHelpEnabled = false
                }
            };
            try
            {
                b.Image = icon(16);
                b.LargeImage = icon(32);
            }
            catch (System.Exception)
            {
                b.ShowImage = false;
                b.ShowText = true;
            }
            return b;
        }

        /// <summary>Колонка из двух кнопок одна под другой.</summary>
        private static RibbonRowPanel Column(RibbonItem top, RibbonItem bottom)
        {
            var column = new RibbonRowPanel();
            column.Items.Add(top);
            column.Items.Add(new RibbonRowBreak());
            column.Items.Add(bottom);
            return column;
        }

        /// <summary>Кнопка ленты отправляет команду в командную строку документа.</summary>
        private class SendCommand : System.Windows.Input.ICommand
        {
            // Доступность кнопок не меняется, подписчики не нужны.
            public event EventHandler CanExecuteChanged { add { } remove { } }

            public bool CanExecute(object parameter)
            {
                return true;
            }

            public void Execute(object parameter)
            {
                var doc = AcadApp.DocumentManager.MdiActiveDocument;
                if (doc == null)
                    return;

                var button = parameter as RibbonButton;
                string command = button != null
                    ? button.CommandParameter as string
                    : parameter as string;

                if (string.IsNullOrEmpty(command))
                    return;

                doc.SendStringToExecute("_" + command + "\n", true, false, true);
            }
        }
    }
}
