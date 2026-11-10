namespace DwGangsta
{
    /// <summary>Итог обработки одного листа.</summary>
    public class SplitResult
    {
        public string LayoutName;
        public string FilePath;
        public int EntitiesKept;
        public int EntitiesRemoved;
        public int ViewportCount;

        /// <summary>Сколько контуров видовых экранов обведено в модели.</summary>
        public int ViewOutlines;
        public string Warning;
        public string Error;

        public bool Success { get { return Error == null; } }
    }
}
