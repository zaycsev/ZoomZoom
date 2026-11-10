using System;
using System.Collections.Generic;
using System.Text;

namespace DwGangsta
{
    /// <summary>
    /// Оси, о которых чертёж: «Г-Д1-2», «1-7...А-Б...1-5», «В-Д 5-8». По ним чертежи
    /// одного участка поля встают в сборке друг под друга — подушка под опалубкой,
    /// опалубка под бетонированием, — независимо от того, как подробно назван файл.
    ///
    /// Разбирается то, что стоит после слов «в осях» (и его опечаток: «в ося», «в осяхх»)
    /// и до отметки: буквенный диапазон и числовой. Общий диапазон поля впереди —
    /// «1-7...А-Б...1-5» — не в счёт: за буквами идёт то, что нужно.
    /// </summary>
    public class AxisKey : IComparable<AxisKey>
    {
        /// <summary>Оси в имени нашлись.</summary>
        public bool Known;

        public int LetterFrom = -1, LetterTo = -1;
        public int NumberFrom = -1, NumberTo = -1;

        /// <summary>Как оси записаны в имени — для отчёта.</summary>
        public string Text = "";

        /// <summary>Разбирает имя файла. Осей нет — Known = false.</summary>
        public static AxisKey Parse(string name)
        {
            var key = new AxisKey();
            if (string.IsNullOrEmpty(name)) return key;

            bool marked;
            string tail = AfterMarker(name, out marked);
            tail = CutTail(tail);

            int letterAt, letterEnd;
            if (Letters(tail, marked, out letterAt, out letterEnd, key))
            {
                Numbers(tail, letterEnd, key);
            }
            else if (marked)
            {
                // «в осях 1-7» — про всё поле сразу.
                Numbers(tail, 0, key);
            }

            key.Known = key.LetterFrom >= 0 || key.NumberFrom >= 0;
            if (key.Known) key.Text = Describe(key);
            return key;
        }

        /// <summary>Часть имени после «в осях». Слова нет — разбираем имя целиком.</summary>
        static string AfterMarker(string name, out bool marked)
        {
            const string marker = "в ос";
            int at = name.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            marked = at >= 0;
            if (at < 0) return name;

            // «в осях», «в ося», «в осяхх» — пропускаем слово целиком.
            int from = at + marker.Length;
            while (from < name.Length && char.IsLetter(name[from])) from++;
            return name.Substring(from);
        }

        /// <summary>Отметки и уточнения в скобках к осям не относятся.</summary>
        static string CutTail(string text)
        {
            int at = text.IndexOf("отм", StringComparison.OrdinalIgnoreCase);
            if (at >= 0) text = text.Substring(0, at);

            at = text.IndexOf('(');
            if (at >= 0) text = text.Substring(0, at);

            return text;
        }

        static bool Letters(string text, bool marked, out int at, out int end, AxisKey key)
        {
            at = -1; end = -1;
            for (int i = 0; i < text.Length; i++)
            {
                if (!IsAxisLetter(text[i])) continue;

                // «Г-Д»: буква, тире, буква.
                int dash = i + 1;
                while (dash < text.Length && text[dash] == ' ') dash++;
                if (dash < text.Length && IsDash(text[dash]))
                {
                    int second = dash + 1;
                    while (second < text.Length && text[second] == ' ') second++;
                    if (second < text.Length && IsAxisLetter(text[second]))
                    {
                        key.LetterFrom = Order(text[i]);
                        key.LetterTo = Order(text[second]);
                        at = i; end = second + 1;
                        return true;
                    }
                }

                // Одиночная буква рядом с цифрой: «А1-5». Без слова «в осях» такому
                // не верим: «КЖ1-2-3» — это шифр чертежа, а не оси.
                if (!marked) continue;

                int next = i + 1;
                while (next < text.Length && (text[next] == ' ' || IsDash(text[next]))) next++;
                if (next < text.Length && char.IsDigit(text[next]))
                {
                    key.LetterFrom = key.LetterTo = Order(text[i]);
                    at = i; end = i + 1;
                    return true;
                }
            }
            return false;
        }

        static void Numbers(string text, int from, AxisKey key)
        {
            for (int i = from; i < text.Length; i++)
            {
                if (!char.IsDigit(text[i])) continue;

                int firstEnd = i;
                while (firstEnd < text.Length && char.IsDigit(text[firstEnd])) firstEnd++;

                int dash = firstEnd;
                while (dash < text.Length && text[dash] == ' ') dash++;
                if (dash < text.Length && IsDash(text[dash]))
                {
                    int second = dash + 1;
                    while (second < text.Length && text[second] == ' ') second++;

                    int secondEnd = second;
                    while (secondEnd < text.Length && char.IsDigit(text[secondEnd])) secondEnd++;

                    if (secondEnd > second)
                    {
                        key.NumberFrom = Number(text, i, firstEnd);
                        key.NumberTo = Number(text, second, secondEnd);
                        return;
                    }
                }

                key.NumberFrom = key.NumberTo = Number(text, i, firstEnd);
                return;
            }
        }

        static int Number(string text, int from, int to)
        {
            int value;
            return int.TryParse(text.Substring(from, to - from), out value) ? value : -1;
        }

        static bool IsDash(char c)
        {
            return c == '-' || c == '–' || c == '—' || c == '_';
        }

        /// <summary>Ось обозначают прописной кириллицей: А, Б, В… Латиница тоже бывает.</summary>
        static bool IsAxisLetter(char c)
        {
            if (c >= 'А' && c <= 'Я') return true;
            if (c == 'Ё') return true;
            return c >= 'A' && c <= 'Z';
        }

        /// <summary>Порядок буквы в алфавите: «А» ближе всего к началу.</summary>
        static int Order(char c)
        {
            if (c == 'Ё') c = 'Е';
            if (c >= 'А' && c <= 'Я') return c - 'А';
            if (c >= 'A' && c <= 'Z') return 1000 + (c - 'A');   // латиница — своим рядом
            return 2000;
        }

        static string Describe(AxisKey key)
        {
            var text = new StringBuilder();
            if (key.LetterFrom >= 0)
            {
                text.Append(Letter(key.LetterFrom));
                if (key.LetterTo != key.LetterFrom) text.Append('-').Append(Letter(key.LetterTo));
            }
            if (key.NumberFrom >= 0)
            {
                if (text.Length > 0) text.Append(' ');
                text.Append(key.NumberFrom);
                if (key.NumberTo != key.NumberFrom) text.Append('-').Append(key.NumberTo);
            }
            return text.ToString();
        }

        static string Letter(int order)
        {
            if (order >= 1000 && order < 2000) return ((char)('A' + order - 1000)).ToString();
            return order >= 0 && order < 32 ? ((char)('А' + order)).ToString() : "?";
        }

        /// <summary>
        /// Порядок: сперва то, что ближе к «А», потом — что ближе к первой оси.
        /// Чертежи без осей идут после всех.
        /// </summary>
        public int CompareTo(AxisKey other)
        {
            if (other == null) return -1;
            if (Known != other.Known) return Known ? -1 : 1;
            if (!Known) return 0;

            int order = Rank(LetterFrom).CompareTo(Rank(other.LetterFrom));
            if (order != 0) return order;

            order = Rank(LetterTo).CompareTo(Rank(other.LetterTo));
            if (order != 0) return order;

            order = Rank(NumberFrom).CompareTo(Rank(other.NumberFrom));
            if (order != 0) return order;

            return Rank(NumberTo).CompareTo(Rank(other.NumberTo));
        }

        /// <summary>Не указано — значит, обо всём поле: такие идут первыми.</summary>
        static int Rank(int value)
        {
            return value < 0 ? -1 : value;
        }

        /// <summary>Одинаковые оси — один и тот же участок поля.</summary>
        public string Id
        {
            get
            {
                return Known
                    ? LetterFrom + ":" + LetterTo + ":" + NumberFrom + ":" + NumberTo
                    : "";
            }
        }
    }
}
