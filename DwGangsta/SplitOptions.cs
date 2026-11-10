using System.Collections.Generic;

namespace DwGangsta
{
    /// <summary>Настройки разделения чертежа на файлы по листам.</summary>
    public class SplitOptions
    {
        /// <summary>Имена листов, которые нужно выгрузить. Пусто — все листы чертежа.</summary>
        public List<string> Layouts = new List<string>();

        /// <summary>
        /// Обводить в модели то, что показывают видовые экраны листа. Контуры ложатся
        /// на свой слой, не печатаются и в самом листе не видны — они для работы в модели.
        /// </summary>
        public bool DrawViewOutlines = true;

        /// <summary>Чистить пространство модели, оставляя только видимое в видовых экранах листа.</summary>
        public bool TrimModel = true;

        /// <summary>Запас вокруг области видового экрана, доли от её размера (0.02 = 2%).</summary>
        public double MarginFactor = 0.0;

        /// <summary>Учитывать видовые экраны с отключённым показом (ВЭ «выключен»).</summary>
        public bool IncludeOffViewports = false;

        /// <summary>Не удалять объекты Civil 3D / AEC — у них есть взаимные ссылки.</summary>
        public bool KeepAecObjects = false;

        /// <summary>
        /// Очищать неиспользуемые слои, блоки и стили после чистки модели. По умолчанию
        /// выключено: чертёж должен оставаться в исходном виде со всеми слоями, блоками
        /// и стилями, даже если в конкретном листе они не используются.
        /// </summary>
        public bool PurgeUnused = false;

        /// <summary>Папка для файлов листов. Пусто — папка исходного чертежа.</summary>
        public string OutputFolder = "";

        /// <summary>Складывать результат в подпапку «ИмяЧертежа_Листы» внутри папки для файлов.</summary>
        public bool UseSubFolder = false;

        /// <summary>Перезаписывать уже существующие файлы вместо добавления индекса к имени.</summary>
        public bool Overwrite = false;
    }
}
