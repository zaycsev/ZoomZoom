using System;
using System.IO;
using System.Text;

namespace DwGangsta
{
    /// <summary>
    /// Диагностический журнал. Включается переменной среды ZOOMZOOM_TRACE,
    /// в которой указан путь к файлу журнала (прежнее имя DWGANGSTA_TRACE тоже понимается).
    /// </summary>
    internal static class Trace
    {
        static readonly string Path =
            Environment.GetEnvironmentVariable("ZOOMZOOM_TRACE") ??
            Environment.GetEnvironmentVariable("DWGANGSTA_TRACE");

        public static bool Enabled { get { return !string.IsNullOrEmpty(Path); } }

        public static void Write(string message)
        {
            if (!Enabled) return;
            try
            {
                File.AppendAllText(Path,
                    DateTime.Now.ToString("HH:mm:ss.fff") + "  " + message + Environment.NewLine,
                    Encoding.UTF8);
            }
            catch (System.Exception) { /* журнал не должен мешать работе */ }
        }
    }
}
