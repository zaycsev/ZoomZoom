namespace ZoomZoom
{
    /// <summary>
    /// Язык графического интерфейса — ленты, палитр и окон: тот, что выбран в
    /// установщике; без установщика — английский в английском AutoCAD, русский во
    /// всех остальных. Командная строка и отчёты всегда по-русски. Язык задаётся
    /// один раз при загрузке плагина (RescaleApp.DetectEnglish) и до конца сеанса
    /// не меняется.
    ///
    /// Класс без ссылок на AutoCAD: его можно подключить и к инструментам,
    /// которые работают без AutoCAD (например, отрисовщику палитр).
    /// </summary>
    internal static class Lang
    {
        /// <summary>true — интерфейс по-английски.</summary>
        internal static bool English { get; set; }

        /// <summary>Текст на языке интерфейса.</summary>
        internal static string T(string ru, string en)
        {
            return English ? en : ru;
        }
    }
}
