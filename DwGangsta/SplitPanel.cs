using System;
using System.Collections.Generic;
using System.IO;
using System.Windows.Forms;
using ZoomZoom;

namespace DwGangsta
{
    /// <summary>
    /// Вкладка палитры «Разделить по листам». Палитра немодальная и живёт сколько угодно,
    /// поэтому список листов берётся у активного чертежа и обновляется вслед за ним —
    /// это делает хозяин палитры через LoadDrawing. Сама вкладка AutoCAD не касается:
    /// по кнопке она лишь собирает настройки и отдаёт их в событии Run.
    /// </summary>
    public class SplitPanel : ThemedPanel
    {
        readonly ThemedLabel _header;
        readonly ThemedLabel _listLabel;
        readonly CheckList _list;
        readonly FieldFrame _listFrame;
        readonly FlatButton _selectAll, _selectNone;
        readonly ThemedLabel _folderHeading;
        readonly TextBox _folder;
        readonly FieldFrame _folderFrame;
        readonly FlatButton _browse;
        readonly ToggleSwitch _subFolder;
        readonly ThemedLabel _trimHeading;
        readonly ToggleSwitch _outlines;
        readonly ToggleSwitch _trimModel;
        readonly NumberField _margin;
        readonly ThemedLabel _marginLabel;
        readonly ToggleSwitch _includeOff;
        readonly ToggleSwitch _keepAec;
        readonly ThemedLabel _fileHeading;
        readonly ToggleSwitch _purge;
        readonly ToggleSwitch _overwrite;
        readonly FlatButton _run;

        string _path;                 // чертёж, чьи листы сейчас в списке
        string _suggested = "";       // папка, предложенная по чертежу: её можно заменить на новую

        /// <summary>Нажата «Разделить»: настройки собраны и проверены.</summary>
        public event EventHandler<SplitOptions> Run;

        public SplitPanel()
        {
            var options = new SplitOptions();

            _header = MakeLabel(Lang.T("Нет открытого чертежа", "No drawing open"));
            _listLabel = MakeLabel(Lang.T("Листы для выгрузки", "Layouts to export"), LabelRole.Heading);
            _list = new CheckList();
            _listFrame = new FieldFrame(_list, false);

            _selectAll = MakeButton(Lang.T("Выделить все", "Select All"));
            _selectAll.Click += (s, e) => SetAllChecked(true);
            _selectNone = MakeButton(Lang.T("Снять все", "Clear All"));
            _selectNone.Click += (s, e) => SetAllChecked(false);

            // Путь можно вписать или вставить из адресной строки проводника,
            // а можно выбрать кнопкой «Обзор...».
            _folderHeading = MakeLabel(Lang.T("Куда сохранить файлы листов", "Save layout files to"), LabelRole.Heading);
            _folder = new TextBox();
            _folderFrame = new FieldFrame(_folder, true);
            _browse = MakeButton(Lang.T("Обзор...", "Browse..."));
            _browse.Click += OnBrowse;
            _subFolder = MakeToggle(Lang.T("Складывать в подпапку «ИмяЧертежа_Листы»",
                                         "Put the files into the subfolder \"DrawingName_Layouts\""),
                                  options.UseSubFolder);

            _trimHeading = MakeLabel(Lang.T("Пространство модели", "Model space"), LabelRole.Heading);
            _outlines = MakeToggle(Lang.T("Обводить в модели то, что показывают видовые экраны листа",
                                          "Outline in model space what the layout viewports show"),
                                   options.DrawViewOutlines);
            _trimModel = MakeToggle(Lang.T("Оставлять только то, что видно в видовых экранах листа",
                                           "Keep only what is visible in the layout viewports"),
                                    options.TrimModel);
            _trimModel.CheckedChanged += (s, e) => UpdateEnabled();
            _margin = new NumberField
            {
                Minimum = 0,
                Maximum = 100,
                Value = (decimal)Math.Round(options.MarginFactor * 100.0)
            };
            _marginLabel = MakeLabel(Lang.T("Запас вокруг области видового экрана, %", "Margin around the viewport area, %"));
            _includeOff = MakeToggle(Lang.T("Учитывать выключенные видовые экраны", "Include viewports that are turned off"),
                                     options.IncludeOffViewports);
            _keepAec = MakeToggle(Lang.T("Не удалять объекты Civil 3D", "Do not delete Civil 3D objects"),
                                  options.KeepAecObjects);

            _fileHeading = MakeLabel(Lang.T("Файлы", "Files"), LabelRole.Heading);
            _purge = MakeToggle(Lang.T("Очищать неиспользуемые слои, блоки и стили", "Purge unused layers, blocks and styles"),
                                options.PurgeUnused);
            _overwrite = MakeToggle(Lang.T("Перезаписывать существующие файлы", "Overwrite existing files"), options.Overwrite);

            _run = MakeButton(Lang.T("Разделить", "Split"));
            _run.Click += OnRun;

            Controls.AddRange(new Control[]
            {
                _header, _listLabel, _listFrame, _selectAll, _selectNone,
                _folderHeading, _folderFrame, _browse, _subFolder,
                _trimHeading, _outlines, _trimModel, _marginLabel, _margin, _includeOff, _keepAec,
                _fileHeading, _purge, _overwrite, _run
            });

            ApplyTheme(Theme.Dark);
            UpdateEnabled();
            LoadDrawing(null, new List<string>());
            Ready();
        }

        /// <summary>
        /// Показывает листы чертежа. path — полный путь; null — чертежа нет, пусто —
        /// чертёж ещё не сохранён (разделять его нечем: файлы листов делаются из файла).
        /// Тот же чертёж — снятые флажки остаются снятыми. Папку, которую предложили по
        /// прошлому чертежу, заменяет папка нового; вписанную руками — не трогает.
        /// </summary>
        public void LoadDrawing(string path, List<string> layouts)
        {
            if (path != null && path == _path && SameLayouts(layouts)) return;

            var off = new HashSet<string>();
            if (path == _path)
            {
                var on = new HashSet<string>(_list.CheckedNames());
                foreach (object item in _list.Items)
                    if (!on.Contains(item.ToString())) off.Add(item.ToString());
            }
            _path = path;

            _list.BeginUpdate();
            _list.Items.Clear();
            foreach (string name in layouts) _list.Add(name, !off.Contains(name));
            _list.EndUpdate();

            bool saved = !string.IsNullOrEmpty(path);
            _header.Text = path == null ? Lang.T("Нет открытого чертежа.", "No drawing open.")
                         : !saved ? Lang.T("Чертёж ещё не сохранён — сохраните его, чтобы разделить по листам.",
                                           "The drawing is not saved yet — save it to split it by layouts.")
                         : Lang.T("Чертёж: ", "Drawing: ") + Path.GetFileName(path);
            _run.Enabled = saved && layouts.Count > 0;

            string folder = saved ? Path.GetDirectoryName(path) ?? "" : "";
            string typed = (_folder.Text ?? "").Trim();
            if (typed.Length == 0 || string.Equals(typed, _suggested, StringComparison.OrdinalIgnoreCase))
                _folder.Text = folder;
            _suggested = folder;

            PerformLayout();
            Invalidate(true);
        }

        bool SameLayouts(List<string> layouts)
        {
            if (layouts.Count != _list.Items.Count) return false;
            for (int i = 0; i < layouts.Count; i++)
                if (layouts[i] != _list.Items[i].ToString()) return false;
            return true;
        }

        /// <summary>
        /// В широкой палитре — два ряда по два блока: слева чертёж с листами и папка,
        /// справа пространство модели и файлы. В узкой — те же блоки одной стопкой.
        /// </summary>
        protected override int Arrange(int width)
        {
            int u = U;
            int pad = u / 2 + 2;             // поле от края палитры
            int gap = u / 2;                 // расстояние между блоками
            int outer = width - pad * 2;
            int y = pad;

            if (TwoColumns(outer))
            {
                y += Row(Sheets, ModelSpace, pad, y, outer, Stretch) + gap;
                y += Row(Folder, Files, pad, y, outer) + gap;
            }
            else
            {
                y += Sheets(pad, y, outer, Stretch) + gap;
                y += Folder(pad, y, outer, 0) + gap;
                y += ModelSpace(pad, y, outer, 0) + gap;
                y += Files(pad, y, outer, 0) + gap;
            }

            MainButton(_run, pad, ref y, outer, pad);
            return y;
        }

        int GroupPad => (int)(U * 0.6);       // отступ внутри рамки блока

        /// <summary>
        /// Чертёж и его листы. Добавку (или убавку) высоты получает список листов,
        /// но короче четырёх строк он не становится.
        /// </summary>
        int Sheets(int x0, int y0, int outer, int extra)
        {
            int gap = U / 2, gp = GroupPad, inner = outer - gp * 2, x = x0 + gp;
            int y = y0;
            int top = BeginGroup(ref y, gp);
            Stack(_header, x, ref y, inner, gap);
            Line(_listLabel, x, ref y, inner, gap / 2);
            int rows = Math.Max(4, Math.Min(12, _list.Items.Count + 1));
            int listH = Math.Max(4 * _list.ItemHeight, rows * _list.ItemHeight + extra) + 4;
            Place(_listFrame, x, y, inner, listH);
            y += listH + gap;
            Buttons(new[] { _selectAll, _selectNone }, x, ref y, inner, 0);
            EndGroup(x0, outer, top, ref y, gp, 0);
            return y - y0;
        }

        /// <summary>Куда сохранить файлы листов.</summary>
        int Folder(int x0, int y0, int outer, int extra)
        {
            int gap = U / 2, gp = GroupPad, inner = outer - gp * 2, x = x0 + gp;
            int y = y0;
            int top = BeginGroup(ref y, gp);
            Line(_folderHeading, x, ref y, inner, gap / 2);
            FieldWithButton(_folderFrame, _browse, x, ref y, inner, gap);
            Stack(_subFolder, x, ref y, inner, 0);
            y += extra;
            EndGroup(x0, outer, top, ref y, gp, 0);
            return y - y0;
        }

        /// <summary>Чистка пространства модели.</summary>
        int ModelSpace(int x0, int y0, int outer, int extra)
        {
            int gap = U / 2, gp = GroupPad, inner = outer - gp * 2, x = x0 + gp;
            int y = y0;
            int top = BeginGroup(ref y, gp);
            Line(_trimHeading, x, ref y, inner, gap);
            Stack(_outlines, x, ref y, inner, gap / 2);
            Stack(_trimModel, x, ref y, inner, gap / 2);

            // Вложенные настройки — с отступом под подпись переключателя выше.
            int ix = x + _trimModel.TextIndent;
            int iw = inner - _trimModel.TextIndent;
            NumberRow(_marginLabel, _margin, ix, ref y, iw, gap / 2);
            Stack(_includeOff, ix, ref y, iw, gap / 2);
            Stack(_keepAec, ix, ref y, iw, 0);
            y += extra;
            EndGroup(x0, outer, top, ref y, gp, 0);
            return y - y0;
        }

        /// <summary>Настройки файлов листов.</summary>
        int Files(int x0, int y0, int outer, int extra)
        {
            int gap = U / 2, gp = GroupPad, inner = outer - gp * 2, x = x0 + gp;
            int y = y0;
            int top = BeginGroup(ref y, gp);
            Line(_fileHeading, x, ref y, inner, gap);
            Stack(_purge, x, ref y, inner, gap / 2);
            Stack(_overwrite, x, ref y, inner, 0);
            y += extra;
            EndGroup(x0, outer, top, ref y, gp, 0);
            return y - y0;
        }

        /// <summary>
        /// Выбор папки в окне проводника: в нём есть адресная строка, куда путь
        /// можно вставить, не прокликивая подпапки.
        /// </summary>
        void OnBrowse(object sender, EventArgs e)
        {
            string start = Directory.Exists(_folder.Text.Trim().Trim('"')) ? _folder.Text.Trim().Trim('"') : "";
            string folder = FolderPicker.Show(this, start, Lang.T("Куда сохранить файлы листов", "Save layout files to"));
            if (folder != null) _folder.Text = folder;
        }

        /// <summary>
        /// Полный путь с диском. Папки может ещё не быть — её создаст разделение,
        /// но недопустимых символов и относительных путей быть не должно.
        /// </summary>
        static bool IsValidFolder(string folder)
        {
            try { return Path.IsPathRooted(folder) && Path.GetFullPath(folder).Length > 0; }
            catch (Exception) { return false; }
        }

        void SetAllChecked(bool value)
        {
            for (int i = 0; i < _list.Items.Count; i++) _list.SetItemChecked(i, value);
        }

        void UpdateEnabled()
        {
            bool trim = _trimModel.Checked;
            _margin.Enabled = trim;
            _marginLabel.Enabled = trim;
            _includeOff.Enabled = trim;
            _keepAec.Enabled = trim;
        }

        void OnRun(object sender, EventArgs e)
        {
            List<string> chosen = _list.CheckedNames();
            if (chosen.Count == 0)
            {
                Say(Lang.T("Выберите хотя бы один лист.", "Select at least one layout."));
                return;
            }

            // Путь приходит как вписали: иногда в кавычках («Копировать как путь»).
            string folder = (_folder.Text ?? "").Trim().Trim('"');
            if (folder.Length == 0 || !IsValidFolder(folder))
            {
                Say(folder.Length == 0
                    ? Lang.T("Укажите, куда сохранить файлы листов.", "Specify where to save the layout files.")
                    : Lang.T("Такой путь записать нельзя:", "This path cannot be used:") +
                      Environment.NewLine + folder);
                return;
            }

            var options = new SplitOptions
            {
                Layouts = chosen,
                OutputFolder = folder,
                DrawViewOutlines = _outlines.Checked,
                TrimModel = _trimModel.Checked,
                MarginFactor = (double)_margin.Value / 100.0,
                IncludeOffViewports = _includeOff.Checked,
                KeepAecObjects = _keepAec.Checked,
                PurgeUnused = _purge.Checked,
                UseSubFolder = _subFolder.Checked,
                Overwrite = _overwrite.Checked
            };
            Run?.Invoke(this, options);
        }
    }
}
