using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;
using AcAp = Autodesk.AutoCAD.ApplicationServices.Application;

namespace DwGangsta
{
    public class Commands
    {
        /// <summary>Разделить чертёж по листам — открывает палитру на вкладке разделения.</summary>
        [CommandMethod("DWGSPLIT", CommandFlags.Modal)]
        public void SplitLayouts()
        {
            ShowPalette(DwGangstaPalette.Tab.Split);
        }

        /// <summary>
        /// Палитра — отдельным методом: код без интерфейса не должен тянуть за собой
        /// Windows Forms, в ядре без графики (accoreconsole) это роняет процесс.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static void ShowPalette(DwGangstaPalette.Tab tab)
        {
            DwGangstaPalette.Show(tab);
        }

        /// <summary>
        /// Разделение с настройками, которые отдала кнопка «Разделить» в палитре.
        /// Сама по себе команда ни о чём не спрашивает — её вызывает палитра.
        /// </summary>
        [CommandMethod("DWGSPLITRUN", CommandFlags.Modal)]
        public void SplitFromPalette()
        {
            Document doc = AcAp.DocumentManager.MdiActiveDocument;
            if (doc == null) return;

            SplitOptions options = TakePendingSplit();
            if (options == null)
            {
                doc.Editor.WriteMessage("\nНастройки разделения задаются в палитре — команда DWGSPLIT.\n");
                return;
            }

            List<string> layouts;
            if (!Prepare(doc, new SplitOptions(), out layouts)) return;

            // Пока настраивали, лист могли удалить или переименовать — делим только то, что есть.
            options.Layouts = options.Layouts.FindAll(layouts.Contains);
            if (options.Layouts.Count == 0)
            {
                doc.Editor.WriteMessage("\nВыбранных листов в чертеже больше нет.\n");
                return;
            }
            Execute(doc, options);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static SplitOptions TakePendingSplit()
        {
            SplitOptions options = DwGangstaPalette.PendingSplit;
            DwGangstaPalette.PendingSplit = null;
            return options;
        }

        /// <summary>
        /// Обводит в модели открытого чертежа то, что показывают видовые экраны всех его
        /// листов. Чертёж не разбирается: появляется только разметка на отдельном слое,
        /// и видно, какой участок модели куда попадает, не листая вкладки листов.
        /// </summary>
        [CommandMethod("DWGVIEWS", CommandFlags.Modal)]
        public void HighlightViewports()
        {
            Document doc = AcAp.DocumentManager.MdiActiveDocument;
            if (doc == null) return;

            doc.Editor.WriteMessage(Environment.NewLine + Highlight(doc) +
                                    Environment.NewLine);
        }

        /// <summary>
        /// Рисует рамки видовых экранов в модели открытого чертежа и рассказывает, что вышло.
        /// Одним текстом и для командной строки, и для диалога разделения.
        /// </summary>
        internal static string Highlight(Document doc)
        {
            int drawn, layouts;
            short color = NextHighlightColor();
            using (DocumentLock docLock = doc.LockDocument())
            {
                drawn = ViewOutlines.DrawAllLayouts(doc.Database, MergeOptions.ViewLayerName,
                                                    out layouts, color);
            }

            try { doc.Editor.Regen(); }
            catch (System.Exception ex) { Trace.Write("перерисовать не удалось: " + ex.Message); }

            if (drawn == 0)
            {
                return layouts == 0
                    ? "В чертеже нет листов — обводить нечего."
                    : "Ни на одном из листов (" + layouts + ") нет окон в модель — обводить нечего.";
            }

            return "Обведено видовых экранов: " + drawn + ", листов просмотрено: " + layouts +
                   ", цвет — " + ColorNames[color - 1] +
                   ". Рамки лежат в модели на слое «" + MergeOptions.ViewLayerName +
                   "»: он не печатается и в видовых экранах не виден, так что на листы " +
                   "разметка не попадёт. Повторный вызов сотрёт эти рамки и обведёт заново другим цветом.";
        }

        // Стандартные цвета AutoCAD без белого и чёрного: ACI 1–6.
        static readonly string[] ColorNames = { "красный", "жёлтый", "зелёный", "голубой", "синий", "пурпурный" };
        static readonly Random Dice = new Random();
        static short _lastColor;

        /// <summary>
        /// Цвет подсветки наугад — но не тот же, что в прошлый раз, иначе повтора не видно.
        /// Им же обводятся экраны в файлах «Разделить по листам»: у соседних файлов цвета разные.
        /// </summary>
        internal static short NextHighlightColor()
        {
            short color;
            do color = (short)Dice.Next(1, ColorNames.Length + 1);
            while (color == _lastColor);
            _lastColor = color;
            return color;
        }

        /// <summary>
        /// То же самое без диалога: все листы, настройки по умолчанию.
        /// Пригодна для пакетной обработки и работает в accoreconsole.
        /// </summary>
        [CommandMethod("DWGSPLITALL", CommandFlags.Modal)]
        public void SplitLayoutsNoDialog()
        {
            Trace.Write("=== DWGSPLITALL");
            Document doc = AcAp.DocumentManager.MdiActiveDocument;
            if (doc == null) return;

            var options = new SplitOptions();
            List<string> layouts;
            if (!Prepare(doc, options, out layouts)) return;

            options.Layouts = layouts;
            Execute(doc, options);
        }

        /// <summary>
        /// Сборка без диалога: список файлов через точку с запятой в переменной среды
        /// DWGANGSTA_MERGE_FILES, путь результата — в DWGANGSTA_MERGE_TARGET.
        /// </summary>
        [CommandMethod("DWGMERGEBATCH", CommandFlags.Modal)]
        public void MergeDrawingsBatch()
        {
            Trace.Write("=== DWGMERGEBATCH");
            Document doc = AcAp.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            Editor ed = doc.Editor;

            string files = Environment.GetEnvironmentVariable("DWGANGSTA_MERGE_FILES");
            string target = Environment.GetEnvironmentVariable("DWGANGSTA_MERGE_TARGET");
            if (string.IsNullOrEmpty(files) || string.IsNullOrEmpty(target))
            {
                ed.WriteMessage("\nНе заданы DWGANGSTA_MERGE_FILES и DWGANGSTA_MERGE_TARGET.\n");
                return;
            }

            var options = new MergeOptions { TargetPath = target, OpenResult = false };

            // Список может прийти файлом: в именах чертежей встречаются точки с запятой,
            // и через разделитель такой список не передать.
            string list = Environment.GetEnvironmentVariable("DWGANGSTA_MERGE_LIST");
            if (!string.IsNullOrEmpty(list) && File.Exists(list))
            {
                foreach (string line in File.ReadAllLines(list))
                {
                    string path = line.Trim();
                    if (path.Length > 0) options.Files.Add(path);
                }
            }

            // Для автотестов: перенос модели целиком или только видимого в листах.
            if (Environment.GetEnvironmentVariable("DWGANGSTA_MERGE_KEEPALL") == "1")
                options.TrimModel = false;
            if (Environment.GetEnvironmentVariable("DWGANGSTA_MERGE_TRIM") == "1")
                options.TrimModel = true;
            // Как из окна сборки: вкладки листов — ровно в порядке списка файлов.
            if (Environment.GetEnvironmentVariable("DWGANGSTA_MERGE_TABS_IN_ORDER") == "1")
                options.TabsInListOrder = true;
            string placement = Environment.GetEnvironmentVariable("DWGANGSTA_MERGE_PLACEMENT");
            if (placement == "asis") options.Placement = MergePlacement.AsIs;
            else if (placement == "nooverlap") options.Placement = MergePlacement.NoOverlap;
            else if (placement == "byname") options.Placement = MergePlacement.ByName;

            // Папки, добавленные пользователем: в пакетном режиме — списком через «;».
            string roots = Environment.GetEnvironmentVariable("DWGANGSTA_MERGE_ROOTS");
            if (!string.IsNullOrEmpty(roots))
            {
                foreach (string root in roots.Split(';'))
                {
                    string path = root.Trim();
                    if (path.Length > 0) options.Roots.Add(path);
                }
            }
            if (options.Files.Count == 0)
            {
                foreach (string file in files.Split(';'))
                {
                    string path = file.Trim();
                    if (path.Length > 0) options.Files.Add(path);
                }
            }
            ExecuteMerge(doc, options);
        }

        internal static void ExecuteMerge(Document doc, MergeOptions options)
        {
            Editor ed = doc.Editor;
            ed.WriteMessage("\n" + ZoomZoom.RescaleApp.PluginName + ": собирается чертежей — " + options.Files.Count + "\n");

            var watch = Stopwatch.StartNew();
            MergeResult result;
            using (DocumentLock docLock = doc.LockDocument())
            {
                result = RunMerge(options, ed);
            }
            watch.Stop();

            ed.WriteMessage("\nГотово за " + watch.Elapsed.TotalSeconds.ToString("F1") +
                            " с. Листов в сборке: " + result.LayoutCount);
            if (result.FailedCount > 0) ed.WriteMessage(", не удалось файлов: " + result.FailedCount);
            ed.WriteMessage("\n" + result.TargetPath + "\n");

            foreach (MergeItemResult item in result.Items)
            {
                if (item.Success) continue;
                ed.WriteMessage("  ! " + item.FileName + ": " + item.Error + "\n");
            }

            if (options.OpenResult && File.Exists(result.TargetPath)) OpenMerged(ed, result.TargetPath);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static MergeResult RunMerge(MergeOptions options, Editor ed)
        {
            return DrawingMerger.Merge(options, message => ed.WriteMessage(message + "\n"));
        }

        /// <summary>
        /// Открытие готовой сборки. Держится отдельным методом: обращение к списку документов
        /// в ядре без графики (accoreconsole) роняет процесс уже на подготовке метода.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        static void OpenMerged(Editor ed, string path)
        {
            try { AcAp.DocumentManager.Open(path, false); }
            catch (System.Exception ex) { ed.WriteMessage("Открыть сборку не удалось: " + ex.Message + "\n"); }
        }

        /// <summary>Проверяет, что чертёж пригоден для разделения, и собирает список листов.</summary>
        static bool Prepare(Document doc, SplitOptions options, out List<string> layouts)
        {
            layouts = null;
            Editor ed = doc.Editor;
            Database db = doc.Database;

            string sourcePath = db.Filename;
            Trace.Write("source=" + sourcePath);
            if (string.IsNullOrEmpty(sourcePath) || !File.Exists(sourcePath))
            {
                ed.WriteMessage("\nЧертёж ещё ни разу не сохранён. Сохраните его и повторите команду.\n");
                return false;
            }

            layouts = LayoutSplitter.GetLayoutNames(db);
            Trace.Write("layouts=" + layouts.Count);
            if (layouts.Count == 0)
            {
                ed.WriteMessage("\nВ чертеже нет листов — делить нечего.\n");
                return false;
            }
            return true;
        }

        /// <summary>Тема палитры — та же, что у интерфейса AutoCAD (COLORTHEME).</summary>
        internal static Theme DialogTheme()
        {
            try
            {
                return Theme.FromColorTheme(Convert.ToInt32(AcAp.GetSystemVariable("COLORTHEME")));
            }
            catch (System.Exception)
            {
                return Theme.Dark;
            }
        }

        static void Execute(Document doc, SplitOptions options)
        {
            Editor ed = doc.Editor;
            ed.WriteMessage("\n" + ZoomZoom.RescaleApp.PluginName + ": обрабатывается листов — " + options.Layouts.Count + "\n");

            var watch = Stopwatch.StartNew();
            List<SplitResult> results;
            using (DocumentLock docLock = doc.LockDocument())
            {
                results = LayoutSplitter.Split(doc.Database, doc.Database.Filename, options,
                    message => ed.WriteMessage(message + "\n"));
            }
            watch.Stop();

            Report(ed, results, watch.Elapsed);
        }

        static void Report(Editor ed, List<SplitResult> results, TimeSpan elapsed)
        {
            int ok = 0, failed = 0, removed = 0;
            foreach (SplitResult r in results)
            {
                if (r.Success) { ok++; removed += r.EntitiesRemoved; }
                else failed++;
            }

            ed.WriteMessage("\nГотово за " + elapsed.TotalSeconds.ToString("F1") +
                            " с. Создано файлов: " + ok);
            if (removed > 0) ed.WriteMessage(", удалено объектов модели: " + removed);
            if (failed > 0) ed.WriteMessage(", с ошибками: " + failed);
            ed.WriteMessage("\n");

            foreach (SplitResult r in results)
            {
                if (r.Success) continue;
                ed.WriteMessage("  ! " + r.LayoutName + ": " + r.Error + "\n");
            }
        }
    }
}
