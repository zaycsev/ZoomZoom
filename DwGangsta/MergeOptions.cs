using System.Collections.Generic;

namespace DwGangsta
{
    /// <summary>Как раскладывать перенесённые чертежи в модели сборки.</summary>
    public enum MergePlacement
    {
        /// <summary>Как есть: каждый чертёж ложится в свои исходные координаты.</summary>
        AsIs,

        /// <summary>Исходные координаты, но участки раздвигаются, чтобы не наложиться.</summary>
        NoOverlap,

        /// <summary>По наименованию: папка — колонка, этап к этапу по высоте.</summary>
        ByName
    }

    /// <summary>Настройки сборки нескольких чертежей в один.</summary>
    public class MergeOptions
    {
        /// <summary>Файлы DWG, которые нужно собрать, в порядке размещения.</summary>
        public List<string> Files = new List<string>();

        /// <summary>
        /// Папки, которые пользователь добавлял кнопкой «Добавить папку», по порядку.
        /// Чертежи каждой такой папки подписываются своим цветом — видно, что откуда.
        /// </summary>
        public List<string> Roots = new List<string>();

        /// <summary>Куда сохранить собранный чертёж.</summary>
        public string TargetPath = "";

        /// <summary>Как раскладывать участки в модели сборки.</summary>
        public MergePlacement Placement = MergePlacement.ByName;

        /// <summary>Обводить в модели то, что показывают видовые экраны листов.</summary>
        public bool DrawViewOutlines = true;

        /// <summary>Слой для контуров видовых экранов.</summary>
        public const string ViewLayerName = "DwG_Видовые экраны";

        /// <summary>Слой для контуров видовых экранов сборки.</summary>
        public string ViewLayer = ViewLayerName;

        /// <summary>Обводить участок каждого исходного файла прямоугольником.</summary>
        public bool DrawFrames = true;

        /// <summary>Подписывать участок именем исходного файла.</summary>
        public bool DrawLabels = true;

        /// <summary>
        /// Переносить из модели только то, что видно хотя бы в одном видовом экране
        /// чертежа. Выключить — участки разъедутся: клетка каждого резервируется под
        /// всю модель, включая забытые объекты далеко в стороне от листов.
        /// </summary>
        public bool TrimModel = false;

        /// <summary>Промежуток между участками в модели, доли от размера участка.</summary>
        public double GapFactor = 0.05;

        /// <summary>
        /// Ставить рядом чертежи с похожими названиями: опалубка к опалубке,
        /// бетонирование к бетонированию. Серии определяются по именам файлов.
        /// </summary>
        public bool GroupSimilarNames = true;

        /// <summary>Слой для рамок и подписей — чтобы их можно было выключить одним щелчком.</summary>
        public const string ServiceLayerName = "DwG_Участки";

        /// <summary>Слой для рамок и подписей сборки.</summary>
        public string ServiceLayer = ServiceLayerName;

        /// <summary>Открыть собранный чертёж после сборки.</summary>
        public bool OpenResult = true;

        /// <summary>
        /// Вкладки листов — в порядке Files, как его расставили в окне сборки. Иначе —
        /// в порядке раскладки (серия за серией, по осям); так работает пакетная сборка.
        /// </summary>
        public bool TabsInListOrder = false;
    }
}
