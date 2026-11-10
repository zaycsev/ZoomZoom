using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace DwGangsta
{
    /// <summary>Место одного чертежа в таблице раскладки.</summary>
    public class TableCell
    {
        public string File = "";

        /// <summary>Колонка: добавленная папка. Внутри неё чертежи одного этапа идут вправо.</summary>
        public int Column;

        /// <summary>Строка: этап работ. У всех папок этап с одним номером стоит на одной высоте.</summary>
        public int Row;
    }

    /// <summary>
    /// Раскладка «по осям и номерам»: каждая добавленная папка — своя колонка сверху вниз,
    /// соседняя папка встаёт рядом, и одинаковые по счёту этапы у всех папок оказываются
    /// на одной высоте: подушка к подушке, опалубка к опалубке. Если в одном этапе папки
    /// лежат два чертежа, второй занимает следующую колонку — этап растёт вправо.
    ///
    /// Порядок и папок, и этапов — как в проводнике, с числами как числами: «2» раньше «10».
    /// </summary>
    public static partial class TableLayout
    {
        /// <summary>Раскладывает файлы по клеткам таблицы. Порядок в списке — порядок сборки.</summary>
        public static List<TableCell> Arrange(IList<string> files, IList<string> roots)
        {
            return Arrange(files, roots, true);
        }

        /// <param name="groupSeries">
        /// Делить ли папку без подпапок на серии по именам файлов: опалубка к опалубке,
        /// бетонирование к бетонированию, каждая серия — своей строкой. Выключено —
        /// вся папка идёт одной строкой.
        /// </param>
        public static List<TableCell> Arrange(IList<string> files, IList<string> roots, bool groupSeries)
        {
            // Серии считаем по исходному порядку списка: в нём же сборка их и перечисляет,
            // и в этом же порядке они должны лечь строками сверху вниз.
            Dictionary<string, int> series = groupSeries ? SeriesIndex(files) : null;

            var byRoot = new List<KeyValuePair<string, List<string>>>();
            var index = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            foreach (string file in Sorted(files))
            {
                string root = RootOf(file, roots);
                int at;
                if (!index.TryGetValue(root, out at))
                {
                    at = byRoot.Count;
                    index[root] = at;
                    byRoot.Add(new KeyValuePair<string, List<string>>(root, new List<string>()));
                }
                byRoot[at].Value.Add(file);
            }

            // Папки — в том же порядке, что и файлы: «8-15» раньше «15-24».
            var cells = new List<TableCell>();
            int column = 0;

            foreach (KeyValuePair<string, List<string>> root in byRoot)
            {
                List<List<string>> stages = Stages(root.Value);

                // Вся папка одной строкой — внутри строки серии держатся вместе.
                // Разложена по подпапкам — строки и так по работам, серии только мешали бы
                // колонкам вставать по осям друг под другом.
                column += ByAxes(stages, stages.Count == 1 ? series : null, column, cells);
            }
            return cells;
        }

        /// <summary>
        /// Расставляет чертежи папки по колонкам. Колонка — участок поля по осям из имени
        /// файла: «А-Б1-5» одного этапа встаёт над «А-Б1-5» следующего, и по вертикали
        /// читается вся история одного места. Осей в именах нет — чертежи идут по порядку,
        /// как лежат в папке. Возвращает, сколько колонок заняла папка.
        /// </summary>
        static int ByAxes(List<List<string>> stages, Dictionary<string, int> series,
                          int column, List<TableCell> cells)
        {
            var keys = new Dictionary<string, AxisKey>(StringComparer.Ordinal);
            var ranks = new Dictionary<string, int>(StringComparer.Ordinal);
            var perStage = new List<Dictionary<string, List<string>>>();

            foreach (List<string> stage in stages)
            {
                var byKey = new Dictionary<string, List<string>>(StringComparer.Ordinal);
                foreach (string file in stage)
                {
                    AxisKey key = AxisKey.Parse(NameOf(file));
                    string id = key.Known ? key.Id : "";

                    // Серия идёт впереди осей: чертежи одной работы встают в ряду подряд,
                    // а уже внутри работы — по осям.
                    int rank = 0;
                    if (series != null)
                    {
                        if (!series.TryGetValue(file, out rank)) rank = int.MaxValue;
                        id = rank.ToString("D9", CultureInfo.InvariantCulture) + "|" + id;
                    }

                    if (!keys.ContainsKey(id)) { keys[id] = key; ranks[id] = rank; }

                    List<string> same;
                    if (!byKey.TryGetValue(id, out same)) byKey[id] = same = new List<string>();
                    same.Add(file);
                }
                perStage.Add(byKey);
            }

            // Ни у кого осей не нашлось — раскладываем как лежало.
            if (keys.Count == 1 && keys.ContainsKey(""))
            {
                int widest = 1;
                for (int row = 0; row < stages.Count; row++)
                {
                    if (stages[row].Count > widest) widest = stages[row].Count;
                    for (int i = 0; i < stages[row].Count; i++)
                        cells.Add(new TableCell { File = stages[row][i], Column = column + i, Row = row });
                }
                return widest;
            }

            var order = new List<string>(keys.Keys);
            order.Sort(delegate(string a, string b)
            {
                if (series != null && ranks[a] != ranks[b]) return ranks[a].CompareTo(ranks[b]);
                return keys[a].CompareTo(keys[b]);
            });

            // Один участок может встретиться в этапе дважды — под него берётся столько
            // колонок, сколько нужно самому «густому» этапу.
            var start = new Dictionary<string, int>(StringComparer.Ordinal);
            int width = 0;
            foreach (string id in order)
            {
                int most = 1;
                foreach (Dictionary<string, List<string>> stage in perStage)
                {
                    List<string> same;
                    if (stage.TryGetValue(id, out same) && same.Count > most) most = same.Count;
                }
                start[id] = width;
                width += most;
            }

            for (int row = 0; row < perStage.Count; row++)
            {
                foreach (string id in order)
                {
                    List<string> same;
                    if (!perStage[row].TryGetValue(id, out same)) continue;

                    same.Sort(NameGrouper.ComparePaths);
                    for (int i = 0; i < same.Count; i++)
                        cells.Add(new TableCell { File = same[i], Column = column + start[id] + i, Row = row });
                }
            }
            return width;
        }

        static string NameOf(string path)
        {
            try { return Path.GetFileNameWithoutExtension(path) ?? path; }
            catch (System.Exception) { return path; }
        }

        /// <summary>У каждого файла — номер его серии по именам, в порядке перечисления.</summary>
        static Dictionary<string, int> SeriesIndex(IList<string> files)
        {
            var index = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            List<NameGroup> groups = NameGrouper.Group(files);

            for (int i = 0; i < groups.Count; i++)
            {
                foreach (string file in groups[i].Files) index[file] = i;
            }
            return index;
        }

        /// <summary>
        /// Этапы папки — её подпапки по порядку: каждая даёт строку. Подпапок нет
        /// (все файлы лежат в самой папке) — вся папка и есть одна строка: чертежи идут
        /// в ряд слева направо.
        ///
        /// Правило одно и то же на любой глубине: папка — строка, её чертежи — в ряд.
        /// Квадратной сеткой папку не раскладываем: ряд заворачивало бы вниз, и часть
        /// чертежей оказывалась бы под остальными ни по какому признаку, просто по счёту.
        /// </summary>
        static List<List<string>> Stages(List<string> files)
        {
            var stages = new List<List<string>>();
            var index = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            foreach (string file in files)
            {
                string folder = FolderOf(file);
                int at;
                if (!index.TryGetValue(folder, out at))
                {
                    at = stages.Count;
                    index[folder] = at;
                    stages.Add(new List<string>());
                }
                stages[at].Add(file);
            }

            return stages;
        }

        /// <summary>Папка, которую добавил пользователь. Не нашлась — папка самого файла.</summary>
        static string RootOf(string file, IList<string> roots)
        {
            string found = null;
            if (roots != null)
            {
                foreach (string root in roots)
                {
                    if (string.IsNullOrEmpty(root)) continue;
                    if (!file.StartsWith(root, StringComparison.OrdinalIgnoreCase)) continue;

                    // Папку могли добавить вместе с вложенной — считаем по ближайшей.
                    if (found == null || root.Length > found.Length) found = root;
                }
            }
            return found ?? FolderOf(file);
        }

        static string FolderOf(string path)
        {
            try { return Path.GetDirectoryName(path) ?? ""; }
            catch (System.Exception) { return ""; }
        }

        static List<string> Sorted(IList<string> files)
        {
            var sorted = new List<string>(files);
            sorted.Sort(NameGrouper.ComparePaths);
            return sorted;
        }

    }
}
