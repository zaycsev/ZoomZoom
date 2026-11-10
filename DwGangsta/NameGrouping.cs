using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace DwGangsta
{
    /// <summary>Серия чертежей — файлы, имена которых говорят об одной работе.</summary>
    public class NameGroup
    {
        /// <summary>Слово, по которому файлы попали в серию: «Опалубка», «Бетонирование».</summary>
        public string Title = "";

        public List<string> Files = new List<string>();
    }

    /// <summary>
    /// Раскладывает файлы по сериям, читая только их имена — без интернета, словарей
    /// и настроек. Имя режется на слова, слово приводится к основе (первые буквы, чтобы
    /// «опалубка», «опалубки» и «опалубочные» считались одним словом), и файл попадает
    /// в серию по первому слову, которое связывает его с частью остальных файлов.
    /// </summary>
    public static class NameGrouper
    {
        /// <summary>Длина основы слова. Пять букв различают работы, но терпят падежи.</summary>
        const int StemLength = 5;

        /// <summary>Короткие слова — это шифры разделов («АР», «КЖ») и предлоги, они не о работе.</summary>
        const int MinWordLength = 3;

        /// <summary>Файлы набраны больше чем из одной папки — раскладывать по папкам.</summary>
        public static bool FromSeveralFolders(IList<string> files)
        {
            var folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string file in files)
            {
                folders.Add(FolderOf(file));
                if (folders.Count > 1) return true;
            }
            return false;
        }

        /// <summary>
        /// Повторяет в сборке структуру папок: папки идут по порядку, внутри папки —
        /// файлы по именам, причём «2» раньше «10», как в проводнике. Имя папки
        /// в таких выгрузках и есть название работы — «1. Песчаная подушка под приямки».
        /// </summary>
        public static List<NameGroup> ByFolders(IList<string> files)
        {
            var sorted = new List<string>(files);
            sorted.Sort(ComparePaths);

            var groups = new List<NameGroup>();
            NameGroup current = null;
            string currentFolder = null;

            foreach (string file in sorted)
            {
                string folder = FolderOf(file);
                if (current == null || !string.Equals(folder, currentFolder, StringComparison.OrdinalIgnoreCase))
                {
                    current = new NameGroup { Title = FolderName(folder) };
                    groups.Add(current);
                    currentFolder = folder;
                }
                current.Files.Add(file);
            }
            return groups;
        }

        static string FolderOf(string path)
        {
            if (string.IsNullOrEmpty(path)) return "";
            try
            {
                string folder = Path.GetDirectoryName(path);
                return folder ?? "";
            }
            catch (System.Exception) { return ""; }
        }

        /// <summary>Имя папки без пути — им подписывается ряд в модели и вкладки листов.</summary>
        public static string FolderName(string folder)
        {
            if (string.IsNullOrEmpty(folder)) return "";
            string[] parts = Segments(folder);
            return parts.Length > 0 ? parts[parts.Length - 1] : folder;
        }

        /// <summary>Порядок как в проводнике: по папкам, внутри — по именам с учётом чисел.</summary>
        internal static int ComparePaths(string a, string b)
        {
            string[] x = Segments(a), y = Segments(b);
            int common = Math.Min(x.Length, y.Length);

            for (int i = 0; i < common; i++)
            {
                int order = CompareNatural(x[i], y[i]);
                if (order != 0) return order;
            }
            return x.Length.CompareTo(y.Length);
        }

        static string[] Segments(string path)
        {
            return path.Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries);
        }

        /// <summary>Сравнение с числами как числами: «Лист 2» раньше «Листа 10».</summary>
        internal static int CompareNatural(string a, string b)
        {
            int i = 0, j = 0;
            while (i < a.Length && j < b.Length)
            {
                if (char.IsDigit(a[i]) && char.IsDigit(b[j]))
                {
                    int startA = i, startB = j;
                    while (i < a.Length && char.IsDigit(a[i])) i++;
                    while (j < b.Length && char.IsDigit(b[j])) j++;

                    string numberA = a.Substring(startA, i - startA).TrimStart('0');
                    string numberB = b.Substring(startB, j - startB).TrimStart('0');

                    if (numberA.Length != numberB.Length) return numberA.Length - numberB.Length;
                    int digits = string.CompareOrdinal(numberA, numberB);
                    if (digits != 0) return digits;
                    continue;
                }

                char ca = char.ToLowerInvariant(a[i]), cb = char.ToLowerInvariant(b[j]);
                if (ca != cb) return ca.CompareTo(cb);
                i++; j++;
            }
            return (a.Length - i) - (b.Length - j);
        }

        /// <summary>Одна серия из всех файлов — раскладка в том порядке, что задал пользователь.</summary>
        public static List<NameGroup> Single(IList<string> files)
        {
            var group = new NameGroup { Title = "" };
            foreach (string file in files) group.Files.Add(file);

            var groups = new List<NameGroup>();
            if (group.Files.Count > 0) groups.Add(group);
            return groups;
        }

        /// <summary>
        /// Собирает файлы в серии. Порядок серий и порядок файлов внутри серии —
        /// как в исходном списке: ручную расстановку в диалоге это не ломает.
        /// </summary>
        public static List<NameGroup> Group(IList<string> files)
        {
            int count = files.Count;
            var words = new List<List<string>>(count);   // слова имени как есть, для подписи серии
            var stems = new List<List<string>>(count);   // их основы, для сравнения
            var seenIn = new Dictionary<string, int>();  // в скольких файлах встретилась основа

            for (int i = 0; i < count; i++)
            {
                List<string> nameWords = Words(SafeName(files[i]));
                var nameStems = new List<string>(nameWords.Count);
                var once = new HashSet<string>();

                foreach (string word in nameWords)
                {
                    string stem = Stem(word);
                    nameStems.Add(stem);
                    if (!once.Add(stem)) continue;

                    int seen;
                    seenIn[stem] = seenIn.TryGetValue(stem, out seen) ? seen + 1 : 1;
                }

                words.Add(nameWords);
                stems.Add(nameStems);
            }

            var groups = new List<NameGroup>();
            var byStem = new Dictionary<string, NameGroup>(StringComparer.Ordinal);

            for (int i = 0; i < count; i++)
            {
                int keyIndex = ChooseKey(stems[i], seenIn, count);
                string key = keyIndex >= 0 ? stems[i][keyIndex] : "";

                NameGroup group;
                if (key.Length == 0 || !byStem.TryGetValue(key, out group))
                {
                    group = new NameGroup
                    {
                        Title = keyIndex >= 0 ? words[i][keyIndex] : SafeName(files[i])
                    };
                    groups.Add(group);
                    if (key.Length > 0) byStem[key] = group;
                }
                group.Files.Add(files[i]);
            }

            // Серия из одного файла — не серия: подписываем её самим именем файла,
            // иначе в отчёте появится случайное слово вроде «плиты».
            foreach (NameGroup single in groups)
            {
                if (single.Files.Count == 1) single.Title = SafeName(single.Files[0]);
            }
            return groups;
        }

        /// <summary>
        /// Слово, по которому файл узнаётся в ряду остальных. Годится то, что встретилось
        /// не только здесь (иначе серия из одного файла) и не у всех подряд (общее слово —
        /// это название объекта, оно ничего не разделяет). Из подходящих берётся самое
        /// редкое: «опалубка» у пяти файлов из тридцати говорит о работе, а «этаж» у
        /// двадцати восьми — ни о чём. При равной частоте — то, что стоит в имени раньше.
        /// Ни одного подходящего нет — берётся слово, общее для всех: пусть файлы окажутся
        /// одной серией, это ближе к правде, чем россыпь из одиночек.
        /// </summary>
        static int ChooseKey(List<string> stems, Dictionary<string, int> seenIn, int total)
        {
            int best = -1, bestSeen = 0;
            for (int i = 0; i < stems.Count; i++)
            {
                int seen = seenIn[stems[i]];
                if (seen <= 1 || seen >= total) continue;
                if (best < 0 || seen < bestSeen) { best = i; bestSeen = seen; }
            }
            if (best >= 0) return best;

            for (int i = 0; i < stems.Count; i++)
            {
                if (seenIn[stems[i]] > 1) return i;
            }
            return -1;
        }

        static string SafeName(string path)
        {
            if (string.IsNullOrEmpty(path)) return "";
            try { return Path.GetFileNameWithoutExtension(path); }
            catch (System.Exception) { return path; }   // путь с недопустимыми знаками
        }

        /// <summary>Значимые слова имени: без разделителей, номеров и шифров разделов.</summary>
        internal static List<string> Words(string name)
        {
            var words = new List<string>();
            if (string.IsNullOrEmpty(name)) return words;

            var current = new StringBuilder();
            for (int i = 0; i <= name.Length; i++)
            {
                char c = i < name.Length ? name[i] : ' ';
                if (char.IsLetter(c) || char.IsDigit(c)) { current.Append(c); continue; }

                Take(words, current.ToString());
                current.Length = 0;
            }
            return words;
        }

        static void Take(List<string> words, string word)
        {
            if (word.Length < MinWordLength) return;

            bool hasLetter = false;
            foreach (char c in word)
            {
                if (char.IsLetter(c)) { hasLetter = true; break; }
            }
            if (!hasLetter) return;   // «01», «2024» — это номера, а не работа

            words.Add(word);
        }

        /// <summary>Основа слова: начало без учёта регистра, «ё» и «е» — одна буква.</summary>
        internal static string Stem(string word)
        {
            int length = Math.Min(word.Length, StemLength);
            var stem = new StringBuilder(length);
            for (int i = 0; i < length; i++)
            {
                char c = char.ToLowerInvariant(word[i]);
                stem.Append(c == 'ё' ? 'е' : c);
            }
            return stem.ToString();
        }
    }
}
