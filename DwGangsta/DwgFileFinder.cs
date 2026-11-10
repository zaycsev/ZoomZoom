using System;
using System.Collections.Generic;
using System.IO;
using System.Security;

namespace DwGangsta
{
    /// <summary>
    /// Рекурсивный поиск DWG-файлов: обходит выбранную папку и все её подпапки,
    /// на любую глубину. Подпапка, к которой нет доступа (права, сетевой обрыв),
    /// пропускается — она не обрывает поиск по остальным.
    /// </summary>
    public static class DwgFileFinder
    {
        /// <summary>
        /// Ищет *.dwg в <paramref name="rootFolder"/> и всех вложенных папках.
        /// <paramref name="inaccessibleFolders"/> — сколько подпапок пришлось пропустить.
        /// </summary>
        public static List<string> FindDwgFiles(string rootFolder, out int inaccessibleFolders)
        {
            var found = new List<string>();
            int skipped = 0;
            CollectFrom(rootFolder, found, ref skipped);

            found.Sort(StringComparer.CurrentCulture);
            inaccessibleFolders = skipped;
            return found;
        }

        static void CollectFrom(string folder, List<string> found, ref int inaccessibleFolders)
        {
            string[] files;
            try
            {
                files = Directory.GetFiles(folder, "*.dwg");
            }
            catch (System.Exception ex) when (IsAccessProblem(ex))
            {
                inaccessibleFolders++;
                return;
            }

            foreach (string file in files)
            {
                // Шаблон "*.dwg" на некоторых томах сопоставляется и с короткими именами
                // вида "name~1.dwgXX" — отсекаем такие явной проверкой расширения.
                if (string.Equals(Path.GetExtension(file), ".dwg", StringComparison.OrdinalIgnoreCase))
                    found.Add(file);
            }

            string[] subfolders;
            try
            {
                subfolders = Directory.GetDirectories(folder);
            }
            catch (System.Exception ex) when (IsAccessProblem(ex))
            {
                inaccessibleFolders++;
                return;
            }

            foreach (string subfolder in subfolders)
                CollectFrom(subfolder, found, ref inaccessibleFolders);
        }

        static bool IsAccessProblem(System.Exception ex)
        {
            return ex is UnauthorizedAccessException || ex is IOException || ex is SecurityException;
        }
    }
}
