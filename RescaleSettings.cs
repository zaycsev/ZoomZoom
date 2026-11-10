using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace ZoomZoom
{
    /// <summary>
    /// Настройки, которые палитра передаёт командам ZOOMZOOMSEL и ZOOMZOOMALL.
    /// Кнопки ленты не масштабируют сами: выбор объектов требует контекста
    /// команды, поэтому кнопка запускает команду, а та берёт значения отсюда.
    ///
    /// Настройки переживают перезапуск AutoCAD: каждое изменение сразу пишется в
    /// %APPDATA%\ZoomZoom\settings.ini, а при загрузке плагина читается оттуда.
    /// Файл лежит не в папке плагина, а отдельно — поэтому не пропадает при
    /// переустановке и обновлении и общий для всех версий AutoCAD на машине.
    /// Нет файла (первый запуск) или он испорчен — берутся значения по
    /// умолчанию: уменьшить в 1000 раз, прокси расчленять, экраны пересчитывать.
    /// </summary>
    internal static class RescaleSettings
    {
        // Значения по умолчанию — то, что видит пользователь, только что
        // установивший плагин.
        private const double DefaultDivisor = 1000.0;
        private const bool DefaultEnlarge = false;
        private const bool DefaultExplodeProxies = true;
        private const bool DefaultScaleViewports = true;

        private static double _divisor = DefaultDivisor;
        private static bool _enlarge = DefaultEnlarge;
        private static bool _explodeProxies = DefaultExplodeProxies;
        private static bool _scaleViewports = DefaultScaleViewports;
        private static bool _loading;

        static RescaleSettings()
        {
            Load();
        }

        /// <summary>Во сколько раз — всегда больше 0 и не 1; направление задаёт Enlarge.</summary>
        public static double Divisor
        {
            get { return _divisor; }
            set { if (IsValidDivisor(value) && value != _divisor) { _divisor = value; Save(); } }
        }

        /// <summary>false — уменьшить (1:1000 → 1:1), true — увеличить (1:1 → 1:1000).</summary>
        public static bool Enlarge
        {
            get { return _enlarge; }
            set { if (value != _enlarge) { _enlarge = value; Save(); } }
        }

        public static bool ExplodeProxies
        {
            get { return _explodeProxies; }
            set { if (value != _explodeProxies) { _explodeProxies = value; Save(); } }
        }

        /// <summary>Пересчитывать видовые экраны на листах, чтобы листы после
        /// масштабирования модели выглядели как до него.</summary>
        public static bool ScaleViewports
        {
            get { return _scaleViewports; }
            set { if (value != _scaleViewports) { _scaleViewports = value; Save(); } }
        }

        internal static string FilePath
        {
            get
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "ZoomZoom", "settings.ini");
            }
        }

        private static bool IsValidDivisor(double v)
        {
            return v > 0 && v <= 1000000 && Math.Abs(v - 1.0) > 1e-12 && !double.IsNaN(v);
        }

        // ------------------------------------------------------------ файл

        /// <summary>
        /// Чтение. Каждое значение — отдельно: испорченная строка не сбрасывает
        /// остальные, а неизвестные ключи (от будущих версий) пропускаются.
        /// </summary>
        private static void Load()
        {
            _loading = true;
            try
            {
                if (!File.Exists(FilePath))
                    return;
                foreach (string raw in File.ReadAllLines(FilePath, Encoding.UTF8))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line[0] == '#' || line[0] == ';')
                        continue;
                    int eq = line.IndexOf('=');
                    if (eq <= 0)
                        continue;
                    string key = line.Substring(0, eq).Trim();
                    string val = line.Substring(eq + 1).Trim();

                    double d;
                    bool b;
                    switch (key)
                    {
                        case "Divisor":
                            if (double.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out d) && IsValidDivisor(d))
                                _divisor = d;
                            break;
                        case "Enlarge":
                            if (bool.TryParse(val, out b)) _enlarge = b;
                            break;
                        case "ExplodeProxies":
                            if (bool.TryParse(val, out b)) _explodeProxies = b;
                            break;
                        case "ScaleViewports":
                            if (bool.TryParse(val, out b)) _scaleViewports = b;
                            break;
                    }
                }
            }
            catch (Exception)
            {
                // Файл не прочитался — работаем со значениями по умолчанию.
            }
            finally
            {
                _loading = false;
            }
        }

        /// <summary>
        /// Запись — через временный файл и замену: если AutoCAD упадёт посреди
        /// записи, останется прежний целый файл, а не обрубок.
        /// </summary>
        private static void Save()
        {
            if (_loading)
                return;
            try
            {
                string path = FilePath;
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                var lines = new List<string>
                {
                    "# Зум-зум: настройки палитры. Можно удалить — вернутся значения по умолчанию.",
                    "Divisor=" + _divisor.ToString("R", CultureInfo.InvariantCulture),
                    "Enlarge=" + _enlarge,
                    "ExplodeProxies=" + _explodeProxies,
                    "ScaleViewports=" + _scaleViewports,
                };
                string tmp = path + ".tmp";
                File.WriteAllLines(tmp, lines, new UTF8Encoding(false));
                if (File.Exists(path))
                    File.Replace(tmp, path, null);
                else
                    File.Move(tmp, path);
            }
            catch (Exception)
            {
                // Не записалось (нет прав, диск) — настройка действует до
                // закрытия AutoCAD, работе это не мешает.
            }
        }
    }
}
