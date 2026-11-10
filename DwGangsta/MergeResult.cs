using System.Collections.Generic;

namespace DwGangsta
{
    /// <summary>Итог вливания одного исходного чертежа в сборку.</summary>
    public class MergeItemResult
    {
        public string FilePath;
        public string FileName;

        /// <summary>Серия, в которую попал файл по своему имени. Пусто — группировка выключена.</summary>
        public string Group;

        public int EntitiesCopied;

        /// <summary>Сколько объектов модели не поехало в сборку: их не видно ни в одном листе.</summary>
        public int EntitiesTrimmed;

        /// <summary>Сколько поверхностей и стилей Civil 3D переименовано, чтобы не совпасть с уже собранными.</summary>
        public int CivilRenamed;

        public List<string> Layouts = new List<string>();
        public string Error;

        public bool Success { get { return Error == null; } }
    }

    /// <summary>Итог всей сборки.</summary>
    public class MergeResult
    {
        public string TargetPath;
        public List<MergeItemResult> Items = new List<MergeItemResult>();

        public int LayoutCount
        {
            get
            {
                int count = 0;
                foreach (MergeItemResult item in Items) count += item.Layouts.Count;
                return count;
            }
        }

        public int FailedCount
        {
            get
            {
                int count = 0;
                foreach (MergeItemResult item in Items) if (!item.Success) count++;
                return count;
            }
        }
    }
}
