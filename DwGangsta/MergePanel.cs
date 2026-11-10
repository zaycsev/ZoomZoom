using System;
using System.Collections.Generic;
using System.IO;
using System.Windows.Forms;
using ZoomZoom;

namespace DwGangsta
{
    /// <summary>
    /// Вкладка палитры «Собрать в один». Список файлов живёт вместе с палитрой: собрали —
    /// можно поправить пару настроек и собрать снова, не набирая папки заново. AutoCAD
    /// вкладка не касается: по кнопке собирает настройки и отдаёт их в событии Run.
    /// </summary>
    public class MergePanel : ThemedPanel
    {
        readonly PathList _files;
        readonly FieldFrame _filesFrame;
        readonly ThemedLabel _status;
        readonly ThemedLabel _filesHeading;
        readonly FlatButton _addFiles, _addFolder, _remove, _left, _right;
        readonly ThemedLabel _targetHeading;
        readonly FieldFrame _targetFrame;
        readonly FlatButton _browse;
        readonly ThemedLabel _modelHeading, _gapLabel;
        readonly FlatButton _run;

        /// <summary>Папка прошлого добавления: следующий обзор открывается там же.</summary>
        string _lastFolder = "";

        /// <summary>Добавленные папки по порядку — по ним раскрашиваются подписи в модели.</summary>
        readonly List<string> _roots = new List<string>();

        /// <summary>
        /// Порядок переставлен руками. До этого список сам держит порядок, в котором
        /// сборка создаст листы; после — новые файлы просто дописываются в конец.
        /// </summary>
        bool _manualOrder;
        readonly TextBox _target;
        readonly RadioOption _asIs;
        readonly RadioOption _noOverlap;
        readonly RadioOption _byName;
        readonly ToggleSwitch _outlines;
        readonly ToggleSwitch _trim;
        readonly ToggleSwitch _frames;
        readonly ToggleSwitch _labels;
        readonly ToggleSwitch _series;
        readonly NumberField _gap;
        readonly ToggleSwitch _openResult;

        /// <summary>Нажата «Собрать»: настройки собраны и проверены.</summary>
        public event EventHandler<MergeOptions> Run;

        public MergePanel() : this(new MergeOptions()) { }

        public MergePanel(MergeOptions options)
        {
            _filesHeading = MakeLabel(Lang.T("Чертежи для сборки", "Drawings to merge"), LabelRole.Heading);

            _files = new PathList { SelectionMode = SelectionMode.MultiExtended };
            _filesFrame = new FieldFrame(_files, false);

            _addFiles = MakeButton(Lang.T("Добавить файлы...", "Add Files..."));
            _addFiles.Click += OnAddFiles;
            Tip(_addFiles, Lang.T("Добавить в сборку один или несколько чертежей DWG.\n" +
                                  "Путь из проводника можно вставить прямо в окне выбора.",
                                  "Add one or more DWG drawings to the merge.\n" +
                                  "You can paste a path from Explorer right into the file dialog."));
            _addFolder = MakeButton(Lang.T("Добавить директории...", "Add Folders..."));
            _addFolder.Click += OnAddFolder;
            Tip(_addFolder, Lang.T("Добавить все чертежи DWG из выбранной папки\n" +
                                   "вместе со всеми вложенными подпапками.",
                                   "Add all DWG drawings from the selected folder\n" +
                                   "together with all nested subfolders."));
            _remove = MakeButton(Lang.T("Убрать", "Remove"));
            _remove.Click += OnRemove;
            Tip(_remove, Lang.T("Убрать выделенные чертежи из списка (то же — клавиша Delete).",
                                "Remove the selected drawings from the list (or press Delete)."));
            // Delete в списке — то же, что «Убрать».
            _files.KeyDown += (s, e) => { if (e.KeyCode == Keys.Delete) OnRemove(s, e); };

            // Листы сборки идут в порядке списка: «Влево» — вкладка встанет раньше,
            // «Вправо» — позже, как в диспетчере листов AutoCAD.
            _left = MakeButton(Lang.T("Влево", "Left"));
            _left.Click += (s, e) => MoveSelected(-1);
            Tip(_left, Lang.T("Поднять выделенные чертежи выше по списку:\n" +
                              "их листы встанут в сборке раньше.\n" +
                              "Порядок в списке — порядок листов в сборке.",
                              "Move the selected drawings up the list:\n" +
                              "their layouts will come earlier in the merged drawing.\n" +
                              "The list order is the layout order of the merged drawing."));
            _right = MakeButton(Lang.T("Вправо", "Right"));
            _right.Click += (s, e) => MoveSelected(1);
            Tip(_right, Lang.T("Опустить выделенные чертежи ниже по списку:\n" +
                               "их листы встанут в сборке позже.\n" +
                               "Порядок в списке — порядок листов в сборке.",
                               "Move the selected drawings down the list:\n" +
                               "their layouts will come later in the merged drawing.\n" +
                               "The list order is the layout order of the merged drawing."));

            _status = MakeLabel("", LabelRole.Hint);

            _targetHeading = MakeLabel(Lang.T("Сохранить сборку как", "Save merged drawing as"), LabelRole.Heading);
            _target = new TextBox { Text = options.TargetPath };
            _targetFrame = new FieldFrame(_target, true);
            _browse = MakeButton(Lang.T("Обзор...", "Browse..."));
            _browse.Click += OnBrowse;
            _openResult = MakeToggle(Lang.T("Открыть сборку после завершения", "Open the merged drawing when done"),
                                     options.OpenResult);

            _modelHeading = MakeLabel(Lang.T("Модель", "Model"), LabelRole.Heading);
            _asIs = MakeRadio(Lang.T("Импорт в исходных координатах — как есть, не глядя на перекрытия",
                                     "Original coordinates — as is, overlaps are ignored"),
                              options.Placement == MergePlacement.AsIs);
            _noOverlap = MakeRadio(Lang.T("В исходных координатах с учётом перекрытия — наложившееся отодвигается",
                                          "Original coordinates without overlaps — overlapping drawings move aside"),
                                   options.Placement == MergePlacement.NoOverlap);
            _byName = MakeRadio(Lang.T("Сортировка по наименованию — папки и подпапки по колонкам, их содержимое в ряды",
                                       "Sort by name — folders and subfolders in columns, their contents in rows"),
                                options.Placement == MergePlacement.ByName);
            _outlines = MakeToggle(Lang.T("Обводить в модели то, что показывают видовые экраны листов",
                                          "Outline in model space what the layout viewports show"),
                                   options.DrawViewOutlines);
            _trim = MakeToggle(Lang.T("Переносить только находящееся в видовых экранах листов",
                                      "Bring only what is inside layout viewports"),
                               options.TrimModel);
            _frames = MakeToggle(Lang.T("Обводить участок каждого чертежа прямоугольником",
                                        "Draw a rectangle around the area of each drawing"), options.DrawFrames);
            _labels = MakeToggle(Lang.T("Подписывать участок именем исходного файла",
                                        "Label each area with its source file name"), options.DrawLabels);
            _series = MakeToggle(Lang.T("Ставить рядом чертежи с похожими названиями",
                                        "Place drawings with similar names next to each other"),
                                 options.GroupSimilarNames);
            _gapLabel = MakeLabel(Lang.T("Промежуток между участками, % от размера", "Gap between areas, % of their size"));
            _gap = new NumberField
            {
                Minimum = 0,
                Maximum = 200,
                Value = (decimal)Math.Round(options.GapFactor * 100.0)
            };

            _run = MakeButton(Lang.T("Собрать", "Merge"));
            _run.Click += OnRun;

            Controls.AddRange(new Control[]
            {
                _filesHeading, _filesFrame, _addFiles, _addFolder, _remove, _left, _right, _status,
                _targetHeading, _targetFrame, _browse, _openResult,
                _modelHeading, _asIs, _noOverlap, _byName, _outlines, _trim, _frames, _labels, _series,
                _gapLabel, _gap, _run
            });

            ApplyTheme(Theme.Dark);

            foreach (string file in options.Files) _files.Items.Add(file);
            SortPlanned();

            // Раскладка и серии меняют порядок, в котором сборка создаёт листы, — список следом.
            foreach (RadioOption r in new[] { _asIs, _noOverlap, _byName })
                r.CheckedChanged += (s, e) => { if (((RadioOption)s).Checked) SortPlanned(); };
            _series.CheckedChanged += (s, e) => SortPlanned();
            ShowTotal("");
            Ready();
        }

        /// <summary>
        /// В широкой палитре слева чертежи для сборки и под ними «Сохранить сборку как»,
        /// справа во всю их высоту — настройки модели; высоты колонок выравнивает список
        /// чертежей. В узкой — те же блоки одной стопкой.
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
                int w = ColumnWidth(outer);
                int xr = pad + outer - w;

                // Левая колонка — два блока; список в верхнем подстраивается так, чтобы
                // колонка сравнялась с правой, но не короче шести строк. Если левая
                // всё равно выше — удлиняется рамка правой. Окно выше содержимого —
                // список забирает и эту высоту, а рамка правой тянется следом.
                int files = Measure(Files, w), target = Measure(Target, w);
                int left = files + gap + target;
                int right = Measure(Model, w);
                int extra = Math.Max(ListMin - ListHeight, right - left) + Math.Max(0, Stretch);
                int h = Math.Max(left + extra, right);

                files = Files(pad, y, w, extra);
                Target(pad, y + files + gap, w, 0);
                Model(xr, y, w, h - right);
                y += h + gap;
            }
            else
            {
                y += Files(pad, y, outer, Stretch) + gap;
                y += Target(pad, y, outer, 0) + gap;
                y += Model(pad, y, outer, 0) + gap;
            }

            MainButton(_run, pad, ref y, outer, pad);
            return y;
        }

        int GroupPad => (int)(U * 0.6);       // отступ внутри рамки блока

        /// <summary>Высота списка чертежей, пока её не подстроили под соседа.</summary>
        int ListHeight => U * 12;

        /// <summary>Короче шести строк список не становится — дальше прокрутка палитры.</summary>
        int ListMin => _files.ItemHeight * 6 + 2;

        /// <summary>Исходные чертежи. Добавку высоты (или убавку) получает список.</summary>
        int Files(int x0, int y0, int outer, int extra)
        {
            int u = U, gap = u / 2, gp = GroupPad, inner = outer - gp * 2, x = x0 + gp;
            int y = y0;
            int top = BeginGroup(ref y, gp);
            Line(_filesHeading, x, ref y, inner, gap / 2);
            int listH = Math.Max(ListMin, ListHeight + extra);
            Place(_filesFrame, x, y, inner, listH);
            y += listH + gap;
            // Добавить — одним рядом, правка списка — другим.
            Buttons(new[] { _addFiles, _addFolder }, x, ref y, inner, gap / 2);
            Buttons(new[] { _remove, _left, _right }, x, ref y, inner, gap);
            Stack(_status, x, ref y, inner, 0);
            EndGroup(x0, outer, top, ref y, gp, 0);
            return y - y0;
        }

        /// <summary>Куда сохранить сборку.</summary>
        int Target(int x0, int y0, int outer, int extra)
        {
            int gap = U / 2, gp = GroupPad, inner = outer - gp * 2, x = x0 + gp;
            int y = y0;
            int top = BeginGroup(ref y, gp);
            Line(_targetHeading, x, ref y, inner, gap / 2);
            FieldWithButton(_targetFrame, _browse, x, ref y, inner, gap);
            Stack(_openResult, x, ref y, inner, 0);
            y += extra;
            EndGroup(x0, outer, top, ref y, gp, 0);
            return y - y0;
        }

        /// <summary>Раскладка модели сборки.</summary>
        int Model(int x0, int y0, int outer, int extra)
        {
            int u = U, gap = u / 2, gp = GroupPad, inner = outer - gp * 2, x = x0 + gp;
            int y = y0;
            int top = BeginGroup(ref y, gp);
            Line(_modelHeading, x, ref y, inner, gap);
            Stack(_asIs, x, ref y, inner, gap / 2);
            Stack(_noOverlap, x, ref y, inner, gap / 2);
            Stack(_byName, x, ref y, inner, u);
            Stack(_outlines, x, ref y, inner, gap / 2);
            Stack(_trim, x, ref y, inner, gap / 2);
            Stack(_frames, x, ref y, inner, gap / 2);
            Stack(_labels, x, ref y, inner, gap / 2);
            Stack(_series, x, ref y, inner, gap / 2);
            NumberRow(_gapLabel, _gap, x, ref y, inner, 0);
            y += extra;
            EndGroup(x0, outer, top, ref y, gp, 0);
            return y - y0;
        }

        MergePlacement Placement()
        {
            return _asIs.Checked ? MergePlacement.AsIs
                 : _noOverlap.Checked ? MergePlacement.NoOverlap
                 : MergePlacement.ByName;
        }

        /// <summary>
        /// Ставит список в тот порядок, в каком сборка сама создаст листы: серия за серией,
        /// внутри — по осям. Порядок, расставленный руками, не трогается. Выделение
        /// остаётся на тех же файлах.
        /// </summary>
        void SortPlanned()
        {
            if (_manualOrder || _files.Items.Count < 2) return;

            var files = new List<string>();
            var selected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (object item in _files.Items) files.Add((string)item);
            foreach (object item in _files.SelectedItems) selected.Add((string)item);

            List<string> order;
            try { order = DrawingMerger.PlannedOrder(files, _roots, _series.Checked, Placement()); }
            catch (Exception ex)
            {
                Trace.Write("порядок сборки не посчитан: " + ex.Message);
                return;
            }
            if (order.Count != files.Count) return;      // что-то потерялось — оставляем как есть

            _files.BeginUpdate();
            _files.Items.Clear();
            foreach (string file in order) _files.Items.Add(file);
            for (int i = 0; i < order.Count; i++)
                if (selected.Contains(order[i])) _files.SetSelected(i, true);
            _files.EndUpdate();
        }

        /// <summary>
        /// Сдвигает выделенные файлы на одно место влево (раньше) или вправо (позже).
        /// Можно выделить несколько — едут вместе, сохраняя порядок между собой.
        /// </summary>
        void MoveSelected(int delta)
        {
            var selected = new List<int>();
            foreach (int index in _files.SelectedIndices) selected.Add(index);
            if (selected.Count == 0) return;
            selected.Sort();
            if (delta < 0 && selected[0] == 0) return;
            if (delta > 0 && selected[selected.Count - 1] == _files.Items.Count - 1) return;

            _manualOrder = true;
            if (delta > 0) selected.Reverse();

            _files.BeginUpdate();
            foreach (int index in selected)
            {
                object item = _files.Items[index];
                _files.Items.RemoveAt(index);
                _files.Items.Insert(index + delta, item);
            }
            _files.ClearSelected();
            foreach (int index in selected) _files.SetSelected(index + delta, true);
            _files.EndUpdate();
        }

        void OnAddFiles(object sender, EventArgs e)
        {
            using (var dialog = new OpenFileDialog())
            {
                dialog.Title = Lang.T("Выберите чертежи", "Select drawings");
                dialog.Filter = Lang.T("Чертежи AutoCAD (*.dwg)|*.dwg", "AutoCAD drawings (*.dwg)|*.dwg");
                dialog.Multiselect = true;
                if (dialog.ShowDialog(this) != DialogResult.OK) return;

                Array.Sort(dialog.FileNames, StringComparer.CurrentCulture);

                int added, duplicates;
                Append(dialog.FileNames, out added, out duplicates);
                SuggestTarget();

                ShowTotal(Lang.T("Добавлено файлов: ", "Files added: ") + added + Skipped(duplicates) + ".");
            }
        }

        void OnAddFolder(object sender, EventArgs e)
        {
            // Диалог проводника, а не старое дерево папок: в нём есть адресная строка
            // и поле «Папка», куда путь можно вставить, не прокликивая подпапки.
            // Открываемся там, где выбирали в прошлый раз: соседнюю папку ищут рядом.
            string folder = FolderPicker.Show(this, _lastFolder,
                Lang.T("Папка с чертежами — вложенные подпапки будут просмотрены автоматически",
                       "Folder with drawings — nested subfolders are searched automatically"));
            if (folder == null) return;

            AddFolder(folder);
        }

        /// <summary>
        /// Забирает из папки все чертежи, включая вложенные подпапки.
        /// </summary>
        void AddFolder(string folder)
        {
            _lastFolder = folder;
            if (!_roots.Contains(folder)) _roots.Add(folder);

            List<string> found;
            int inaccessible;
            Cursor.Current = Cursors.WaitCursor;
            try
            {
                found = DwgFileFinder.FindDwgFiles(folder, out inaccessible);
            }
            finally
            {
                Cursor.Current = Cursors.Default;
            }

            int added, duplicates;
            Append(found, out added, out duplicates);
            SuggestTarget();

            ShowTotal(Lang.T("Из папки «" + FolderName(folder) + "» добавлено: ",
                             "Added from \"" + FolderName(folder) + "\": ") +
                      added + Skipped(duplicates) + ".");

            if (found.Count == 0)
            {
                Say(Lang.T("В этой папке и её подпапках DWG-файлов не нашлось.",
                           "No DWG files were found in this folder or its subfolders."));
            }
            else if (inaccessible > 0)
            {
                Say(Lang.T("Найдено файлов: ", "Files found: ") + found.Count +
                    Lang.T(" (добавлено новых: ", " (new: ") + added + ")." + Environment.NewLine +
                    Lang.T("Подпапок без доступа пропущено: ", "Inaccessible subfolders skipped: ") +
                    inaccessible + ".");
            }
        }

        /// <summary>
        /// Дописывает файлы в конец списка, не трогая уже набранные: папки добавляются
        /// одна за другой и собираются вместе. Новые файлы выделяются и подкручиваются
        /// в видимую часть — сразу видно, что именно приехало.
        /// </summary>
        void Append(IList<string> paths, out int added, out int duplicates)
        {
            int before = _files.Items.Count;

            _files.BeginUpdate();
            foreach (string path in paths) AddFile(path);
            _files.EndUpdate();

            added = _files.Items.Count - before;
            duplicates = paths.Count - added;

            if (added == 0) return;

            _files.ClearSelected();
            for (int i = before; i < _files.Items.Count; i++) _files.SetSelected(i, true);
            // Новые встают туда, где сборка создаст их листы, — и список подкручивается к ним.
            SortPlanned();
            if (_files.SelectedIndices.Count > 0) _files.TopIndex = _files.SelectedIndices[0];
        }

        void AddFile(string path)
        {
            foreach (object item in _files.Items)
            {
                if (string.Equals((string)item, path, StringComparison.OrdinalIgnoreCase)) return;
            }
            _files.Items.Add(path);
        }

        /// <summary>Строка под списком: что добавилось только что и сколько набрано всего.</summary>
        void ShowTotal(string what)
        {
            string total = _files.Items.Count == 0
                ? Lang.T("Список пуст.", "The list is empty.")
                : Lang.T("Всего в списке: ", "Total in the list: ") + _files.Items.Count + ".";

            _status.Text = what.Length > 0 ? what + " " + total : total;
            PerformLayout();
        }

        static string Skipped(int duplicates)
        {
            return duplicates > 0 ? Lang.T(" (уже были в списке: ", " (already in the list: ") + duplicates + ")" : "";
        }

        /// <summary>Имя папки без пути — в строку состояния влезает только оно.</summary>
        static string FolderName(string path)
        {
            try
            {
                string name = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar,
                                                            Path.AltDirectorySeparatorChar));
                return name.Length > 0 ? name : path;   // корень диска имени не имеет
            }
            catch (System.Exception) { return path; }
        }

        /// <summary>Подставляет путь результата рядом с исходниками, если он ещё не задан.</summary>
        void SuggestTarget()
        {
            if (_target.Text.Length > 0 || _files.Items.Count == 0) return;

            string first = (string)_files.Items[0];
            string dir = Path.GetDirectoryName(first);
            _target.Text = Path.Combine(dir, Lang.T("Сборка.dwg", "Merged.dwg"));
        }

        void OnRemove(object sender, EventArgs e)
        {
            var selected = new List<int>();
            foreach (int index in _files.SelectedIndices) selected.Add(index);
            selected.Sort();
            for (int i = selected.Count - 1; i >= 0; i--) _files.Items.RemoveAt(selected[i]);

            ShowTotal(selected.Count > 0 ? Lang.T("Убрано файлов: ", "Files removed: ") + selected.Count + "." : "");
        }

        void OnBrowse(object sender, EventArgs e)
        {
            using (var dialog = new SaveFileDialog())
            {
                dialog.Title = Lang.T("Куда сохранить сборку", "Save merged drawing as");
                dialog.Filter = Lang.T("Чертёж AutoCAD (*.dwg)|*.dwg", "AutoCAD drawing (*.dwg)|*.dwg");
                dialog.DefaultExt = "dwg";
                dialog.AddExtension = true;
                if (_target.Text.Length > 0) dialog.FileName = _target.Text;
                if (dialog.ShowDialog(this) == DialogResult.OK) _target.Text = dialog.FileName;
            }
        }

        void OnRun(object sender, EventArgs e)
        {
            if (_files.Items.Count == 0)
            {
                Say(Lang.T("Добавьте хотя бы один чертёж.", "Add at least one drawing."));
                return;
            }
            if (_target.Text.Trim().Length == 0)
            {
                Say(Lang.T("Укажите, куда сохранить сборку.", "Specify where to save the merged drawing."));
                return;
            }

            string target = _target.Text.Trim();
            foreach (object item in _files.Items)
            {
                if (string.Equals((string)item, target, StringComparison.OrdinalIgnoreCase))
                {
                    Say(Lang.T("Сборка не может совпадать с одним из исходных чертежей.",
                               "The merged drawing cannot be one of the source drawings."));
                    return;
                }
            }

            var options = new MergeOptions { Files = new List<string>() };
            foreach (object item in _files.Items) options.Files.Add((string)item);

            options.TargetPath = target;
            options.Roots = new List<string>(_roots);
            options.Placement = Placement();
            options.DrawViewOutlines = _outlines.Checked;
            options.TrimModel = _trim.Checked;
            options.DrawFrames = _frames.Checked;
            options.DrawLabels = _labels.Checked;
            options.GroupSimilarNames = _series.Checked;
            options.GapFactor = (double)_gap.Value / 100.0;
            options.OpenResult = _openResult.Checked;
            options.TabsInListOrder = true;      // листы — ровно в порядке списка
            Run?.Invoke(this, options);
        }
    }
}
