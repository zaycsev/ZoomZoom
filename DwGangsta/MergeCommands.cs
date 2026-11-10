using System.Runtime.CompilerServices;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Runtime;
using AcAp = Autodesk.AutoCAD.ApplicationServices.Application;

namespace DwGangsta
{
    /// <summary>
    /// Команды сборки из палитры. Держатся отдельно от остальных команд: класс завязан
    /// на Windows Forms, а в ядре без графики (accoreconsole) это роняет процесс.
    /// </summary>
    public class MergeCommands
    {
        /// <summary>Собрать несколько чертежей в один — открывает палитру на вкладке сборки.</summary>
        [CommandMethod("DWGMERGE", CommandFlags.Modal)]
        public void MergeDrawings()
        {
            Commands.ShowPalette(DwGangstaPalette.Tab.Merge);
        }

        /// <summary>
        /// Сборка с настройками, которые отдала кнопка «Собрать» в палитре.
        /// Сама по себе команда ни о чём не спрашивает — её вызывает палитра.
        /// </summary>
        [CommandMethod("DWGMERGERUN", CommandFlags.Modal)]
        public void MergeFromPalette()
        {
            Document doc = AcAp.DocumentManager.MdiActiveDocument;
            if (doc == null) return;

            MergeOptions options = TakePending();
            if (options == null)
            {
                doc.Editor.WriteMessage("\nНастройки сборки задаются в палитре — команда DWGMERGE.\n");
                return;
            }
            if (options.Files.Count == 0 || options.TargetPath.Length == 0) return;
            Commands.ExecuteMerge(doc, options);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static MergeOptions TakePending()
        {
            MergeOptions options = DwGangstaPalette.PendingMerge;
            DwGangstaPalette.PendingMerge = null;
            return options;
        }
    }
}
