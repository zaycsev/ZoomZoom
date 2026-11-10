using System;
using Autodesk.AutoCAD.DatabaseServices;

namespace DwGangsta
{
    /// <summary>
    /// Настройки самого чертежа — единицы, система мер, масштаб типов линий и прочее,
    /// что записано в заголовке файла, а не в объектах.
    ///
    /// Пустой чертёж, с которого начинается сборка, AutoCAD строит по своим встроенным
    /// умолчаниям: безразмерные единицы и имперская система. Чертежи, которые в неё
    /// приезжают, обычно метрические, поэтому сборка перенимает устройство первого
    /// добавленного файла — тогда она ведёт себя так же, как исходники: и вставляется
    /// в другие чертежи с правильным масштабом, и новые штриховки берёт из метрических
    /// образцов, и точки с типами линий показывает как привыкли.
    ///
    /// Сами объекты это не трогает: слои, стили и геометрия приезжают из файлов как есть.
    /// </summary>
    public static class DrawingSetup
    {
        /// <summary>Переносит настройки чертежа. Возвращает, сколько удалось перенести.</summary>
        public static int CopyFrom(Database source, Database target)
        {
            if (source == null || target == null) return 0;
            int copied = 0;

            // --- единицы и система мер ---------------------------------------------
            // Главное: по ним AutoCAD масштабирует чертёж при вставке в другой файл
            // и выбирает образцы штриховок — метрические или дюймовые.
            copied += Try(() => target.Insunits = source.Insunits, "единицы вставки");
            copied += Try(() => target.Measurement = source.Measurement, "система мер");
            copied += Try(() => target.Lunits = source.Lunits, "формат длин");
            copied += Try(() => target.Luprec = source.Luprec, "точность длин");
            copied += Try(() => target.Aunits = source.Aunits, "формат углов");
            copied += Try(() => target.Auprec = source.Auprec, "точность углов");
            copied += Try(() => target.Angbase = source.Angbase, "начало отсчёта углов");
            copied += Try(() => target.Angdir = source.Angdir, "направление углов");
            copied += Try(() => target.Unitmode = source.Unitmode, "запись единиц");

            // --- то, от чего зависит сам вид чертежа --------------------------------
            copied += Try(() => target.Ltscale = source.Ltscale, "масштаб типов линий");
            copied += Try(() => target.Celtscale = source.Celtscale, "масштаб типов линий у новых объектов");
            copied += Try(() => target.Psltscale = source.Psltscale, "типы линий в видовых экранах");
            copied += Try(() => target.MsLtScale = source.MsLtScale, "типы линий по масштабу аннотаций");
            copied += Try(() => target.LineWeightDisplay = source.LineWeightDisplay, "показ весов линий");
            copied += Try(() => target.Pdmode = source.Pdmode, "вид точек");
            copied += Try(() => target.Pdsize = source.Pdsize, "размер точек");
            copied += Try(() => target.Attmode = source.Attmode, "показ атрибутов блоков");

            // --- умолчания для дальнейшего черчения ---------------------------------
            copied += Try(() => target.DimAssoc = source.DimAssoc, "связанность размеров");
            copied += Try(() => target.Splinesegs = source.Splinesegs, "гладкость сплайнов");
            copied += Try(() => target.Splinetype = source.Splinetype, "тип сглаживания");
            copied += Try(() => target.Plinegen = source.Plinegen, "типы линий вдоль полилинии");
            copied += Try(() => target.Plinewid = source.Plinewid, "ширина полилиний");

            // Текущий масштаб аннотаций (CANNOSCALE) сюда не попадает намеренно:
            // из чертежа, открытого фоновой базой, он читается как служебный «1:1_1»
            // вместо настоящего, и перенос только засоряет список масштабов сборки.

            Trace.Write("настройки чертежа перенесены: " + copied);
            return copied;
        }

        /// <summary>
        /// Переносит одну настройку. Чертежи приходят всякие, и отдельная настройка
        /// может не прочитаться или не приняться — тогда она просто остаётся прежней,
        /// а сборка продолжается.
        /// </summary>
        static int Try(Action copy, string what)
        {
            try
            {
                copy();
                return 1;
            }
            catch (Exception ex)
            {
                Trace.Write("  настройка «" + what + "» не перенесена: " + ex.Message);
                return 0;
            }
        }
    }
}
