using System;
using System.Collections.Generic;
using System.Globalization;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;

// Core.Application, а не Application из acmgd: команды обязаны работать и
// без интерфейса AutoCAD — в Core Console acmgd.dll не грузится.
using AcadApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;
using AcRxException = Autodesk.AutoCAD.Runtime.Exception;

[assembly: CommandClass(typeof(ZoomZoom.RescaleCommands))]

namespace ZoomZoom
{
    /// <summary>
    /// Пересчёт чертежа из условных единиц (1:1000, размер 6000 = 6000 единиц)
    /// в метры (1:1, размер 6000 = 6 единиц) для совмещения с облаком точек
    /// с тахеометра.
    ///
    /// Масштабирование идёт через Entity.TransformBy — тот же путь, которым
    /// пользуется штатная команда SCALE, поэтому корректно обрабатываются все
    /// типы примитивов: размеры, выноски, штриховки, вставки блоков, тексты,
    /// сплайны, атрибуты и т.д.
    ///
    /// Сверх штатного SCALE команда добирает всё, до чего TransformBy не достаёт,
    /// потому что размер там задан не координатами:
    ///   1) ставит каждому размеру DIMLFAC = коэффициент, чтобы размерное число
    ///      осталось прежним (6000 остаётся 6000, хотя длина стала 6);
    ///   2) делит DIMSCALE на размерах, выносках и допусках — высота текста и
    ///      стрелки считаются от него в момент отрисовки;
    ///   3) делит фиксированную высоту в стилях текста: она перебивает и высоту
    ///      объекта, и DIMTXT x DIMSCALE, а стиль общий на чертёж — если им
    ///      пользуется ещё и лист или блок, заводится копия стиля;
    ///   4) расчленяет прокси и вручную переписывает рамки OLE-вставок: и то,
    ///      и другое TransformBy молча не масштабирует;
    ///   5) временно снимает блокировку со слоёв, чтобы рамка выделения
    ///      захватила вообще всё, и возвращает блокировку обратно;
    ///   6) пересчитывает LTSCALE, PDSIZE и TEXTSIZE, иначе штриховые типы линий
    ///      после сжатия в 1000 раз рисуются сплошными.
    /// </summary>
    public class RescaleCommands
    {
        private const double DefaultDivisor = 1000.0;

        // Имена команд: длинное ZOOMZOOM* и короткое ZZ* для каждой. Дефис внутри
        // имени не ставим — в AutoCAD он занят под командную версию команды
        // (-LAYER, -INSERT), и имя с дефисом только путает при наборе.

        /// <summary>Открыть палитру с настройками. Главная точка входа.</summary>
        [CommandMethod("ZOOMZOOM", CommandFlags.Modal)]
        [CommandMethod("ZZ", CommandFlags.Modal)]
        public void RescaleUiCmd()
        {
            RescalePalette.Show();
        }

        /// <summary>Кнопка «Весь чертёж»: настройки берутся из палитры.</summary>
        [CommandMethod("ZOOMZOOMALL", CommandFlags.Modal | CommandFlags.Redraw)]
        [CommandMethod("ZZALL", CommandFlags.Modal | CommandFlags.Redraw)]
        public void RescaleAllCmd()
        {
            RunPreset(true);
        }

        /// <summary>Кнопка «Рамка»: настройки берутся из палитры.</summary>
        [CommandMethod("ZOOMZOOMSEL", CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        [CommandMethod("ZZSEL", CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void RescaleSelCmd()
        {
            RunPreset(false);
        }

        /// <summary>Быстрый вариант без вопросов — всегда деление на 1000.</summary>
        [CommandMethod("ZOOMZOOM1000", CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        [CommandMethod("ZZ1000", CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void Rescale1000Cmd()
        {
            Run(DefaultDivisor);
        }

        /// <summary>Всё вопросами в командной строке, мимо палитры.</summary>
        [CommandMethod("ZOOMZOOMASK", CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        [CommandMethod("ZZASK", CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        [CommandMethod("ZZTEXT", CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void RescaleCmd()
        {
            Run(null);
        }

        /// <summary>
        /// «Отрезок» — масштаб по опорному отрезку, как «Опорный отрезок» у
        /// штатного МАСШТАБ: выбрать объекты, указать две точки отрезка, потом
        /// новое положение второй (первая остаётся на месте) или длину числом.
        /// Масштабирует Зум-зум — со всем, что он сохраняет (размеры, текст,
        /// видовые экраны, штрихи), от первой точки отрезка.
        /// </summary>
        [CommandMethod("ZOOMZOOMREF", CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        [CommandMethod("ZZREF", CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void RescaleRefCmd()
        {
            Document doc = AcadApp.DocumentManager.MdiActiveDocument;
            if (doc == null)
                return;
            Database db = doc.Database;
            Editor ed = doc.Editor;

            List<ObjectId> relock = UnlockAllLayers(db);
            try
            {
                bool wholeSpace;
                Point3d? windowCenter;
                ObjectId[] ids = GetTargets(ed, db, out wholeSpace, out windowCenter);
                if (ids == null)
                    return;
                if (ids.Length == 0)
                {
                    ed.WriteMessage("\nНечего масштабировать.\n");
                    return;
                }
                if (!wholeSpace)
                {
                    ids = AskMixedViewports(ed, db, ids);
                    if (ids == null)
                        return;
                }

                var p1 = ed.GetPoint("\nПервая точка отрезка (останется на месте): ");
                if (p1.Status != PromptStatus.OK) { ed.WriteMessage("\nОтменено.\n"); return; }
                var p2o = new PromptPointOptions("\nВторая точка отрезка: ")
                {
                    BasePoint = p1.Value,
                    UseBasePoint = true,
                    UseDashedLine = true
                };
                var p2 = ed.GetPoint(p2o);
                if (p2.Status != PromptStatus.OK) { ed.WriteMessage("\nОтменено.\n"); return; }

                // Точки — в ПСК пользователя, считаем в мировых.
                Matrix3d ucs = ed.CurrentUserCoordinateSystem;
                Point3d a = p1.Value.TransformBy(ucs), b = p2.Value.TransformBy(ucs);
                double oldLength = a.DistanceTo(b);
                if (oldLength < 1e-12)
                {
                    ed.WriteMessage("\nТочки отрезка совпадают — масштабировать не от чего.\n");
                    return;
                }

                var lo = new PromptDistanceOptions("\nНовое положение второй точки или длина отрезка: ")
                {
                    BasePoint = p1.Value,
                    UseBasePoint = true,
                    UseDashedLine = true,
                    AllowNegative = false,
                    AllowZero = false
                };
                var nl = ed.GetDistance(lo);
                if (nl.Status != PromptStatus.OK) { ed.WriteMessage("\nОтменено.\n"); return; }
                double newLength = nl.Value;

                double divisor = oldLength / newLength;
                if (Math.Abs(divisor - 1.0) < 1e-12)
                {
                    ed.WriteMessage("\nДлина та же — менять нечего.\n");
                    return;
                }
                ed.WriteMessage("\nОтрезок {0} -> {1}: масштаб x{2}\n",
                    oldLength.ToString("0.####", CultureInfo.InvariantCulture),
                    newLength.ToString("0.####", CultureInfo.InvariantCulture),
                    (newLength / oldLength).ToString("0.########", CultureInfo.InvariantCulture));

                Rescale(doc, db, ed, ids, divisor, wholeSpace, relock.Count,
                        RescaleSettings.ExplodeProxies, windowCenter, a);
            }
            finally
            {
                RelockLayers(db, relock);
            }
        }

        /// <summary>
        /// Размеры Revit — в обычные размеры AutoCAD, без масштабирования. Для
        /// чертежей, уже обработанных Зум-зумом прошлых версий, и для любых
        /// выгрузок, где размеры нарисованы не AutoCAD-ом.
        /// </summary>
        [CommandMethod("ZOOMZOOMDIMS", CommandFlags.Modal)]
        [CommandMethod("ZZDIMS", CommandFlags.Modal)]
        public void DimsCmd()
        {
            Document doc = AcadApp.DocumentManager.MdiActiveDocument;
            if (doc == null)
                return;
            Database db = doc.Database;

            var ids = new List<ObjectId>();
            RXClass dimClass = RXObject.GetClass(typeof(Dimension));
            using (Transaction tr = db.TransactionManager.StartOpenCloseTransaction())
            {
                var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                foreach (ObjectId btrId in bt)
                {
                    var btr = (BlockTableRecord)tr.GetObject(btrId, OpenMode.ForRead);
                    if (!btr.IsLayout)
                        continue;
                    foreach (ObjectId id in btr)
                        if (id.ObjectClass.IsDerivedFrom(dimClass))
                            ids.Add(id);
                }
                tr.Commit();
            }

            List<ObjectId> relock = UnlockAllLayers(db);
            int n;
            try
            {
                using (doc.LockDocument())
                    n = DimNative.ConvertAll(db, ids);
            }
            finally
            {
                RelockLayers(db, relock);
            }
            doc.Editor.WriteMessage("\nРазмеров Revit переведено в размеры AutoCAD: {0} из {1}\n", n, ids.Count);
        }

        private static void Run(double? presetDivisor)
        {
            Document doc = AcadApp.DocumentManager.MdiActiveDocument;
            if (doc == null)
                return;

            Database db = doc.Database;
            Editor ed = doc.Editor;

            // Слои разблокируем до запроса выбора: заблокированные объекты иначе
            // просто не попадут в рамку.
            List<ObjectId> relock = UnlockAllLayers(db);
            try
            {
                bool wholeSpace;
                Point3d? windowCenter;
                ObjectId[] ids = GetTargets(ed, db, out wholeSpace, out windowCenter);
                if (ids == null)
                    return;
                if (!wholeSpace && ids.Length > 0)
                {
                    ids = AskMixedViewports(ed, db, ids);
                    if (ids == null)
                        return;
                }
                if (ids.Length == 0)
                {
                    ed.WriteMessage("\nНечего масштабировать.\n");
                    return;
                }

                double divisor;
                if (!GetDivisor(ed, presetDivisor, out divisor))
                    return;

                // Прокси (СПДС, объекты из BIM-выгрузок) молча игнорируют
                // TransformBy: вызов проходит без ошибки, а объект не двигается.
                // Расчленение — единственный способ их масштабировать.
                bool explodeProxies;
                if (presetDivisor.HasValue)
                    explodeProxies = true;               // RESCALE1000 — без вопросов
                else if (!GetExplodeProxies(ed, out explodeProxies))
                    return;

                Rescale(doc, db, ed, ids, divisor, wholeSpace, relock.Count, explodeProxies, windowCenter);
            }
            finally
            {
                RelockLayers(db, relock);
            }
        }

        /// <summary>
        /// Запуск с настройками из палитры — без вопросов в командной строке.
        /// </summary>
        private static void RunPreset(bool wholeSpace)
        {
            Document doc = AcadApp.DocumentManager.MdiActiveDocument;
            if (doc == null)
                return;

            Database db = doc.Database;
            Editor ed = doc.Editor;

            double value = RescaleSettings.Divisor;
            if (value <= 0 || Math.Abs(value - 1.0) < 1e-12)
            {
                ed.WriteMessage("\nКоэффициент {0} — менять нечего.\n", value);
                return;
            }
            // Вся команда работает от делителя: увеличение в N раз — это
            // деление на 1/N. Дальше всё пересчитывается само в обратную
            // сторону: DIMLFAC x1/N, DIMSCALE xN, видовые экраны, стили текста.
            double divisor = RescaleSettings.Enlarge ? 1.0 / value : value;

            List<ObjectId> relock = UnlockAllLayers(db);
            try
            {
                ObjectId[] ids;
                Point3d? windowCenter = null;
                if (wholeSpace)
                {
                    ids = CollectCurrentSpace(db);
                }
                else
                {
                    var pso = new PromptSelectionOptions();
                    pso.MessageForAdding = "\nВыберите объекты рамкой";
                    pso.RejectObjectsOnLockedLayers = false;

                    PromptSelectionResult psr = ed.GetSelection(pso);
                    if (psr.Status != PromptStatus.OK)
                    {
                        ed.WriteMessage("\nОтменено.\n");
                        return;
                    }
                    ids = AddHiddenInWindows(db, psr.Value, psr.Value.GetObjectIds(), ed.CurrentUserCoordinateSystem);
                    windowCenter = WindowCenter(psr.Value);
                    if (ids.Length > 0)
                    {
                        ids = AskMixedViewports(ed, db, ids);
                        if (ids == null)
                            return;
                    }
                }

                if (ids.Length == 0)
                {
                    ed.WriteMessage("\nНечего масштабировать.\n");
                    return;
                }

                Rescale(doc, db, ed, ids, divisor, wholeSpace, relock.Count,
                        RescaleSettings.ExplodeProxies, windowCenter);
            }
            finally
            {
                RelockLayers(db, relock);
            }
        }

        // ---------------------------------------------------------------- ввод

        /// <summary>
        /// Возвращает объекты для обработки. null — пользователь отказался.
        /// Enter на пустом выборе = всё текущее пространство. Объекты на
        /// замороженных и выключенных слоях рамка добирает сама (AddHiddenInWindows).
        /// </summary>
        private static ObjectId[] GetTargets(Editor ed, Database db, out bool wholeSpace,
                                             out Point3d? windowCenter)
        {
            wholeSpace = false;
            windowCenter = null;

            var pso = new PromptSelectionOptions();
            pso.MessageForAdding = "\nВыберите объекты рамкой (Enter — весь чертёж целиком)";
            pso.RejectObjectsOnLockedLayers = false;

            PromptSelectionResult psr = ed.GetSelection(pso);
            if (psr.Status == PromptStatus.OK)
            {
                windowCenter = WindowCenter(psr.Value);
                return AddHiddenInWindows(db, psr.Value, psr.Value.GetObjectIds(), ed.CurrentUserCoordinateSystem);
            }

            if (psr.Status == PromptStatus.Cancel)
            {
                ed.WriteMessage("\nОтменено.\n");
                return null;
            }

            // Ничего не выбрано — предлагаем обработать пространство целиком.
            var pko = new PromptKeywordOptions("\nНичего не выбрано. Масштабировать текущее пространство целиком?");
            pko.Keywords.Add("Yes", "Да", "Да");
            pko.Keywords.Add("No", "Нет", "Нет");
            pko.Keywords.Default = "Yes";
            pko.AllowNone = true;

            PromptResult pr = ed.GetKeywords(pko);
            if (pr.Status != PromptStatus.OK || pr.StringResult != "Yes")
            {
                ed.WriteMessage("\nОтменено.\n");
                return null;
            }

            wholeSpace = true;
            return CollectCurrentSpace(db);
        }

        /// <summary>Все объекты текущего пространства, включая скрытые слои.</summary>
        private static ObjectId[] CollectCurrentSpace(Database db)
        {
            var ids = new List<ObjectId>();
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                var btr = (BlockTableRecord)tr.GetObject(db.CurrentSpaceId, OpenMode.ForRead);
                foreach (ObjectId id in btr)
                    ids.Add(id);
                tr.Commit();
            }
            return ids.ToArray();
        }

        private static bool GetDivisor(Editor ed, double? presetDivisor, out double divisor)
        {
            if (presetDivisor.HasValue)
            {
                divisor = presetDivisor.Value;
                return true;
            }

            var pdo = new PromptDoubleOptions("\nРазделить чертёж на (меньше 1 — увеличить: 0.001 = в 1000 раз)");
            pdo.DefaultValue = DefaultDivisor;
            pdo.UseDefaultValue = true;
            pdo.AllowNegative = false;
            pdo.AllowZero = false;
            pdo.AllowNone = false;

            PromptDoubleResult pdr = ed.GetDouble(pdo);
            if (pdr.Status != PromptStatus.OK)
            {
                ed.WriteMessage("\nОтменено.\n");
                divisor = 0;
                return false;
            }

            divisor = pdr.Value;
            if (Math.Abs(divisor - 1.0) < 1e-12)
            {
                ed.WriteMessage("\nКоэффициент 1 — менять нечего.\n");
                return false;
            }
            return true;
        }

        /// <summary>
        /// Спрашивает, расчленять ли прокси-объекты. Без расчленения они останутся
        /// в исходном масштабе — приложения, которое умеет их пересчитать, в системе нет.
        /// </summary>
        private static bool GetExplodeProxies(Editor ed, out bool explode)
        {
            var pko = new PromptKeywordOptions(
                "\nРасчленить объекты-прокси (СПДС, выгрузки из BIM)? Иначе они не масштабируются");
            pko.Keywords.Add("Yes", "Да", "Да");
            pko.Keywords.Add("No", "Нет", "Нет");
            pko.Keywords.Default = "Yes";
            pko.AllowNone = true;

            PromptResult pr = ed.GetKeywords(pko);
            if (pr.Status != PromptStatus.OK)
            {
                ed.WriteMessage("\nОтменено.\n");
                explode = false;
                return false;
            }
            explode = (pr.StringResult == "Yes");
            return true;
        }

        // ------------------------------------------------------------ обработка

        private static void Rescale(Document doc, Database db, Editor ed, ObjectId[] ids,
                                    double divisor, bool wholeSpace, int unlockedLayers,
                                    bool explodeProxies, Point3d? selectionBase,
                                    Point3d? fixedBase = null)
        {
            double factor = 1.0 / divisor;

            // Весь чертёж масштабируется вокруг 0,0,0 — так он совмещается с
            // облаком точек и с другими чертежами. Выбранная рамкой часть — вокруг
            // своего центра: иначе фрагмент, лежащий на координатах 945 000,
            // после увеличения в 1000 раз улетал на 945 000 000 — далеко от
            // остального чертежа.
            // «Отрезок» задаёт базовую точку сам — первую точку опорного отрезка.
            Point3d basePoint = fixedBase ?? (wholeSpace ? Point3d.Origin
                              : selectionBase ?? SelectionCenter(db, ids));
            Matrix3d mat = Matrix3d.Scaling(factor, basePoint);

            var st = new Stats();
            st.PerEntityLinetype = !wholeSpace;
            if (wholeSpace && db.CurrentSpaceId == SymbolUtilityServices.GetBlockModelSpaceId(db))
                st.AnnoScales = AnnoRescale.AllScales(db);

            // Видовые экраны смотрят в модель — пересчитывать их есть смысл,
            // только когда сжимается именно модель. Снимок «что видно в каком
            // экране» нужен до сжатия, пока выбранное ещё на своём месте.
            if (RescaleSettings.ScaleViewports &&
                db.CurrentSpaceId == SymbolUtilityServices.GetBlockModelSpaceId(db))
                st.Viewports = ViewportRescale.Prepare(db, ids, wholeSpace);

            // Стили текста, которыми пользуются обрабатываемые объекты. Высота в
            // стиле — единственное место, где живёт размер шрифта у размеров с
            // «фиксированной высотой»; собираем на ходу, правим после обхода.
            var usedStyles = new HashSet<ObjectId>();

            using (DocumentLock docLock = doc.LockDocument())
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                // Обломки расчленённых прокси кладём в то же пространство.
                var space = (BlockTableRecord)tr.GetObject(db.CurrentSpaceId, OpenMode.ForWrite);

                // Порядок прорисовки — до расчленения прокси: их обломки потом
                // встанут на место исходника, а не поверх всего чертежа.
                ObjectIdCollection orderBefore = null;
                if (explodeProxies)
                {
                    try
                    {
                        var dot = (DrawOrderTable)tr.GetObject(space.DrawOrderTableId, OpenMode.ForRead);
                        orderBefore = dot.GetFullDrawOrder(0);
                    }
                    catch (System.Exception) { }
                }

                var pm = new ProgressMeter();
                pm.SetLimit(ids.Length);
                pm.Start(Lang.T("Масштабирование", "Rescaling"));
                try
                {
                    foreach (ObjectId id in ids)
                    {
                        pm.MeterProgress();

                        // forceOpenOnLockedLayer=true — на случай слоёв, которые
                        // мы не смогли разблокировать (например, из xref).
                        Entity ent;
                        try
                        {
                            ent = tr.GetObject(id, OpenMode.ForWrite, false, true) as Entity;
                        }
                        catch (AcRxException)
                        {
                            Count(st.Failures, "не открылся");
                            continue;
                        }
                        if (ent == null)
                            continue;

                        // Прокси TransformBy проглатывает молча — без ошибки и без
                        // эффекта, объект остаётся в прежнем масштабе. Единственный
                        // путь — расчленить его на обычные примитивы.
                        if (ent is ProxyEntity)
                        {
                            if (!explodeProxies)
                            {
                                st.ProxyLeft++;
                                continue;
                            }
                            // Прокси, который можно копировать и удалять, не
                            // разбираем, а переносим в блок целиком (WrapProxies):
                            // так его графика сохраняется точь-в-точь.
                            if (CanWrapProxy((ProxyEntity)ent))
                            {
                                st.ProxyWrapIds.Add(id);
                                st.Done++;
                                continue;
                            }
                            if (ExplodeProxy(tr, space, ent, mat, divisor, factor, st))
                                st.Done++;
                            else
                                st.ProxyFailed++;
                            continue;
                        }

                        // OLE-вставка (таблица Excel, кусок Word, картинка через
                        // «Специальную вставку») — тоже не геометрия: TransformBy
                        // её рамку не масштабирует. Рамку переписываем вручную.
                        var ole = ent as Ole2Frame;
                        if (ole != null)
                        {
                            if (ScaleOle(ole, mat, factor))
                            {
                                st.Ole++;
                                st.Done++;
                            }
                            else
                            {
                                st.OleFailed++;
                                Count(st.Failures, "OLE-вставка: рамка не поддалась");
                            }
                            continue;
                        }

                        CollectTextStyles(tr, ent, usedStyles, false);

                        var assocDim = ent as Dimension;
                        if (assocDim != null && Disassociate(tr, assocDim))
                            st.DimDisassociated++;

                        string typeName = SafeTypeName(ent);
                        try
                        {
                            ApplyToEntity(ent, mat, divisor, factor, st);
                            st.Done++;
                        }
                        catch (AcRxException ex)
                        {
                            Count(st.Failures, typeName + ": " + ex.ErrorStatus);
                        }
                        catch (System.Exception)
                        {
                            Count(st.Failures, typeName);
                        }
                    }
                }
                finally
                {
                    pm.Stop();
                }

                WrapProxies(tr, db, space, orderBefore, mat, st);
                foreach (ObjectId pid in st.ProxyWrapFallback)
                {
                    var px = tr.GetObject(pid, OpenMode.ForWrite, false, true) as Entity;
                    if (px == null || px.IsErased) continue;
                    st.Done--;
                    if (ExplodeProxy(tr, space, px, mat, divisor, factor, st)) st.Done++; else st.ProxyFailed++;
                }
                RestoreProxyDrawOrder(tr, space, orderBefore, st);
                ScaleTextStyles(tr, db, ids, usedStyles, divisor, factor, st);

                // Аннотативные объекты модели: масштабы аннотаций x(1/factor).
                if (wholeSpace && db.CurrentSpaceId == SymbolUtilityServices.GetBlockModelSpaceId(db))
                {
                    st.Anno = new AnnoRescale(db, factor);
                    st.Anno.ApplyToModel(tr, ids);
                }
                FinishDimensions(tr, st);

                tr.Commit();

                // Отдельной транзакцией и строго после фиксации первой: правится
                // только блок, сам размер не открывается на запись — иначе при
                // закрытии он снова пересобрал бы блок и затёр восстановленное.
                RestoreBakedDimensions(db, mat, st);

                // Размеры Revit — в обычные размеры AutoCAD, нарисованные под
                // картинку Revit, которую только что вернули: свой безымянный
                // блок, и дальше их можно двигать, копировать и вставлять в
                // другие чертежи, как нарисованные руками.
                // ZOOMZOOM_NO_NATIVE — только для проверки: оставить картинку
                // Revit, чтобы сравнить её с переведённой.
                if (Environment.GetEnvironmentVariable("ZOOMZOOM_NO_NATIVE") == null)
                {
                    try { st.DimNative = DimNative.ConvertAll(db, st.DimBaked.Keys); }
                    catch (System.Exception) { }
                }

                if (st.Viewports != null)
                    st.Viewports.Apply(db, mat, factor, st.Anno);

                // Рамка: штрих выбранного держит масштаб типа линий самих
                // объектов (x factor, см. ApplyToEntity) — верно для модели. Но
                // если выбранное показано в экранах листов с PSLTSCALE=1, там
                // штрих = LTSCALE x масштаб объекта в единицах листа и вырос бы
                // в factor раз (пунктир свай — сплошной). Листы важнее: масштаб
                // типа линий возвращаем, на листах всё как было — как и при
                // «весь чертёж».
                if (!wholeSpace && st.Viewports != null && st.Viewports.PaperLinetypeLayouts.Count > 0)
                    st.LinetypeKept = KeepEntityLinetypeScale(db, ids, st.Added, factor);
            }

            // Системные переменные общие на весь чертёж. При выборе рамкой
            // большая часть чертежа остаётся в прежнем масштабе, и менять их
            // нельзя: после «рамка x1000, затем весь чертёж /1000» LTSCALE
            // вернулся бы к исходному вместо поделённого. Штрихи выбранного
            // держит LinetypeScale самих объектов (см. ApplyToEntity).
            double oldLts = 0, newLts = 0, oldPd = 0, newPd = 0, oldTs = 0, newTs = 0;
            bool pdChanged = false, tsChanged = false;
            if (wholeSpace)
            {
                // Штрих в модели = LTSCALE x шаблон / масштаб аннотаций модели
                // (при MSLTSCALE=1), а в экранах листов с PSLTSCALE=1 — LTSCALE x
                // шаблон в единицах листа. Масштаб аннотаций модели уже стал в
                // 1/factor раз крупнее (AnnoRescale) — при MSLTSCALE=1 штрихи
                // модели уменьшились сами, LTSCALE не трогаем, и листы как были.
                // При MSLTSCALE=0 включаем его, если масштаб был 1:1 и есть листы
                // с PSLTSCALE=1. Иначе — по-старому, через LTSCALE.
                // Но если все пересчитанные экраны стоят на листах с PSLTSCALE=0,
                // штрих в них = LTSCALE в единицах МОДЕЛИ, и масштаб аннотаций на
                // него не действует: оси в экранах становились сплошными (штрих
                // в 1000 раз длиннее сжатого чертежа). Тогда — только LTSCALE x
                // factor, а MSLTSCALE выключается: иначе штрих модели поделился бы
                // ещё и на увеличенный масштаб аннотаций, второй раз.
                bool onlyModelUnitSheets = st.Viewports != null &&
                    st.Viewports.ModelLinetypeLayouts.Count > 0 && st.Viewports.PaperLinetypeLayouts.Count == 0;
                if (st.Anno != null && st.Anno.CannoChange != null)
                {
                    bool msl = false;
                    try { msl = Convert.ToInt32(AcadApp.GetSystemVariable("MSLTSCALE"), CultureInfo.InvariantCulture) != 0; }
                    catch (System.Exception) { }
                    if (onlyModelUnitSheets)
                    {
                        if (msl)
                        {
                            try
                            {
                                AcadApp.SetSystemVariable("MSLTSCALE", 0);
                                st.MsltscaleOff = true;
                            }
                            catch (System.Exception) { }
                        }
                    }
                    else if (msl)
                        st.ModelLinetypeScale = st.Anno.CannoChange;
                    else if (st.Viewports != null && st.Viewports.PaperLinetypeLayouts.Count > 0 &&
                             st.Viewports.ModelLinetypeLayouts.Count == 0 &&
                             st.Anno.TakeOverLinetypes(st.Anno.Objects))
                        st.ModelLinetypeScale = st.Anno.CannoChange;
                }
                if (st.ModelLinetypeScale == null)
                    AdjustLinetypeScale(factor, out oldLts, out newLts);
                pdChanged = AdjustPointSize(factor, out oldPd, out newPd);
                tsChanged = AdjustTextSize(factor, out oldTs, out newTs);
            }

            ed.Regen();
            if (st.Viewports != null && st.Viewports.Scaled > 0)
                DropLayoutCache();

            Report(ed, db, divisor, factor, basePoint, ids.Length, st, wholeSpace, unlockedLayers,
                   oldLts, newLts, pdChanged, oldPd, newPd, tsChanged, oldTs, newTs);
        }

        /// <summary>
        /// Сбросить кэш графики листов. При LAYOUTREGENCTL=2 (по умолчанию)
        /// AutoCAD держит картинку каждого открытого в сеансе листа вместе с
        /// видом его экранов и при переходе на лист её не пересобирает. Вид
        /// экрана мы поменяли в чертеже, а кэш остаётся со старым — и экран
        /// смотрит туда, где после масштабирования ничего нет: на открывавшихся
        /// листах пусто, на неоткрывавшихся (и в печати) всё верно.
        /// Переключение LAYOUTREGENCTL в 0 выбрасывает кэш; потом значение
        /// возвращается, и листы кэшируются заново уже с новым видом.
        /// </summary>
        private static void DropLayoutCache()
        {
            try
            {
                object was = AcadApp.GetSystemVariable("LAYOUTREGENCTL");
                if (Convert.ToInt32(was, CultureInfo.InvariantCulture) == 0)
                    return;
                AcadApp.SetSystemVariable("LAYOUTREGENCTL", (short)0);
                AcadApp.SetSystemVariable("LAYOUTREGENCTL", was);
            }
            catch (System.Exception) { }
        }

        /// <summary>Счётчики для итогового отчёта.</summary>
        private class Stats
        {
            public int Done, Dims, Blocks, Hatches, Annotative;
            public int AnnoScaled, AnnoViewport;
            public int ProxyExploded, ProxyPieces, ProxyFailed, ProxyLeft;

            /// <summary>Обломки, которые оказались заметно крупнее самого прокси,
            /// и бесконечные линии среди них. Расчленение — единственное место,
            /// где команда создаёт геометрию, поэтому линии, которых до операции
            /// не было, могут взяться только отсюда.</summary>
            public int ProxyInfinite;
            public readonly SortedDictionary<string, int> ProxyOversize =
                new SortedDictionary<string, int>(StringComparer.Ordinal);

            /// <summary>Почему прокси не поддался расчленению и на каком он слое.
            /// «Пусто» почти всегда значит, что у объекта нет собственной графики —
            /// такой на чертеже и не виден, и беспокоиться о нём нечего.</summary>
            public readonly SortedDictionary<string, int> ProxyFailKinds =
                new SortedDictionary<string, int>(StringComparer.Ordinal);
            public int Ole, OleFailed;
            public int Tables, TableCells;
            public int MLeaders;
            public int DimscaleZero;

            /// <summary>Размеры, у которых пришлось дотянуть опорные точки вручную,
            /// и какие именно точки это были.</summary>
            public int DimPointsFixed;
            public readonly SortedDictionary<string, int> DimFixKinds =
                new SortedDictionary<string, int>(StringComparer.Ordinal);

            /// <summary>Обработанные размеры и то, где у них должна стоять подпись,
            /// если она поставлена вручную. Пересчёт блока размера отложен на самый
            /// конец: пока высота шрифта в стиле не поделена, AutoCAD считает подпись
            /// огромной и уносит её прочь от размера — а положение с флагом «вручную»
            /// потом прилипает.</summary>
            public readonly List<ObjectId> DimIds = new List<ObjectId>();
            public readonly Dictionary<ObjectId, Point3d> DimTextTargets =
                new Dictionary<ObjectId, Point3d>();
            public int DimTextRestored;

            /// <summary>Размеры, у которых снята ассоциативность. См. Disassociate.</summary>
            public int DimDisassociated;

            /// <summary>Масштабируется выбранное, а не весь чертёж: LTSCALE не
            /// трогается, штрихи держит LinetypeScale каждого объекта.</summary>
            public bool PerEntityLinetype;

            /// <summary>Масштабы аннотаций чертежа, если масштабируется вся модель
            /// и их у её объектов поменяет AnnoRescale; иначе null. См. KeepsPaperSize.</summary>
            public List<AnnotationScale> AnnoScales;

            /// <summary>Листы только с PSLTSCALE=0: штрихи доведены LTSCALE, MSLTSCALE выключен.</summary>
            public bool MsltscaleOff;

            /// <summary>Штрихи модели доведены масштабом аннотаций, а не LTSCALE:
            /// «было -> стало» для отчёта; null — через LTSCALE.</summary>
            public string ModelLinetypeScale;

            /// <summary>Рамка: масштаб типа линий выбранных возвращён (листы с PSLTSCALE=1).</summary>
            public bool LinetypeKept;

            /// <summary>Перевод масштабов аннотаций (весь чертёж, модель); null — не было.</summary>
            public AnnoRescale Anno;

            /// <summary>Исходная картинка каждого размера — копии того, что лежало
            /// в его блоке до масштабирования. См. RestoreBakedDimensions.</summary>
            public readonly Dictionary<ObjectId, List<Entity>> DimBaked =
                new Dictionary<ObjectId, List<Entity>>();
            public int DimBakedRestored;
            /// <summary>Размеров Revit, переведённых в обычные размеры AutoCAD (DimNative).</summary>
            public int DimNative;

            /// <summary>Пересчёт видовых экранов; null — не делался (выключен
            /// в палитре или масштабировалась не модель).</summary>
            public ViewportRescale Viewports;

            /// <summary>Расчленённый прокси -> его обломки в порядке появления.</summary>
            public readonly Dictionary<ObjectId, List<ObjectId>> ProxyPieceIds =
                new Dictionary<ObjectId, List<ObjectId>>();
            public bool ProxyOrderRestored;

            /// <summary>Прокси, которые уходят в блок целиком, и итог этого.</summary>
            public readonly List<ObjectId> ProxyWrapIds = new List<ObjectId>();
            public int ProxyWrapped, ProxyWrapBlocks;
            public readonly List<ObjectId> ProxyWrapFallback = new List<ObjectId>();
            /// <summary>Обломки расчленённых прокси: они наши, хоть их и не было
            /// в исходном выборе. Нужны, чтобы обход «чужого» не принял их за
            /// посторонних и не запретил трогать их стили текста.</summary>
            public readonly List<ObjectId> Added = new List<ObjectId>();
            public readonly List<string> StylesScaled = new List<string>();
            public readonly List<string> StylesCloned = new List<string>();
            public int StylesRepointed;
            public readonly List<string> StylesShared = new List<string>();
            public readonly List<string> StylesFailed = new List<string>();
            public readonly SortedDictionary<string, int> Failures =
                new SortedDictionary<string, int>(StringComparer.Ordinal);
        }

        /// <summary>Масштабирование одного обычного (не прокси) объекта.</summary>
        private static void ApplyToEntity(Entity ent, Matrix3d mat, double divisor,
                                          double factor, Stats st)
        {
            // Размеры таблицы надо запомнить до трансформации: что из них
            // TransformBy пересчитает, а что бросит, зависит от версии AutoCAD,
            // и разобрать это можно только сравнением «до/после».
            var table = ent as Table;
            TableSnapshot before = table != null ? TableSnapshot.Take(table) : null;

            // Мультивыноска — по той же причине: высота текста и стрелки лежат на
            // самом объекте, и что из этого пересчитает TransformBy, знает только
            // сама версия AutoCAD.
            var mleader = ent as MLeader;
            MLeaderSnapshot mlBefore = mleader != null ? MLeaderSnapshot.Take(mleader, st.AnnoScales) : null;

            // У размера геометрия разложена по именованным опорным точкам, и
            // проверить, что TransformBy достал до каждой, можно только сверкой.
            var dimension = ent as Dimension;
            DimSnapshot dimBefore = dimension != null ? DimSnapshot.Take(dimension) : null;

            // Картинку размера снимаем до любых правок: и TransformBy, и смена
            // DIMSCALE могут заставить AutoCAD пересобрать блок по-своему.
            if (dimension != null && !dimension.ObjectId.IsNull &&
                dimension.Annotative != AnnotativeStates.True)
            {
                List<Entity> baked = TakeBakedGraphics(dimension);
                if (baked != null)
                    st.DimBaked[dimension.ObjectId] = baked;
            }

            if (KeepsPaperSize(ent, st.AnnoScales))
            {
                // Только перенос: точка вставки уходит туда же, куда при
                // масштабировании, величина на листе остаётся прежней.
                Point3d at = ent is MText ? ((MText)ent).Location
                           : ent is DBText ? ((DBText)ent).Position
                           : ((BlockReference)ent).Position;
                ent.TransformBy(Matrix3d.Displacement(at.GetVectorTo(at.TransformBy(mat))));
            }
            else
            {
                ent.TransformBy(mat);
            }

            // Длина штриха = LTSCALE x LinetypeScale объекта. Когда LTSCALE не
            // трогается (выбор рамкой), штрих выбранного растёт вместе с ним
            // только через собственный множитель.
            if (st.PerEntityLinetype)
            {
                try { ent.LinetypeScale = ent.LinetypeScale * factor; }
                catch (System.Exception) { }
            }

            if (dimBefore != null && dimBefore.Restore(mat, factor, st, SafeTypeName(dimension)) > 0)
                st.DimPointsFixed++;

            if (before != null)
            {
                st.TableCells += ScaleTable(table, before, factor);
                st.Tables++;
            }

            if (mlBefore != null)
            {
                ScaleMLeader(mleader, mlBefore, factor);
                st.MLeaders++;
            }

            // Высота текста, стрелки, полки и отступы задаются не геометрией,
            // а множителем DIMSCALE, которого TransformBy не касается. Без этого
            // шага оформление остаётся в прежнем размере и выглядит гигантским
            // на сжатом чертеже.
            switch (ScaleAnnotationSize(ent, factor, st))
            {
                case 1: st.AnnoScaled++; break;
                case -1: st.AnnoViewport++; break;
            }

            var dim = ent as Dimension;
            if (dim != null)
            {
                // Геометрия сжалась в 1000 раз -> Measurement стал 6.
                // Линейный масштаб 1000 возвращает подпись к 6000.
                double cur = dim.Dimlfac;
                if (Math.Abs(cur) < 1e-12)
                    cur = 1.0;
                dim.Dimlfac = cur * divisor;
                st.Dims++;

                // Пересчёт блока — в самом конце, когда стили уже поделены.
                st.DimIds.Add(dim.ObjectId);
                if (dimBefore != null)
                {
                    Point3d textTarget;
                    if (dimBefore.TextTarget(mat, out textTarget))
                        st.DimTextTargets[dim.ObjectId] = textTarget;
                }
            }

            if (ent is BlockReference) st.Blocks++;
            if (ent is Hatch) st.Hatches++;
            if (ent.Annotative == AnnotativeStates.True) st.Annotative++;
        }

        /// <summary>
        /// Аннотативный текст и аннотативный блок: величина на листе (высота
        /// текста, масштаб блока) хранится в самом объекте, а в модели он виден
        /// в «величина на листе / масштаб аннотаций». При Зум-зуме всей модели
        /// масштаб аннотаций и так меняется в 1/factor раз (AnnoRescale), и новая
        /// запись масштаба считается от величины на листе. TransformBy уменьшает
        /// и её — выходило дважды, x factor²: надпись высотой 50 становилась
        /// 0.00005 вместо 0.05, блок 1:100 — 0.0001 вместо 0.1. Многострочный
        /// текст и блок — всегда, однострочный — когда у него нет текущего
        /// масштаба аннотаций чертежа.
        ///
        /// Аннотативные без единого масштаба (так выгружает Revit) ApplyToModel
        /// не трогает — их масштабирует обычный TransformBy.
        /// Мультивыноску и размеры не трогаем: у них, кроме точки вставки, есть
        /// своя геометрия (стрелки, выносные), и её надо именно масштабировать.
        /// Ограничение: если у объекта несколько масштабов и их записи сдвинуты
        /// друг относительно друга вручную, сдвиг остаётся прежним в единицах модели.
        /// </summary>
        private static bool KeepsPaperSize(Entity ent, List<AnnotationScale> scales)
        {
            if (scales == null || (!(ent is MText) && !(ent is DBText) && !(ent is BlockReference)))
                return false;
            try
            {
                return ent.Annotative == AnnotativeStates.True && AnnoRescale.HasAnyScale(ent, scales);
            }
            catch (System.Exception) { return false; }
        }

        /// <summary>
        /// Центр рамки (или нескольких рамок), которой выбирали объекты, —
        /// базовая точка для выбора рамкой: что было в рамке, в ней и остаётся.
        /// Габарит объектов тут хуже: один блок, у которого вставка в рамке,
        /// а графика на другом конце чертежа, уводит его центр далеко в сторону.
        /// null — хоть что-то выбрано не рамкой (щелчком, линией, заранее);
        /// тогда считаем по габариту, см. SelectionCenter.
        /// </summary>
        private static Point3d? WindowCenter(SelectionSet ss)
        {
            bool any = false;
            var ext = new Extents3d();
            try
            {
                foreach (SelectedObject so in ss)
                {
                    var w = so as CrossingOrWindowSelectedObject;
                    if (w == null)
                        return null;
                    foreach (PickPointDescriptor pd in w.GetPickPoints())
                    {
                        Point3d p = pd.PointOnLine;
                        if (any) ext.AddPoint(p); else { ext = new Extents3d(p, p); any = true; }
                    }
                }
            }
            catch (System.Exception) { return null; }
            if (!any)
                return null;
            return ext.MinPoint + (ext.MaxPoint - ext.MinPoint) / 2.0;
        }

        /// <summary>
        /// Рамка: если выбранное видно в видовых экранах листов вместе с чем-то
        /// ещё, такой экран при масштабировании только выбранного сохранить
        /// нельзя. Говорим, что ещё там видно, и спрашиваем: только выбранное
        /// или целиком — со всем, что видно в этих экранах (по умолчанию —
        /// целиком, тогда листы остаются как были). null — отменено.
        /// </summary>
        private static ObjectId[] AskMixedViewports(Editor ed, Database db, ObjectId[] ids)
        {
            if (!RescaleSettings.ScaleViewports ||
                db.CurrentSpaceId != SymbolUtilityServices.GetBlockModelSpaceId(db))
                return ids;

            ViewportRescale.MixedInfo info;
            try { info = ViewportRescale.Prepare(db, ids, false).FindMixed(db); }
            catch (System.Exception) { return ids; }
            if (info.Clean)
                return ids;

            ed.WriteMessage("\nВместе с выбранным в видовых экранах листов видно ещё:");
            foreach (string line in info.Report)
                ed.WriteMessage("\n" + line);
            ed.WriteMessage("\n\nВыбранное — масштабировать только выбранное ({0}). Экраны, где видно и другое ({1}), " +
                            "останутся как были, и выбранное в них станет другого размера.", ids.Length, info.ViewsWhole.Count);
            ed.WriteMessage("\nВсё в видовом — вместе со всем, что видно в этих экранах: ещё {0}{1}. " +
                            "Экраны пересчитаются, на листах всё останется как было.",
                            info.Extra.Count,
                            info.ExtraHidden > 0 ? " (из них скрыто слоями " + info.ExtraHidden + ")" : "");

            var options = new PromptKeywordOptions("\nМасштабировать [Выбранное/Всё в видовом] <Всё в видовом>: ")
            {
                AllowNone = true,
                AppendKeywordsToMessage = false
            };
            options.Keywords.Add("Selected", "Выбранное", "Выбранное");
            options.Keywords.Add("Whole", "Всё", "Всё в видовом");   // в ключевом слове пробелов быть не может: набирать «Вс»
            options.Keywords.Default = "Whole";
            PromptResult answer = ed.GetKeywords(options);
            if (answer.Status == PromptStatus.Cancel || answer.Status == PromptStatus.Error)
            {
                ed.WriteMessage("\nОтменено.\n");
                return null;
            }
            if (answer.Status == PromptStatus.OK && answer.StringResult == "Selected")
                return ids;

            var all = new List<ObjectId>(ids);
            var have = new HashSet<ObjectId>(ids);
            foreach (ObjectId id in info.Extra)
                if (have.Add(id))
                    all.Add(id);
            return all.ToArray();
        }

        /// <summary>Вернуть масштаб типа линий выбранных (и обломков прокси) к исходному.</summary>
        private static bool KeepEntityLinetypeScale(Database db, ObjectId[] ids, List<ObjectId> added, double factor)
        {
            try
            {
                using (Transaction tr = db.TransactionManager.StartTransaction())
                {
                    var all = new List<ObjectId>(ids);
                    all.AddRange(added);
                    foreach (ObjectId id in all)
                    {
                        Entity ent;
                        try { ent = tr.GetObject(id, OpenMode.ForWrite, false, true) as Entity; }
                        catch (System.Exception) { continue; }
                        if (ent == null || ent.IsErased)
                            continue;
                        try { ent.LinetypeScale = ent.LinetypeScale / factor; }
                        catch (System.Exception) { }
                    }
                    tr.Commit();
                }
                return true;
            }
            catch (System.Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// Рамка AutoCAD не выбирает объекты на выключенных и замороженных
        /// слоях — а «весь чертёж» их масштабирует, и после рамки они остались
        /// бы в старом масштабе. Добираем их по тем же рамкам: обычная рамка —
        /// объект целиком внутри, секущая — пересекает (по габариту). Выбор не
        /// рамкой (щелчком) — ничего не добавляем.
        /// </summary>
        private static ObjectId[] AddHiddenInWindows(Database db, SelectionSet ss, ObjectId[] ids, Matrix3d ucs)
        {
            // Рамка рисуется в осях ПСК пользователя: при повёрнутой ПСК в мировых
            // осях она — повёрнутый прямоугольник. Сравниваем всё в осях ПСК.
            Matrix3d toUcs = ucs.Inverse();
            var windows = new List<KeyValuePair<Extents3d, bool>>();      // габарит рамки (в ПСК), секущая
            var seen = new HashSet<string>();
            try
            {
                foreach (SelectedObject so in ss)
                {
                    var w = so as CrossingOrWindowSelectedObject;
                    if (w == null)
                        continue;
                    bool any = false;
                    var ext = new Extents3d();
                    foreach (PickPointDescriptor pd in w.GetPickPoints())
                    {
                        Point3d p = pd.PointOnLine.TransformBy(toUcs);
                        p = new Point3d(p.X, p.Y, 0);
                        if (any) ext.AddPoint(p); else { ext = new Extents3d(p, p); any = true; }
                    }
                    if (!any)
                        continue;
                    bool crossing = so.SelectionMethod == SelectionMethod.Crossing;
                    string key = ext.MinPoint + "|" + ext.MaxPoint + "|" + crossing;
                    if (seen.Add(key))
                        windows.Add(new KeyValuePair<Extents3d, bool>(ext, crossing));
                }
            }
            catch (System.Exception) { return ids; }
            if (windows.Count == 0)
                return ids;

            var result = new List<ObjectId>(ids);
            var have = new HashSet<ObjectId>(ids);
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                var hidden = new HashSet<ObjectId>();
                var layers = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
                foreach (ObjectId lid in layers)
                {
                    var ltr = (LayerTableRecord)tr.GetObject(lid, OpenMode.ForRead);
                    if (ltr.IsOff || ltr.IsFrozen)
                        hidden.Add(lid);
                }
                if (hidden.Count > 0)
                {
                    var space = (BlockTableRecord)tr.GetObject(db.CurrentSpaceId, OpenMode.ForRead);
                    foreach (ObjectId id in space)
                    {
                        if (have.Contains(id))
                            continue;
                        var ent = tr.GetObject(id, OpenMode.ForRead, false, true) as Entity;
                        if (ent == null || !hidden.Contains(ent.LayerId))
                            continue;
                        Extents3d e;
                        try { e = InUcs(ent.GeometricExtents, toUcs); }
                        catch (System.Exception) { continue; }
                        foreach (var w in windows)
                        {
                            Extents3d r = w.Key;
                            bool inside = e.MinPoint.X >= r.MinPoint.X && e.MaxPoint.X <= r.MaxPoint.X &&
                                          e.MinPoint.Y >= r.MinPoint.Y && e.MaxPoint.Y <= r.MaxPoint.Y;
                            bool touches = e.MinPoint.X <= r.MaxPoint.X && e.MaxPoint.X >= r.MinPoint.X &&
                                           e.MinPoint.Y <= r.MaxPoint.Y && e.MaxPoint.Y >= r.MinPoint.Y;
                            if (w.Value ? touches : inside)
                            {
                                result.Add(id);
                                have.Add(id);
                                break;
                            }
                        }
                    }
                }
                tr.Commit();
            }
            return result.ToArray();
        }

        /// <summary>Габарит объекта в осях ПСК (по восьми углам мирового габарита).</summary>
        private static Extents3d InUcs(Extents3d w, Matrix3d toUcs)
        {
            var e = new Extents3d();
            bool any = false;
            for (int i = 0; i < 8; i++)
            {
                var p = new Point3d((i & 1) == 0 ? w.MinPoint.X : w.MaxPoint.X,
                                    (i & 2) == 0 ? w.MinPoint.Y : w.MaxPoint.Y,
                                    (i & 4) == 0 ? w.MinPoint.Z : w.MaxPoint.Z).TransformBy(toUcs);
                p = new Point3d(p.X, p.Y, 0);
                if (any) e.AddPoint(p); else { e = new Extents3d(p, p); any = true; }
            }
            return e;
        }

        /// <summary>
        /// Центр общего габарита выбранного — базовая точка для выбора рамкой.
        /// Бесконечные линии в габарит не входят: у них его нет. Если габарита
        /// не нашлось ни у одного объекта — 0,0,0, как у всего чертежа.
        /// </summary>
        private static Point3d SelectionCenter(Database db, ObjectId[] ids)
        {
            bool any = false;
            var ext = new Extents3d();
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                foreach (ObjectId id in ids)
                {
                    try
                    {
                        var ent = tr.GetObject(id, OpenMode.ForRead, false, true) as Entity;
                        if (ent == null || ent is Xline || ent is Ray)
                            continue;
                        Extents3d e = ent.GeometricExtents;
                        if (any) ext.AddExtents(e); else { ext = e; any = true; }
                    }
                    catch (System.Exception) { }      // пустой объект, габаритов нет
                }
                tr.Commit();
            }
            if (!any)
                return Point3d.Origin;
            return ext.MinPoint + (ext.MaxPoint - ext.MinPoint) / 2.0;
        }

        /// <summary>
        /// Снимает с размера ассоциативность (как _DIMDISASSOCIATE): удаляет его
        /// объект связи с геометрией ACAD_DIMASSOC. true — связь была и снята.
        ///
        /// Связанный размер AutoCAD пересчитывает по геометрии сам, уже после
        /// конца команды — когда и размер, и геометрия отмасштабированы, а
        /// подпись поставлена на место. На этом чертеже такой пересчёт часть
        /// размеров отцеплял («Выносная линия откреплена»), а у размера с
        /// перенесённой вручную подписью уносил её вдвое дальше от начала
        /// координат — с длинной полкой через всё поле. Без связи размер
        /// остаётся ровно таким, каким его оставила команда.
        /// </summary>
        private static bool Disassociate(Transaction tr, Dimension dim)
        {
            try
            {
                ObjectId xd = dim.ExtensionDictionary;
                if (xd.IsNull || xd.IsErased)
                    return false;
                var dict = tr.GetObject(xd, OpenMode.ForRead) as DBDictionary;
                if (dict == null || !dict.Contains("ACAD_DIMASSOC"))
                    return false;

                ObjectId assocId = dict.GetAt("ACAD_DIMASSOC");
                dict.UpgradeOpen();
                dict.Remove(assocId);

                // Ссылки на связь из списков реакторов размера и геометрии
                // остаются, но удалённому объекту AutoCAD уведомлений не шлёт.
                DBObject assoc = tr.GetObject(assocId, OpenMode.ForWrite, false, true);
                if (!assoc.IsErased)
                    assoc.Erase();
                return true;
            }
            catch (System.Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// Запоминает, почему прокси не разобрался и где он лежит. Слой тут важнее
        /// причины: если все отказы на одном непечатном слое или графики у них нет
        /// вовсе, беспокоиться не о чем, а вот отказ на рабочем слое надо смотреть.
        /// </summary>
        private static void NoteProxyFail(Entity proxy, string reason, Stats st)
        {
            Count(st.ProxyFailKinds, reason + ", слой " + SafeLayer(proxy)
                  + (Diagonal(proxy) > 0 ? "" : ", габаритов нет"));
        }

        /// <summary>
        /// Диагональ габаритного прямоугольника объекта, или 0, если габаритов
        /// у него нет. У бесконечных линий (XLINE, RAY) их нет по определению.
        /// </summary>
        private static double Diagonal(Entity ent)
        {
            try
            {
                Extents3d ext = ent.GeometricExtents;
                return (ext.MaxPoint - ext.MinPoint).Length;
            }
            catch (System.Exception) { return 0; }
        }

        /// <summary>
        /// Запоминает обломок, который вышел заметно крупнее расчленённого прокси.
        ///
        /// Прокси рисуется закэшированной графикой, а Explode отдаёт настоящую
        /// геометрию — и она бывает куда больше того, что было видно на экране.
        /// Так на чертеже и появляются линии через всё поле, которых до операции
        /// не было: масштабирование тут ни при чём, объект таким родился.
        /// Отдельно считаем XLINE и RAY: они бесконечные, рисуются через весь
        /// экран при любом зуме, и уменьшить их нельзя в принципе.
        /// </summary>
        private static void NoteIfOversize(Entity piece, double sourceSize, Stats st)
        {
            try
            {
                if (piece is Xline || piece is Ray)
                {
                    st.ProxyInfinite++;
                    Count(st.ProxyOversize,
                          "бесконечная " + SafeTypeName(piece) + ", слой " + SafeLayer(piece));
                    return;
                }

                if (sourceSize <= 0)
                    return;

                double size = Diagonal(piece);
                if (size > sourceSize * 2.0)
                    Count(st.ProxyOversize,
                          SafeTypeName(piece) + ", слой " + SafeLayer(piece)
                          + " — крупнее прокси в " + Num(size / sourceSize) + " раз");
            }
            catch (System.Exception) { }
        }

        private static string SafeLayer(Entity ent)
        {
            try { return ent.Layer; }
            catch (System.Exception) { return "?"; }
        }

        /// <summary>
        /// Пересчёт блоков размеров — последним делом, когда поделены и DIMSCALE,
        /// и фиксированная высота в стилях текста.
        ///
        /// Порядок тут решает всё. Пока высота шрифта в стиле осталась прежней,
        /// подпись у сжатого в 1000 раз размера оказывается в сотни раз крупнее
        /// его самого. AutoCAD её, разумеется, не вписывает и уносит наружу —
        /// на твоём чертеже подпись улетала на 2.7 единицы от размера длиной
        /// 0.0064, и полка к ней шла через весь лист. Хуже того, у подписи,
        /// поставленной вручную, новое положение прилипает: замена стиля потом
        /// уже ничего не возвращает.
        ///
        /// Поэтому пересчёт откладывается до конца, а положение подписи, если оно
        /// задано вручную, после пересчёта возвращается на своё — поделённое —
        /// место. Автоматическую подпись не трогаем: её AutoCAD расставляет сам,
        /// и теперь уже правильно.
        /// </summary>
        private static void FinishDimensions(Transaction tr, Stats st)
        {
            foreach (ObjectId id in st.DimIds)
            {
                Dimension dim;
                try { dim = tr.GetObject(id, OpenMode.ForWrite, false, true) as Dimension; }
                catch (AcRxException) { continue; }
                if (dim == null)
                    continue;

                try { dim.RecomputeDimensionBlock(true); }
                catch (System.Exception) { }

                Point3d target;
                if (!st.DimTextTargets.TryGetValue(id, out target))
                    continue;

                try
                {
                    if (dim.UsingDefaultTextPosition)
                        continue;
                    if (Near(dim.TextPosition, target))
                        continue;
                    dim.TextPosition = target;
                    st.DimTextRestored++;
                }
                catch (System.Exception) { }
            }
        }

        /// <summary>
        /// Копия того, что нарисовано в блоке размера, — в памяти, вне чертежа.
        ///
        /// Размер показывается через свой блок, и в чужих чертежах (экспорт Revit
        /// и подобное) этот блок нарисован НЕ по правилам AutoCAD: например,
        /// к перенесённой вручную подписи идёт наклонная полка, подчёркивающая
        /// число. RecomputeDimensionBlock такую картинку выбрасывает и рисует
        /// свою — при правильных числах размер выглядит иначе. Поэтому исходную
        /// картинку снимаем заранее, а в конце возвращаем, уменьшив той же
        /// матрицей. Null — снимать нечего или не вышло; тогда остаётся то,
        /// что нарисовал AutoCAD.
        /// </summary>
        private static List<Entity> TakeBakedGraphics(Dimension dim)
        {
            ObjectId blockId;
            try { blockId = dim.DimBlockId; }
            catch (System.Exception) { return null; }
            if (blockId.IsNull || !blockId.IsValid || blockId.IsErased)
                return null;

            var copies = new List<Entity>();
            try
            {
                var btr = (BlockTableRecord)blockId.GetObject(OpenMode.ForRead);
                foreach (ObjectId id in btr)
                {
                    var e = id.GetObject(OpenMode.ForRead) as Entity;
                    if (e != null)
                        copies.Add((Entity)e.Clone());
                }
            }
            catch (System.Exception)
            {
                foreach (Entity e in copies) e.Dispose();
                return null;
            }

            if (copies.Count == 0)
                return null;
            return copies;
        }

        /// <summary>
        /// Возврат исходной картинки размеров: содержимое текущего блока каждого
        /// размера заменяется снятыми заранее копиями, уменьшенными матрицей.
        ///
        /// Каждый размер — во вложенной транзакции: если на каком-то что-то
        /// сорвётся, откатывается только он, и у него остаётся картинка от
        /// AutoCAD, а не полупустой блок.
        ///
        /// Сразу после этого размеры Revit переводятся в обычные размеры AutoCAD
        /// (DimNative), которые AutoCAD перерисовывает в тот же вид.
        /// </summary>
        private static void RestoreBakedDimensions(Database db, Matrix3d mat, Stats st)
        {
            if (st.DimBaked.Count == 0)
                return;

            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                foreach (KeyValuePair<ObjectId, List<Entity>> pair in st.DimBaked)
                {
                    List<Entity> baked = pair.Value;
                    bool done = false;

                    using (Transaction one = db.TransactionManager.StartTransaction())
                    {
                        try
                        {
                            var dim = one.GetObject(pair.Key, OpenMode.ForRead, false) as Dimension;
                            ObjectId blockId = dim != null ? dim.DimBlockId : ObjectId.Null;
                            if (!blockId.IsNull)
                            {
                                var btr = (BlockTableRecord)one.GetObject(blockId, OpenMode.ForWrite);
                                var old = new List<ObjectId>();
                                foreach (ObjectId id in btr) old.Add(id);
                                foreach (ObjectId id in old)
                                    one.GetObject(id, OpenMode.ForWrite).Erase();

                                foreach (Entity e in baked)
                                {
                                    e.TransformBy(mat);
                                    btr.AppendEntity(e);
                                    one.AddNewlyCreatedDBObject(e, true);
                                }

                                one.Commit();
                                done = true;
                                st.DimBakedRestored++;
                            }
                        }
                        catch (System.Exception)
                        {
                            Count(st.Failures, "размер: исходная картинка не вернулась, оставлена картинка AutoCAD");
                        }
                    }

                    // Не попавшие в чертёж копии — наши, освобождаем сами. Попавшие
                    // при откате вложенной транзакции AutoCAD уберёт сам.
                    if (!done)
                        foreach (Entity e in baked)
                            if (!e.IsDisposed && e.ObjectId.IsNull)
                                e.Dispose();
                }
                tr.Commit();
            }
        }

        // ------------------------------------------------- опорные точки размеров

        /// <summary>
        /// Снимок опорных точек размера до трансформации и доводка после неё.
        ///
        /// Размер держит геометрию не в общей форме, а в наборе именованных точек:
        /// начала выносных линий, точка размерной линии, положение подписи. Длина
        /// выносной линии из них и складывается — расстояние от точки привязки до
        /// размерной линии плюс вылет DIMEXE x DIMSCALE. Вылет мы делим вместе
        /// с DIMSCALE, а вот если какая-то из точек после TransformBy осталась
        /// в старых координатах, выносная линия тянется от ужатого чертежа к ней
        /// и перечёркивает всё поле — при том что и подпись, и число выглядят
        /// правильными.
        ///
        /// Поэтому не полагаемся на то, что TransformBy достал до каждой точки:
        /// цели считаются от снимка «до», а трогается только то, что до цели не
        /// дошло. Если размер отмасштабировался штатно — не делается ничего.
        /// Тот же приём уже держит рамки OLE, ячейки таблиц и мультивыноски.
        /// </summary>
        private class DimSnapshot
        {
            private class PointSlot
            {
                public string Name;
                public Func<Point3d> Get;
                public Action<Point3d> Set;
                public Point3d Before;
            }

            private class ScalarSlot
            {
                public string Name;
                public Func<double> Get;
                public Action<double> Set;
                public double Before;
            }

            private readonly List<PointSlot> points = new List<PointSlot>();
            private readonly List<ScalarSlot> scalars = new List<ScalarSlot>();

            /// <summary>Точка в список — вместе со значением на момент снимка.</summary>
            private void P(string name, Func<Point3d> get, Action<Point3d> set)
            {
                try { points.Add(new PointSlot { Name = name, Get = get, Set = set, Before = get() }); }
                catch (System.Exception) { }
            }

            private void S(string name, Func<double> get, Action<double> set)
            {
                try { scalars.Add(new ScalarSlot { Name = name, Get = get, Set = set, Before = get() }); }
                catch (System.Exception) { }
            }

            public static DimSnapshot Take(Dimension dim)
            {
                var s = new DimSnapshot();

                // Положение подписи берём только когда оно задано вручную:
                // у размера с текстом «по умолчанию» запись в TextPosition
                // приколотила бы подпись на место и отключила автоматическую
                // расстановку — то есть починила бы то, что не ломалось.
                bool defaultText = true;
                try { defaultText = dim.UsingDefaultTextPosition; }
                catch (System.Exception) { }
                if (!defaultText)
                    s.P("TextPosition", () => dim.TextPosition, v => dim.TextPosition = v);

                s.S("Elevation", () => dim.Elevation, v => dim.Elevation = v);

                var rotated = dim as RotatedDimension;
                if (rotated != null)
                {
                    s.P("XLine1Point", () => rotated.XLine1Point, v => rotated.XLine1Point = v);
                    s.P("XLine2Point", () => rotated.XLine2Point, v => rotated.XLine2Point = v);
                    s.P("DimLinePoint", () => rotated.DimLinePoint, v => rotated.DimLinePoint = v);
                    return s;
                }

                var aligned = dim as AlignedDimension;
                if (aligned != null)
                {
                    s.P("XLine1Point", () => aligned.XLine1Point, v => aligned.XLine1Point = v);
                    s.P("XLine2Point", () => aligned.XLine2Point, v => aligned.XLine2Point = v);
                    s.P("DimLinePoint", () => aligned.DimLinePoint, v => aligned.DimLinePoint = v);
                    return s;
                }

                // Угловой по двум прямым: четыре конца прямых плюс точка дуги.
                var angular2 = dim as LineAngularDimension2;
                if (angular2 != null)
                {
                    s.P("XLine1Start", () => angular2.XLine1Start, v => angular2.XLine1Start = v);
                    s.P("XLine1End", () => angular2.XLine1End, v => angular2.XLine1End = v);
                    s.P("XLine2Start", () => angular2.XLine2Start, v => angular2.XLine2Start = v);
                    s.P("XLine2End", () => angular2.XLine2End, v => angular2.XLine2End = v);
                    s.P("ArcPoint", () => angular2.ArcPoint, v => angular2.ArcPoint = v);
                    return s;
                }

                var angular3 = dim as Point3AngularDimension;
                if (angular3 != null)
                {
                    s.P("CenterPoint", () => angular3.CenterPoint, v => angular3.CenterPoint = v);
                    s.P("XLine1Point", () => angular3.XLine1Point, v => angular3.XLine1Point = v);
                    s.P("XLine2Point", () => angular3.XLine2Point, v => angular3.XLine2Point = v);
                    s.P("ArcPoint", () => angular3.ArcPoint, v => angular3.ArcPoint = v);
                    return s;
                }

                var radial = dim as RadialDimension;
                if (radial != null)
                {
                    s.P("Center", () => radial.Center, v => radial.Center = v);
                    s.P("ChordPoint", () => radial.ChordPoint, v => radial.ChordPoint = v);
                    s.S("LeaderLength", () => radial.LeaderLength, v => radial.LeaderLength = v);
                    return s;
                }

                var diametric = dim as DiametricDimension;
                if (diametric != null)
                {
                    s.P("ChordPoint", () => diametric.ChordPoint, v => diametric.ChordPoint = v);
                    s.P("FarChordPoint", () => diametric.FarChordPoint, v => diametric.FarChordPoint = v);
                    s.S("LeaderLength", () => diametric.LeaderLength, v => diametric.LeaderLength = v);
                    return s;
                }

                var ordinate = dim as OrdinateDimension;
                if (ordinate != null)
                {
                    s.P("Origin", () => ordinate.Origin, v => ordinate.Origin = v);
                    s.P("DefiningPoint", () => ordinate.DefiningPoint, v => ordinate.DefiningPoint = v);
                    s.P("LeaderEndPoint", () => ordinate.LeaderEndPoint, v => ordinate.LeaderEndPoint = v);
                    return s;
                }

                var arc = dim as ArcDimension;
                if (arc != null)
                {
                    s.P("CenterPoint", () => arc.CenterPoint, v => arc.CenterPoint = v);
                    s.P("XLine1Point", () => arc.XLine1Point, v => arc.XLine1Point = v);
                    s.P("XLine2Point", () => arc.XLine2Point, v => arc.XLine2Point = v);
                    s.P("ArcPoint", () => arc.ArcPoint, v => arc.ArcPoint = v);
                    return s;
                }

                return s;
            }

            /// <summary>
            /// Куда должна встать подпись, если она поставлена вручную.
            /// false — подпись стоит автоматически, и трогать её нельзя.
            /// </summary>
            public bool TextTarget(Matrix3d mat, out Point3d target)
            {
                target = Point3d.Origin;
                foreach (PointSlot p in points)
                {
                    if (p.Name != "TextPosition")
                        continue;
                    target = p.Before.TransformBy(mat);
                    return true;
                }
                return false;
            }

            /// <summary>
            /// Дотягивает точки, до которых TransformBy не добрался. Возвращает,
            /// сколько их было. Ноль — размер отмасштабировался сам, и мы к нему
            /// не притронулись.
            /// </summary>
            public int Restore(Matrix3d mat, double factor, Stats st, string typeName)
            {
                int touched = 0;

                foreach (PointSlot p in points)
                {
                    try
                    {
                        Point3d target = p.Before.TransformBy(mat);
                        if (Near(p.Get(), target))
                            continue;
                        p.Set(target);
                        Count(st.DimFixKinds, typeName + "." + p.Name);
                        touched++;
                    }
                    catch (System.Exception) { }
                }

                foreach (ScalarSlot v in scalars)
                {
                    try
                    {
                        double target = v.Before * factor;
                        if (Near(v.Get(), target))
                            continue;
                        v.Set(target);
                        Count(st.DimFixKinds, typeName + "." + v.Name);
                        touched++;
                    }
                    catch (System.Exception) { }
                }

                return touched;
            }
        }

        // -------------------------------------------------------- мультивыноски

        /// <summary>
        /// Размеры мультивыноски до трансформации — в «эффективном» виде, то есть
        /// уже умноженные на общий множитель Scale. Именно эта величина и рисуется
        /// на экране, и только её имеет смысл сравнивать до и после.
        /// </summary>
        private class MLeaderSnapshot
        {
            public double TextHeight, ArrowSize, DoglegLength, LandingGap;

            /// <summary>Аннотативная, со своими масштабами аннотаций: величины
            /// выше — на листе (как есть, без Scale), их надо сохранить.</summary>
            public bool KeepPaper;

            public static MLeaderSnapshot Take(MLeader ml, List<AnnotationScale> scales)
            {
                try
                {
                    if (scales != null && ml.Annotative == AnnotativeStates.True &&
                        AnnoRescale.HasAnyScale(ml, scales))
                    {
                        return new MLeaderSnapshot
                        {
                            KeepPaper = true,
                            TextHeight = ml.TextHeight,
                            ArrowSize = ml.ArrowSize,
                            DoglegLength = ml.DoglegLength,
                            LandingGap = ml.LandingGap
                        };
                    }
                    double k = ml.Scale;
                    return new MLeaderSnapshot
                    {
                        TextHeight = ml.TextHeight * k,
                        ArrowSize = ml.ArrowSize * k,
                        DoglegLength = ml.DoglegLength * k,
                        LandingGap = ml.LandingGap * k
                    };
                }
                catch (System.Exception) { return null; }
            }
        }

        /// <summary>
        /// Доводит размеры мультивыноски после TransformBy.
        ///
        /// В отличие от размера, у мультивыноски высота текста и стрелки лежат не
        /// в стиле, а на самом объекте: в чертеже под съёмку встречается
        /// TEXTHEIGHT = 250 при SCALEFACTOR = 1, тогда как у соседнего размера
        /// DIMTXT = 2.5 живёт в стиле, а на объекте только DIMSCALE = 100.
        /// До стиля TransformBy не достаёт — потому размеру мы и делим DIMSCALE.
        /// До мультивыноски достаёт и делит её сам. Деление Scale поверх этого
        /// уменьшало выноску не в 1000 раз, а в миллион.
        ///
        /// Поэтому Scale не трогаем вовсе, а сверяем то, что реально видно:
        /// произведение «величина x Scale». Если оно уже поделилось — не делаем
        /// ничего; если нет — подтягиваем саму величину. Так получается верно и
        /// когда TransformBy поделил величины, и когда он поделил Scale, и когда
        /// не тронул ничего: каждый параметр проверяется отдельно, и коэффициент
        /// не может лечь дважды.
        /// </summary>
        private static void ScaleMLeader(MLeader ml, MLeaderSnapshot before, double factor)
        {
            // Аннотативная со своими масштабами: высота текста, стрелка, полка и
            // отступ лежат на ней в мм листа, а в модель их переводит масштаб
            // аннотаций. TransformBy делит и их — вместе с переводом масштаба
            // (AnnoRescale, 1:100 -> 10:1) выноска ужималась x factor², текст
            // 250 -> 0.00025 вместо 0.25. Возвращаем величины листа как были.
            if (before.KeepPaper)
            {
                try { ml.TextHeight = before.TextHeight; } catch (System.Exception) { }
                try { ml.ArrowSize = before.ArrowSize; } catch (System.Exception) { }
                try { ml.DoglegLength = before.DoglegLength; } catch (System.Exception) { }
                try { ml.LandingGap = before.LandingGap; } catch (System.Exception) { }
                return;
            }

            double k;
            try { k = ml.Scale; }
            catch (System.Exception) { return; }

            // Scale <= 0 — размер тянется от масштаба аннотаций, а не от чертежа.
            if (k <= 0)
                return;

            try { ml.TextHeight = Fit(ml.TextHeight, before.TextHeight, factor, k); }
            catch (System.Exception) { }
            try { ml.ArrowSize = Fit(ml.ArrowSize, before.ArrowSize, factor, k); }
            catch (System.Exception) { }
            try { ml.DoglegLength = Fit(ml.DoglegLength, before.DoglegLength, factor, k); }
            catch (System.Exception) { }
            try { ml.LandingGap = Fit(ml.LandingGap, before.LandingGap, factor, k); }
            catch (System.Exception) { }
        }

        /// <summary>
        /// Новое значение параметра: прежнее, если видимый размер уже поделился,
        /// иначе такое, чтобы поделился. Сравнение идёт по произведению на общий
        /// множитель — по тому, что видно на экране.
        /// </summary>
        private static double Fit(double current, double effectiveBefore, double factor, double scale)
        {
            double target = effectiveBefore * factor;
            if (Near(current * scale, target))
                return current;
            return target / scale;
        }

        // ------------------------------------------------------------- таблицы

        /// <summary>
        /// Размеры таблицы до трансформации: высота текста в каждой ячейке,
        /// высоты строк, ширины колонок, поля ячеек. Нужны, чтобы после
        /// TransformBy понять, что он пересчитал сам, а что оставил как было.
        /// </summary>
        private class TableSnapshot
        {
            public int Rows, Cols;
            /// <summary>Высоты текста по ячейкам: [строка * Cols + колонка][номер
            /// содержимого]. В ячейке может лежать несколько блоков содержимого,
            /// у каждого своя высота; у пустой — null.</summary>
            public double[][] TextHeights;
            public double[] RowHeights;
            public double[] ColWidths;
            public double VMargin, HMargin;

            public static TableSnapshot Take(Table t)
            {
                try
                {
                    var s = new TableSnapshot();
                    s.Rows = t.Rows.Count;
                    s.Cols = t.Columns.Count;
                    if (s.Rows <= 0 || s.Cols <= 0)
                        return null;

                    s.TextHeights = new double[s.Rows * s.Cols][];
                    for (int r = 0; r < s.Rows; r++)
                        for (int c = 0; c < s.Cols; c++)
                        {
                            try
                            {
                                CellContentsCollection contents = t.Cells[r, c].Contents;
                                var hs = new double[contents.Count];
                                for (int i = 0; i < hs.Length; i++)
                                {
                                    try { hs[i] = contents[i].TextHeight; }
                                    catch (System.Exception) { hs[i] = 0; }
                                }
                                s.TextHeights[r * s.Cols + c] = hs;
                            }
                            catch (System.Exception) { }
                        }

                    s.RowHeights = new double[s.Rows];
                    for (int r = 0; r < s.Rows; r++)
                    {
                        try { s.RowHeights[r] = t.Rows[r].Height; }
                        catch (System.Exception) { s.RowHeights[r] = 0; }
                    }

                    s.ColWidths = new double[s.Cols];
                    for (int c = 0; c < s.Cols; c++)
                    {
                        try { s.ColWidths[c] = t.Columns[c].Width; }
                        catch (System.Exception) { s.ColWidths[c] = 0; }
                    }

                    s.VMargin = TableMargin(t, true);
                    s.HMargin = TableMargin(t, false);
                    return s;
                }
                catch (System.Exception) { return null; }
            }
        }

        // Поля ячейки — зазор между рамкой и текстом. Пара свойств на самой
        // таблице помечена устаревшей, а замена ей (Cells[r,c].HorizontalLine)
        // задаёт не то же самое: там отступ отдельной линии, а не общий отступ
        // содержимого. Прямого соответствия нет, поэтому пользуемся старой парой
        // с точечным подавлением предупреждения.
#pragma warning disable 618
        private static double TableMargin(Table t, bool vertical)
        {
            try { return vertical ? t.VerticalCellMargin : t.HorizontalCellMargin; }
            catch (System.Exception) { return 0; }
        }

        private static void SetTableMargin(Table t, bool vertical, double value)
        {
            try
            {
                if (vertical) t.VerticalCellMargin = value;
                else t.HorizontalCellMargin = value;
            }
            catch (System.Exception) { }
        }
#pragma warning restore 618

        /// <summary>
        /// Доводит таблицу после TransformBy.
        ///
        /// У AcDbTable размер живёт не в координатах: высота текста задаётся
        /// в каждой ячейке (а по умолчанию тянется из стиля таблицы), высоты
        /// строк и ширины колонок — отдельными числами. Разные версии AutoCAD
        /// пересчитывают из этого разное, поэтому каждую величину сравниваем
        /// со снимком «до» и трогаем только то, что осталось прежним. Значение
        /// берётся от исходного, а не умножается на текущее, — иначе на версии,
        /// где TransformBy справился сам, таблица ужалась бы дважды.
        ///
        /// Высота текста ставится переопределением на самой ячейке, а не правкой
        /// стиля таблицы: стиль общий на весь чертёж, ровно как размерный.
        /// Возвращает число ячеек, которым высоту пришлось доводить руками.
        /// </summary>
        private static int ScaleTable(Table t, TableSnapshot was, double factor)
        {
            int cells = 0;
            try { t.SuppressRegenerateTable(true); }
            catch (System.Exception) { }

            try
            {
                for (int r = 0; r < was.Rows; r++)
                {
                    double h = was.RowHeights[r];
                    if (h <= 0) continue;
                    try
                    {
                        if (!Near(t.Rows[r].Height, h)) continue;
                        t.Rows[r].Height = h * factor;
                    }
                    catch (System.Exception) { }
                }

                for (int c = 0; c < was.Cols; c++)
                {
                    double w = was.ColWidths[c];
                    if (w <= 0) continue;
                    try
                    {
                        if (!Near(t.Columns[c].Width, w)) continue;
                        t.Columns[c].Width = w * factor;
                    }
                    catch (System.Exception) { }
                }

                for (int r = 0; r < was.Rows; r++)
                    for (int c = 0; c < was.Cols; c++)
                    {
                        double[] hs = was.TextHeights[r * was.Cols + c];
                        if (hs == null || hs.Length == 0) continue;

                        CellContentsCollection contents;
                        try { contents = t.Cells[r, c].Contents; }
                        catch (System.Exception) { continue; }

                        for (int i = 0; i < hs.Length && i < contents.Count; i++)
                        {
                            double h = hs[i];
                            if (h <= 0) continue;
                            try
                            {
                                if (!Near(contents[i].TextHeight, h)) continue;
                                contents[i].TextHeight = h * factor;
                                cells++;
                            }
                            catch (System.Exception) { }
                        }
                    }

                if (was.VMargin > 0 && Near(TableMargin(t, true), was.VMargin))
                    SetTableMargin(t, true, was.VMargin * factor);
                if (was.HMargin > 0 && Near(TableMargin(t, false), was.HMargin))
                    SetTableMargin(t, false, was.HMargin * factor);
            }
            finally
            {
                try { t.SuppressRegenerateTable(false); } catch (System.Exception) { }
                try { t.GenerateLayout(); } catch (System.Exception) { }
                try { t.RecomputeTableBlock(true); } catch (System.Exception) { }
            }

            return cells;
        }

        // ------------------------------------------------- высота шрифта в стилях

        /// <summary>
        /// Высота текста в стиле (STYLE, поле «Высота»). Если она не равна нулю,
        /// стиль считается стилем «с фиксированной высотой», и эта высота
        /// перебивает всё остальное: у обычного текста игнорируется собственная
        /// высота объекта, у размеров — произведение DIMTXT x DIMSCALE. Ни
        /// TransformBy, ни наши переопределения на объектах до неё не достают,
        /// поэтому после сжатия чертежа подписи остаются прежнего размера.
        ///
        /// Именно так выглядел чертёж, где «отмасштабировалось всё, кроме размера
        /// шрифта у размеров»: в нём почти все стили ISOCPEUR* заведены с высотой
        /// 700, 600, 125 и так далее. Обнуление высоты в стилях руками помогает
        /// ровно потому, что возвращает управление паре DIMTXT x DIMSCALE.
        ///
        /// Стиль общий на весь чертёж, отдельного переопределения «высота стиля
        /// для этого объекта» в DWG не существует. Поэтому трогаем только те
        /// стили, которыми не пользуется больше никто: ни листы, ни определения
        /// блоков, ни невыделенные объекты. Иначе вместе с моделью ужался бы
        /// штамп на листе, где высота задана уже в миллиметрах бумаги.
        /// </summary>
        private static void ScaleTextStyles(Transaction tr, Database db, ObjectId[] ids,
                                            HashSet<ObjectId> usedByUs, double divisor,
                                            double factor, Stats st)
        {
            var mine = new HashSet<ObjectId>(ids);

            // Обломки расчленённых прокси — наши, хоть в исходном выборе их и не
            // было: стили с них собираем, а сами объекты помечаем как свои, чтобы
            // обход «чужого» не принял их за посторонних.
            foreach (ObjectId added in st.Added)
            {
                mine.Add(added);
                try
                {
                    var piece = tr.GetObject(added, OpenMode.ForRead, false, true) as Entity;
                    if (piece != null) CollectTextStyles(tr, piece, usedByUs, false);
                }
                catch (AcRxException) { }
            }

            // Сначала отбираем стили с фиксированной высотой. Если таких нет —
            // а в здоровом чертеже их и не бывает, — выходим, не читая базу
            // целиком: обход всех определений блоков ради пустого результата
            // на большом чертеже стоит дороже самой операции.
            var candidates = new List<ObjectId>();
            foreach (ObjectId styleId in usedByUs)
            {
                if (FixedTextHeight(tr, styleId) > 0)
                    candidates.Add(styleId);
            }
            if (candidates.Count == 0)
                return;

            HashSet<ObjectId> elsewhere = CollectStylesUsedElsewhere(tr, db, mine);
            Dictionary<ObjectId, ObjectId> remap = null;

            foreach (ObjectId styleId in candidates)
            {
                TextStyleTableRecord style;
                try { style = tr.GetObject(styleId, OpenMode.ForRead) as TextStyleTableRecord; }
                catch (AcRxException) { continue; }
                if (style == null)
                    continue;

                double height = FixedTextHeight(tr, styleId);
                if (height <= 0)
                    continue;

                string name = SafeStyleName(style, styleId);

                // Стилем пользуется кто-то ещё — лист, определение блока,
                // невыделенный объект. Делить высоту у него нельзя: у листа она
                // задана уже в миллиметрах бумаги, а внутри блока текст и так
                // ужмётся вместе со вставкой. Заводим копию стиля с поделённой
                // высотой и переводим на неё только свои объекты — та же логика,
                // что с DIMSCALE: переопределение на объекте, а не правка общего.
                if (elsewhere.Contains(styleId))
                {
                    string cloneName;
                    ObjectId cloneId = CloneTextStyle(tr, db, styleId, height * factor,
                                                      divisor, out cloneName);
                    if (cloneId.IsNull)
                    {
                        st.StylesShared.Add(name);
                        continue;
                    }

                    if (remap == null)
                        remap = new Dictionary<ObjectId, ObjectId>();
                    remap[styleId] = cloneId;
                    st.StylesCloned.Add(name + " -> " + cloneName
                                        + ": " + Num(height) + " -> " + Num(height * factor));
                    continue;
                }

                try
                {
                    style.UpgradeOpen();
                    style.TextSize = height * factor;
                    st.StylesScaled.Add(name + ": " + Num(height) + " -> " + Num(height * factor));
                }
                catch (AcRxException) { st.StylesFailed.Add(name); }
                catch (System.Exception) { st.StylesFailed.Add(name); }
            }

            if (remap != null)
                RepointTextStyles(tr, mine, remap, st);
        }

        /// <summary>
        /// Копия стиля текста с другой высотой. Нужна, когда исходный стиль общий
        /// с листом, блоком или невыделенными объектами: правка на месте задела бы
        /// и их, а переопределения «высота стиля для этого объекта» в DWG нет.
        /// Возвращает ObjectId.Null, если завести копию не вышло, — тогда стиль
        /// остаётся как есть и попадает в отчёт отдельной строкой.
        /// </summary>
        private static ObjectId CloneTextStyle(Transaction tr, Database db, ObjectId srcId,
                                               double newHeight, double divisor, out string cloneName)
        {
            cloneName = null;
            try
            {
                var src = tr.GetObject(srcId, OpenMode.ForRead) as TextStyleTableRecord;
                if (src == null)
                    return ObjectId.Null;

                var tt = (TextStyleTable)tr.GetObject(db.TextStyleTableId, OpenMode.ForWrite);
                string name = UniqueStyleName(tt, src.Name, divisor);
                if (name == null)
                    return ObjectId.Null;

                var clone = new TextStyleTableRecord();
                clone.Name = name;

                // Имя файла ставим раньше Font: у TTF-стилей запись дескриптора
                // переписывает FileName, и обратный порядок затёр бы шрифт.
                try { clone.FileName = src.FileName; } catch (System.Exception) { }
                try { clone.BigFontFileName = src.BigFontFileName; } catch (System.Exception) { }
                try { clone.Font = src.Font; } catch (System.Exception) { }
                try { clone.XScale = src.XScale; } catch (System.Exception) { }
                try { clone.ObliquingAngle = src.ObliquingAngle; } catch (System.Exception) { }
                try { clone.FlagBits = src.FlagBits; } catch (System.Exception) { }
                clone.TextSize = newHeight;

                ObjectId id = tt.Add(clone);
                tr.AddNewlyCreatedDBObject(clone, true);
                cloneName = name;
                return id;
            }
            catch (System.Exception) { return ObjectId.Null; }
        }

        /// <summary>
        /// Имя для копии стиля: «исходное_ZZ1000». Если занято — с номером.
        /// Повторный запуск команды новых копий не плодит: объекты уже смотрят на
        /// копию, а ею, кроме них, никто не пользуется — её поделят на месте.
        /// </summary>
        private static string UniqueStyleName(TextStyleTable tt, string baseName, double divisor)
        {
            // Уменьшение — «_ZZ1000», увеличение — «_ZZx1000».
            string suffix = (divisor >= 1 ? "_ZZ" : "_ZZx")
                          + (divisor >= 1 ? divisor : 1.0 / divisor).ToString("0.####", CultureInfo.InvariantCulture)
                                           .Replace('.', '_').Replace(',', '_');

            // Предел длины имени в таблице символов — 255 знаков.
            string head = baseName ?? "Style";
            if (head.Length + suffix.Length > 240)
                head = head.Substring(0, 240 - suffix.Length);

            string candidate = head + suffix;
            try { if (!tt.Has(candidate)) return candidate; }
            catch (System.Exception) { return null; }

            for (int i = 1; i < 1000; i++)
            {
                string n = candidate + "_" + i;
                try { if (!tt.Has(n)) return n; }
                catch (System.Exception) { return null; }
            }
            return null;
        }

        /// <summary>
        /// Переводит наши объекты со старых стилей на копии. У размера это
        /// оборачивается переопределением DIMTXSTY на самом объекте — ровно так же,
        /// как команда уже поступает с DIMSCALE и DIMLFAC.
        /// </summary>
        private static void RepointTextStyles(Transaction tr, HashSet<ObjectId> mine,
                                              Dictionary<ObjectId, ObjectId> remap, Stats st)
        {
            foreach (ObjectId id in mine)
            {
                Entity ent;
                try { ent = tr.GetObject(id, OpenMode.ForWrite, false, true) as Entity; }
                catch (AcRxException) { continue; }
                if (ent == null)
                    continue;

                try { if (ent.Annotative == AnnotativeStates.True) continue; }
                catch (System.Exception) { }

                try { st.StylesRepointed += Repoint(tr, ent, remap); }
                catch (System.Exception) { }
            }
        }

        private static int Repoint(Transaction tr, Entity ent, Dictionary<ObjectId, ObjectId> remap)
        {
            ObjectId to;

            var txt = ent as DBText;
            if (txt != null)
            {
                if (!remap.TryGetValue(txt.TextStyleId, out to)) return 0;
                txt.TextStyleId = to;
                return 1;
            }

            var mtext = ent as MText;
            if (mtext != null)
            {
                if (!remap.TryGetValue(mtext.TextStyleId, out to)) return 0;
                mtext.TextStyleId = to;
                return 1;
            }

            var dim = ent as Dimension;
            if (dim != null)
            {
                if (!remap.TryGetValue(dim.TextStyleId, out to)) return 0;
                dim.TextStyleId = to;
                // Пересчёт не здесь: он общий и идёт после правки всех стилей.
                return 1;
            }

            var mleader = ent as MLeader;
            if (mleader != null)
            {
                int n = 0;
                if (remap.TryGetValue(mleader.TextStyleId, out to))
                {
                    mleader.TextStyleId = to;
                    n = 1;
                }
                // Содержимое выноски — отдельный MText, и его надо положить обратно:
                // свойство отдаёт копию, правка на месте до объекта не доходит.
                try
                {
                    MText content = mleader.MText;
                    if (content != null && remap.TryGetValue(content.TextStyleId, out to))
                    {
                        content.TextStyleId = to;
                        mleader.MText = content;
                        n = 1;
                    }
                }
                catch (System.Exception) { }
                return n;
            }

            var fcf = ent as FeatureControlFrame;
            if (fcf != null)
            {
                if (!remap.TryGetValue(fcf.TextStyleId, out to)) return 0;
                fcf.TextStyleId = to;
                return 1;
            }

            var table = ent as Table;
            if (table != null)
            {
                int n = 0;
                int rows = table.Rows.Count, cols = table.Columns.Count;
                try { table.SuppressRegenerateTable(true); }
                catch (System.Exception) { }
                try
                {
                    for (int r = 0; r < rows; r++)
                        for (int c = 0; c < cols; c++)
                        {
                            try
                            {
                                CellContentsCollection contents = table.Cells[r, c].Contents;
                                for (int i = 0; i < contents.Count; i++)
                                {
                                    if (!remap.TryGetValue(contents[i].TextStyleId, out to)) continue;
                                    contents[i].TextStyleId = to;
                                    n++;
                                }
                            }
                            catch (System.Exception) { }
                        }
                }
                finally
                {
                    try { table.SuppressRegenerateTable(false); } catch (System.Exception) { }
                    try { table.GenerateLayout(); } catch (System.Exception) { }
                }
                return n;
            }

            var block = ent as BlockReference;
            if (block != null)
            {
                int n = 0;
                foreach (ObjectId attId in block.AttributeCollection)
                {
                    try
                    {
                        var att = tr.GetObject(attId, OpenMode.ForWrite, false, true)
                                  as AttributeReference;
                        if (att != null && remap.TryGetValue(att.TextStyleId, out to))
                        {
                            att.TextStyleId = to;
                            n++;
                        }
                    }
                    catch (System.Exception) { }
                }
                return n;
            }

            return 0;
        }

        /// <summary>
        /// Фиксированная высота стиля, или 0, если её нет. Ноль означает, что
        /// высоту задаёт сам объект, а её уже отмасштабировал TransformBy.
        /// Стиль формы (SHX shape) — не шрифт, высоты у него нет вовсе.
        /// </summary>
        private static double FixedTextHeight(Transaction tr, ObjectId styleId)
        {
            try
            {
                var style = tr.GetObject(styleId, OpenMode.ForRead) as TextStyleTableRecord;
                if (style == null || style.IsShapeFile)
                    return 0;
                double h = style.TextSize;
                return h > 0 ? h : 0;
            }
            catch (System.Exception) { return 0; }
        }

        /// <summary>
        /// Стили текста, которыми пользуется всё, что мы НЕ обрабатываем: листы,
        /// определения блоков, невыделенные объекты текущего пространства.
        /// Обход идёт по таблице блоков — в ней лежат и пространства, и блоки.
        /// </summary>
        private static HashSet<ObjectId> CollectStylesUsedElsewhere(
            Transaction tr, Database db, HashSet<ObjectId> mine)
        {
            var others = new HashSet<ObjectId>();
            BlockTable bt;
            try { bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead); }
            catch (AcRxException) { return others; }

            foreach (ObjectId btrId in bt)
            {
                BlockTableRecord btr;
                try { btr = (BlockTableRecord)tr.GetObject(btrId, OpenMode.ForRead); }
                catch (AcRxException) { continue; }

                foreach (ObjectId id in btr)
                {
                    if (mine.Contains(id))
                        continue;
                    try
                    {
                        var e = tr.GetObject(id, OpenMode.ForRead, false, true) as Entity;
                        // true: даже аннотативный объект держит стиль занятым.
                        if (e != null) CollectTextStyles(tr, e, others, true);
                    }
                    catch (AcRxException) { }
                }
            }
            return others;
        }

        /// <summary>
        /// Складывает в набор стили текста, на которые ссылается объект.
        ///
        /// evenAnnotative = false пропускает аннотативные объекты: их размер
        /// задаётся масштабом аннотаций, мы его не трогаем, и высота их стиля —
        /// это высота на бумаге, а не в чертеже. При обходе «чужого» наоборот
        /// нужно true: аннотативный объект всё равно держит стиль занятым, и
        /// делить его высоту нельзя.
        /// </summary>
        private static void CollectTextStyles(Transaction tr, Entity ent,
                                              HashSet<ObjectId> into, bool evenAnnotative)
        {
            if (!evenAnnotative)
            {
                try { if (ent.Annotative == AnnotativeStates.True) return; }
                catch (System.Exception) { }
            }

            try
            {
                // DBText накрывает и TEXT, и ATTDEF, и ATTRIB.
                var txt = ent as DBText;
                if (txt != null) { AddStyle(into, txt.TextStyleId); return; }

                var mtext = ent as MText;
                if (mtext != null) { AddStyle(into, mtext.TextStyleId); return; }

                // У таблицы стиль текста свой в каждой ячейке.
                var table = ent as Table;
                if (table != null)
                {
                    int rows = table.Rows.Count, cols = table.Columns.Count;
                    for (int r = 0; r < rows; r++)
                        for (int c = 0; c < cols; c++)
                        {
                            try
                            {
                                CellContentsCollection contents = table.Cells[r, c].Contents;
                                for (int i = 0; i < contents.Count; i++)
                                    AddStyle(into, contents[i].TextStyleId);
                            }
                            catch (System.Exception) { }
                        }
                    return;
                }

                // У размера свойство отдаёт действующий DIMTXSTY — хоть из стиля,
                // хоть из переопределения на самом объекте.
                var dim = ent as Dimension;
                if (dim != null) { AddStyle(into, dim.TextStyleId); return; }

                var mleader = ent as MLeader;
                if (mleader != null)
                {
                    AddStyle(into, mleader.TextStyleId);
                    try
                    {
                        MText content = mleader.MText;
                        if (content != null) AddStyle(into, content.TextStyleId);
                    }
                    catch (System.Exception) { }
                    return;
                }

                var fcf = ent as FeatureControlFrame;
                if (fcf != null) { AddStyle(into, fcf.TextStyleId); return; }

                // Атрибуты вставки — отдельные объекты, в таблице блоков их нет,
                // добираться до них надо через саму вставку.
                var block = ent as BlockReference;
                if (block != null)
                {
                    foreach (ObjectId attId in block.AttributeCollection)
                    {
                        try
                        {
                            var att = tr.GetObject(attId, OpenMode.ForRead, false, true)
                                      as AttributeReference;
                            if (att != null) AddStyle(into, att.TextStyleId);
                        }
                        catch (System.Exception) { }
                    }
                }
            }
            catch (System.Exception) { }
        }

        private static void AddStyle(HashSet<ObjectId> into, ObjectId id)
        {
            if (!id.IsNull && id.IsValid)
                into.Add(id);
        }

        private static string SafeStyleName(TextStyleTableRecord style, ObjectId id)
        {
            try { return style.Name; }
            catch (System.Exception) { return id.ToString(); }
        }

        private static string Num(double v)
        {
            return v.ToString("0.######", CultureInfo.GetCultureInfo("ru-RU"));
        }

        /// <summary>
        /// Масштабирование OLE-вставки: таблицы Excel, куска документа Word,
        /// картинки, положенной в чертёж «Специальной вставкой».
        ///
        /// Ole2Frame — не геометрия. Содержимое принадлежит чужому приложению,
        /// а в чертеже от него остаётся прямоугольная рамка с четырьмя углами в
        /// WCS. TransformBy эту рамку не пересчитывает: вставка либо не двигается
        /// вовсе, либо съезжает, сохранив прежний размер, — и после сжатия чертежа
        /// в 1000 раз таблица закрывает его целиком.
        ///
        /// Цели считаются один раз от исходного состояния, а каждый следующий шаг
        /// выполняется, только если предыдущий не сработал. Поэтому шаги не могут
        /// наложиться друг на друга и уменьшить вставку дважды.
        /// </summary>
        private static bool ScaleOle(Ole2Frame ole, Matrix3d mat, double factor)
        {
            Point3d origLoc = ole.Location;
            double origW = ole.WcsWidth, origH = ole.WcsHeight;

            Point3d targetLoc = origLoc.TransformBy(mat);
            double targetW = origW * factor, targetH = origH * factor;

            // Шаг 1. Четыре угла рамки — основное хранилище: они задают разом и
            // положение, и размер, и разворот. Обычно этого шага хватает.
            try
            {
                Rectangle3d r = ole.Position3d;
                ole.Position3d = new Rectangle3d(
                    r.UpperLeft.TransformBy(mat),
                    r.UpperRight.TransformBy(mat),
                    r.LowerLeft.TransformBy(mat),
                    r.LowerRight.TransformBy(mat));
            }
            catch (System.Exception) { }

            // Шаг 2. Размер напрямую — если углы его не изменили.
            if (!Near(ole.WcsWidth, targetW) || !Near(ole.WcsHeight, targetH))
            {
                try
                {
                    ole.WcsWidth = targetW;
                    ole.WcsHeight = targetH;
                }
                catch (System.Exception) { }
            }

            // Шаг 3. Последний рубеж — доля от натурального размера вставки.
            // Только если ни ширина, ни высота вообще не сдвинулись с исходных:
            // иначе множитель лёг бы вторым слоем поверх уже применённого.
            if (!Near(origW, targetW)
                && Near(ole.WcsWidth, origW) && Near(ole.WcsHeight, origH))
            {
                try
                {
                    ole.ScaleWidth = ole.ScaleWidth * factor;
                    ole.ScaleHeight = ole.ScaleHeight * factor;
                }
                catch (System.Exception) { }
            }

            // Шаг 4. Положение — если рамка осталась на прежнем месте. Базовая
            // точка операции 0,0,0, поэтому вставку надо не только сжать, но и
            // придвинуть к началу координат вместе со всем чертежом.
            if (!Near(ole.Location, targetLoc))
            {
                try { ole.TransformBy(Matrix3d.Displacement(targetLoc - ole.Location)); }
                catch (System.Exception) { }
            }

            return Near(ole.WcsWidth, targetW)
                && Near(ole.WcsHeight, targetH)
                && Near(ole.Location, targetLoc);
        }

        /// <summary>
        /// Сравнение с относительным допуском. Абсолютный здесь не работает:
        /// координаты съёмки бывают порядка 42 500 000, а после деления на 1000 —
        /// порядка 42 500, и одна и та же «мелочь» на этих порядках разная.
        /// </summary>
        private static bool Near(double a, double b)
        {
            return Math.Abs(a - b) <= Math.Max(1e-9, Math.Abs(b) * 1e-6);
        }

        private static bool Near(Point3d a, Point3d b)
        {
            return Near(a.X, b.X) && Near(a.Y, b.Y) && Near(a.Z, b.Z);
        }

        /// <summary>
        /// Разбирает прокси на примитивы, масштабирует их и удаляет исходник.
        ///
        /// Прокси — это объект приложения, которого в системе нет (СПДС GraphiCS,
        /// выгрузки из BIM). Внутри у него только закэшированная графика, пересчитать
        /// её некому, и TransformBy на нём молча ничего не делает. После расчленения
        /// получаются обычные отрезки, дуги и текст, которые масштабируются штатно.
        /// Интеллект объекта при этом теряется — но его и не было, пока нет приложения.
        ///
        /// false — расчленить не удалось, объект оставлен как есть.
        /// </summary>
        private const int MaxProxyDepth = 4;

        private static bool ExplodeProxy(Transaction tr, BlockTableRecord space, Entity proxy,
                                         Matrix3d mat, double divisor, double factor, Stats st,
                                         int depth = 0, List<ObjectId> collect = null)
        {
            // Обломки этого прокси, включая обломки вложенных, в порядке
            // появления — чтобы потом поставить их на его место в порядке
            // прорисовки (см. RestoreProxyDrawOrder).
            bool top = collect == null;
            if (top)
                collect = new List<ObjectId>();

            var pieces = new DBObjectCollection();
            try
            {
                proxy.Explode(pieces);
            }
            catch (System.Exception)
            {
                NoteProxyFail(proxy, "расчленение отклонено объектом", st);
                return false;
            }

            // Пусто — у объекта нет собственной графики. Такие среди СПДС
            // попадаются: чистые контейнеры данных, на чертеже их не видно,
            // и масштабировать в них нечего.
            if (pieces.Count == 0)
            {
                NoteProxyFail(proxy, "расчленение дало пусто (графики нет)", st);
                return false;
            }

            // Размер исходника — мерка, с которой сравниваются обломки.
            double sourceSize = Diagonal(proxy);

            int added = 0;
            foreach (DBObject obj in pieces)
            {
                var piece = obj as Entity;
                if (piece == null)
                {
                    obj.Dispose();
                    continue;
                }

                space.AppendEntity(piece);
                tr.AddNewlyCreatedDBObject(piece, true);

                // Прокси внутри прокси — разбираем дальше, иначе вложенный кусок
                // тихо останется в старом масштабе, как и весь объект до правки.
                if (piece is ProxyEntity)
                {
                    if (depth + 1 < MaxProxyDepth &&
                        ExplodeProxy(tr, space, piece, mat, divisor, factor, st, depth + 1, collect))
                        added++;
                    else
                    {
                        Count(st.Failures, "вложенный прокси");
                        collect.Add(piece.ObjectId);
                    }
                    continue;
                }

                collect.Add(piece.ObjectId);
                st.Added.Add(piece.ObjectId);
                NoteIfOversize(piece, sourceSize, st);

                try
                {
                    ApplyToEntity(piece, mat, divisor, factor, st);
                    added++;
                }
                catch (System.Exception)
                {
                    Count(st.Failures, "обломок прокси");
                }
            }

            if (added == 0)
            {
                NoteProxyFail(proxy, "ни один обломок не принялся", st);
                return false;
            }

            // Исходник больше не нужен: его графику мы только что воспроизвели.
            try
            {
                proxy.UpgradeOpen();
                proxy.Erase();
            }
            catch (System.Exception) { }

            if (depth == 0)
                st.ProxyExploded++;      // вложенные не считаем отдельными объектами
            st.ProxyPieces += added;
            if (top)
                st.ProxyPieceIds[proxy.ObjectId] = collect;
            return true;
        }

        /// <summary>
        /// Прокси можно перенести в блок, только если сам объект это разрешает:
        /// копирование (флаг 128) и удаление (флаг 1) — оригинал после переноса
        /// стирается.
        /// </summary>
        private static bool CanWrapProxy(ProxyEntity px)
        {
            try
            {
                int flags = px.ProxyFlags;
                return (flags & 128) != 0 && (flags & 1) != 0;
            }
            catch (System.Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// Прокси — в блок целиком, а блок — вставкой с масштабом коэффициента.
        ///
        /// Расчленение прокси в AutoCAD теряет данные: залитые стрелки и
        /// полосы обозначений разрезов приходят пустыми контурами, а цвет, с
        /// которым прокси рисовал свою графику, заменяется цветом слоя. На
        /// листах это было видно сразу: чёрные залитые знаки становились
        /// зелёными контурами. Перекраской этого не починить — расчленение
        /// отдаёт не то, что нарисовано.
        ///
        /// Сам прокси двигать нельзя (TransformBy он игнорирует), но копию его
        /// можно положить в блок, а вставку блока масштабировать штатно. Внутри
        /// лежит тот же объект со своей графикой — заливки и цвета остаются
        /// точно такими же.
        ///
        /// Порядок прорисовки: подряд идущие прокси (в порядке прорисовки) —
        /// один блок, и вставка встаёт на место первого из них. Так блоков
        /// меньше, а перекрытия с остальными объектами остаются как были.
        ///
        /// Вставка — на слое 0 с цветом, типом и весом линий «ПоБлоку»: на
        /// верхнем уровне это ведёт себя ровно как сами прокси раньше, а слой
        /// каждого прокси внутри блока сохраняется — его заморозка, в том числе
        /// в отдельном видовом экране, по-прежнему прячет его.
        /// </summary>
        private static void WrapProxies(Transaction tr, Database db, BlockTableRecord space,
                                        ObjectIdCollection orderBefore, Matrix3d mat, Stats st)
        {
            if (st.ProxyWrapIds.Count == 0)
                return;

            var wrap = new HashSet<ObjectId>(st.ProxyWrapIds);
            var runs = new List<List<ObjectId>>();
            if (orderBefore != null)
            {
                List<ObjectId> run = null;
                foreach (ObjectId id in orderBefore)
                {
                    if (wrap.Contains(id))
                    {
                        if (run == null) { run = new List<ObjectId>(); runs.Add(run); }
                        run.Add(id);
                        wrap.Remove(id);
                    }
                    else
                        run = null;     // между прокси что-то другое — серия кончилась
                }
            }
            // Без порядка прорисовки или не попавшие в него — отдельной серией.
            if (wrap.Count > 0)
                runs.Add(new List<ObjectId>(wrap));

            var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForWrite);
            foreach (List<ObjectId> run in runs)
            {
                try
                {
                    var btr = new BlockTableRecord { Name = "*U" };
                    ObjectId btrId = bt.Add(btr);
                    tr.AddNewlyCreatedDBObject(btr, true);

                    var map = new IdMapping();
                    db.DeepCloneObjects(new ObjectIdCollection(run.ToArray()), btrId, map, false);

                    // Проверка, что копии действительно легли в блок. Прокси с
                    // запретом копирования AutoCAD «копирует» молча и впустую —
                    // без ошибки, с пустым блоком. Стереть оригиналы после
                    // такого — значит потерять их с чертежа.
                    int copied = 0;
                    foreach (ObjectId c in btr) copied++;
                    if (copied != run.Count)
                    {
                        btr.Erase();
                        st.ProxyWrapFallback.AddRange(run);
                        continue;
                    }

                    var br = new BlockReference(Point3d.Origin, btrId);
                    br.SetDatabaseDefaults(db);
                    br.LayerId = db.LayerZero;
                    br.ColorIndex = 0;                                   // ПоБлоку
                    br.Linetype = "ByBlock";
                    br.LineWeight = LineWeight.ByBlock;
                    br.TransformBy(mat);
                    space.AppendEntity(br);
                    tr.AddNewlyCreatedDBObject(br, true);

                    foreach (ObjectId id in run)
                    {
                        var px = (Entity)tr.GetObject(id, OpenMode.ForWrite, false, true);
                        px.Erase();
                    }

                    // Для RestoreProxyDrawOrder: первая — на место вставки, остальные исчезают.
                    st.ProxyPieceIds[run[0]] = new List<ObjectId> { br.ObjectId };
                    for (int i = 1; i < run.Count; i++)
                        st.ProxyPieceIds[run[i]] = new List<ObjectId>();

                    st.ProxyWrapped += run.Count;
                    st.ProxyWrapBlocks++;
                }
                catch (System.Exception ex)
                {
                    Count(st.Failures, "прокси не перенеслись в блок: " + ex.Message);
                }
            }
        }

        /// <summary>
        /// Обломки прокси — на место самого прокси в порядке прорисовки.
        ///
        /// Новые объекты AutoCAD рисует последними, поверх всего. Обломки
        /// добавляются в пространство в конец, и там, где прокси перекрывался
        /// с другими объектами, сверху оказывалось не то, что было: голубая
        /// ось уходила под оранжевый профиль, чёрный текст — под зелёную
        /// линию. Геометрия на месте, а картинка другая — на листе это видно
        /// как перекрашенные линии. Порядок снимается до расчленения, и в нём
        /// каждый прокси заменяется своими обломками.
        /// </summary>
        private static void RestoreProxyDrawOrder(Transaction tr, BlockTableRecord space,
                                                  ObjectIdCollection orderBefore, Stats st)
        {
            if (orderBefore == null || st.ProxyPieceIds.Count == 0)
                return;

            var pieces = new HashSet<ObjectId>();
            foreach (List<ObjectId> list in st.ProxyPieceIds.Values)
                foreach (ObjectId id in list)
                    pieces.Add(id);

            var order = new ObjectIdCollection();
            foreach (ObjectId id in orderBefore)
            {
                List<ObjectId> list;
                if (st.ProxyPieceIds.TryGetValue(id, out list))
                {
                    foreach (ObjectId p in list)
                        if (!p.IsErased)
                            order.Add(p);
                }
                else if (!id.IsErased && !pieces.Contains(id))
                    order.Add(id);
            }

            try
            {
                var dot = (DrawOrderTable)tr.GetObject(space.DrawOrderTableId, OpenMode.ForWrite);
                dot.SetRelativeDrawOrder(order);
                st.ProxyOrderRestored = true;
            }
            catch (System.Exception)
            {
                Count(st.Failures, "порядок прорисовки обломков прокси не восстановлен");
            }
        }

        /// <summary>
        /// Масштабирует «оформительскую» часть аннотационных объектов: высоту текста,
        /// стрелки, полки, зазоры вокруг подписи. Всё это считается как
        /// «величина из стиля × DIMSCALE» в момент отрисовки, поэтому в координатах
        /// объекта не лежит и TransformBy его не меняет — после сжатия чертежа в
        /// 1000 раз подписи со стрелками остались бы прежнего размера.
        ///
        /// Ставим индивидуальное переопределение на самом объекте, а не правим стиль:
        /// стиль общий для всего чертежа, и при выборе рамкой он задел бы и размеры,
        /// которые пользователь не выделял.
        ///
        /// Возврат: 1 — отмасштабировано, 0 — объекту неприменимо,
        /// -1 — объект аннотативный, его размер задаёт масштаб аннотаций.
        /// </summary>
        private static int ScaleAnnotationSize(Entity ent, double factor, Stats st)
        {
            bool annotative;
            try { annotative = ent.Annotative == AnnotativeStates.True; }
            catch (System.Exception) { annotative = false; }

            var dim = ent as Dimension;
            if (dim != null)
            {
                double next = NextDimscale(dim.Dimscale, annotative, factor, st);
                if (next <= 0) return -1;
                dim.Dimscale = next;
                return 1;
            }

            // Мультивыноски здесь намеренно нет: у неё высота текста и стрелки
            // лежат не в стиле, а на самом объекте, и TransformBy их уже поделил.
            // Множитель Scale поверх этого лёг бы вторым слоем — см. ScaleMLeader.

            // Старая выноска (QLEADER/LEADER) — тоже живёт по размерному стилю.
            var leader = ent as Leader;
            if (leader != null)
            {
                double next = NextDimscale(leader.Dimscale, annotative, factor, st);
                if (next <= 0) return -1;
                leader.Dimscale = next;
                return 1;
            }

            // Допуск формы и расположения (TOLERANCE).
            var fcf = ent as FeatureControlFrame;
            if (fcf != null)
            {
                double next = NextDimscale(fcf.Dimscale, annotative, factor, st);
                if (next <= 0) return -1;
                fcf.Dimscale = next;
                return 1;
            }

            return 0;
        }

        /// <summary>
        /// Новое значение DIMSCALE, или 0, если объект трогать нельзя.
        ///
        /// DIMSCALE = 0 означает «взять масштаб от видового экрана листа», а в
        /// пространстве модели — там, где команда и работает, — это ровно 1.0,
        /// так написано в справке AutoCAD. Пропускать такие объекты нельзя:
        /// это даёт очень обманчивую картину — текст у них встаёт
        /// на место (его высоту задаёт стиль текста с фиксированной высотой,
        /// который команда делит отдельно), а DIMEXE и DIMASZ остаются в прежнем
        /// абсолютном размере. На чертеже, ужатом в 1000 раз, выносная линия
        /// длиной DIMEXE = 1.25 единицы перечёркивает его целиком — размеры
        /// выглядят правильными, но из них во все стороны торчат линии
        /// длиннее самого чертежа.
        ///
        /// Аннотативные — другое дело: у них размер задаёт масштаб аннотаций,
        /// DIMSCALE в отрисовке не участвует вовсе, и трогать его бессмысленно.
        /// Они по-прежнему уходят в отчёт отдельной строкой.
        /// </summary>
        private static double NextDimscale(double current, bool annotative, double factor, Stats st)
        {
            if (annotative)
                return 0;

            if (current > 0)
                return current * factor;

            // Отрицательного DIMSCALE не бывает; если попался — не наш случай.
            if (current < 0)
                return 0;

            st.DimscaleZero++;
            return factor;
        }

        private static string SafeTypeName(Entity ent)
        {
            try { return ent.GetRXClass().Name; }
            catch (System.Exception) { return ent.GetType().Name; }
        }

        private static void Count(SortedDictionary<string, int> map, string key)
        {
            int n;
            map.TryGetValue(key, out n);
            map[key] = n + 1;
        }

        // ------------------------------------------------------------- слои

        private static List<ObjectId> UnlockAllLayers(Database db)
        {
            var unlocked = new List<ObjectId>();
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                var lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
                foreach (ObjectId id in lt)
                {
                    var ltr = (LayerTableRecord)tr.GetObject(id, OpenMode.ForRead);
                    if (!ltr.IsLocked)
                        continue;
                    ltr.UpgradeOpen();
                    ltr.IsLocked = false;
                    unlocked.Add(id);
                }
                tr.Commit();
            }
            return unlocked;
        }

        private static void RelockLayers(Database db, List<ObjectId> ids)
        {
            if (ids == null || ids.Count == 0)
                return;
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                foreach (ObjectId id in ids)
                {
                    var ltr = tr.GetObject(id, OpenMode.ForWrite) as LayerTableRecord;
                    if (ltr != null)
                        ltr.IsLocked = true;
                }
                tr.Commit();
            }
        }

        // ------------------------------------------------------- системные переменные

        /// <summary>
        /// Глобальный LTSCALE делим на тот же коэффициент. Индивидуальный
        /// LinetypeScale объектов НЕ трогаем: длина штриха = LTSCALE * LinetypeScale,
        /// поделив оба, получили бы уменьшение в миллион раз.
        /// </summary>
        private static void AdjustLinetypeScale(double factor, out double oldValue, out double newValue)
        {
            oldValue = 0;
            newValue = 0;
            try
            {
                oldValue = Convert.ToDouble(AcadApp.GetSystemVariable("LTSCALE"), CultureInfo.InvariantCulture);
                newValue = oldValue * factor;
                AcadApp.SetSystemVariable("LTSCALE", newValue);
            }
            catch (System.Exception) { }
        }

        /// <summary>PDSIZE > 0 задан в единицах чертежа, значит его тоже надо пересчитать.
        /// Отрицательный PDSIZE — процент от экрана, его не трогаем.</summary>
        private static bool AdjustPointSize(double factor, out double oldValue, out double newValue)
        {
            oldValue = 0;
            newValue = 0;
            try
            {
                oldValue = Convert.ToDouble(AcadApp.GetSystemVariable("PDSIZE"), CultureInfo.InvariantCulture);
                if (oldValue <= 0)
                    return false;
                newValue = oldValue * factor;
                AcadApp.SetSystemVariable("PDSIZE", newValue);
                return true;
            }
            catch (System.Exception) { return false; }
        }

        /// <summary>
        /// TEXTSIZE — высота, с которой создаётся новый текст. Сам чертёж от неё
        /// не зависит, но без пересчёта первая же надпись после операции вышла бы
        /// в 1000 раз крупнее соседних.
        /// </summary>
        private static bool AdjustTextSize(double factor, out double oldValue, out double newValue)
        {
            oldValue = 0;
            newValue = 0;
            try
            {
                oldValue = Convert.ToDouble(AcadApp.GetSystemVariable("TEXTSIZE"), CultureInfo.InvariantCulture);
                if (oldValue <= 0)
                    return false;
                newValue = oldValue * factor;
                AcadApp.SetSystemVariable("TEXTSIZE", newValue);
                return true;
            }
            catch (System.Exception) { return false; }
        }

        // ------------------------------------------------------------- отчёт

        private static void Report(Editor ed, Database db, double divisor, double factor,
                                   Point3d basePoint, int total, Stats st,
                                   bool wholeSpace, int unlockedLayers,
                                   double oldLts, double newLts,
                                   bool pdChanged, double oldPd, double newPd,
                                   bool tsChanged, double oldTs, double newTs)
        {
            var ci = CultureInfo.GetCultureInfo("ru-RU");
            int done = st.Done, dims = st.Dims, blocks = st.Blocks, hatches = st.Hatches;
            int annotative = st.Annotative, annoScaled = st.AnnoScaled, annoViewport = st.AnnoViewport;
            var failures = st.Failures;

            ed.WriteMessage("\n\n========== {0} ==========", RescaleApp.PluginName);
            ed.WriteMessage("\n Коэффициент      : {0}  ({1}, умножение на {2})",
                divisor >= 1 ? "1 : " + divisor.ToString("0.####", ci)
                             : (1.0 / divisor).ToString("0.####", ci) + " : 1",
                divisor >= 1 ? "уменьшение" : "увеличение",
                factor.ToString("0.##########", ci));
            ed.WriteMessage("\n Базовая точка    : {0}{1}", FormatPoint(basePoint, ci),
                wholeSpace ? "" : "  (центр выбранного — оно остаётся на своём месте)");
            ed.WriteMessage("\n Область          : {0}", wholeSpace ? "всё текущее пространство" : "выбранные объекты");
            ed.WriteMessage("\n Обработано       : {0} из {1}", done, total);

            if (dims > 0)
                ed.WriteMessage("\n   размеров       : {0}  (DIMLFAC x{1} — подписи сохранены)",
                    dims, divisor.ToString("0.####", ci));
            if (st.DimscaleZero > 0)
                ed.WriteMessage("\n   DIMSCALE=0     : {0}  (в модели это 1.0 — принято за 1.0 и поделено, иначе выносные линии остались бы длиннее чертежа)",
                    st.DimscaleZero);

            if (annoScaled > 0)
                ed.WriteMessage("\n   оформление     : {0}  (DIMSCALE x{1} — текст, стрелки, полки)",
                    annoScaled, factor.ToString("0.##########", ci));
            if (st.DimBakedRestored > 0)
                ed.WriteMessage("\n   вид размеров   : {0} из {1}  (исходная картинка блока сохранена и пересчитана — полки, засечки, подписи как были)",
                    st.DimBakedRestored, st.DimBaked.Count);
            if (st.DimNative > 0)
                ed.WriteMessage("\n   размеры Revit  : {0}  (переведены в обычные размеры AutoCAD в том же виде — двигаются, копируются и вставляются в другие чертежи без потерь)",
                    st.DimNative);
            if (blocks > 0)
                ed.WriteMessage("\n   вставок блоков : {0}", blocks);
            if (hatches > 0)
                ed.WriteMessage("\n   штриховок      : {0}", hatches);

            if (st.ProxyExploded > 0)
            {
                ed.WriteMessage("\n   прокси         : {0} расчленено -> {1} примитивов{2}",
                    st.ProxyExploded, st.ProxyPieces,
                    st.ProxyOrderRestored ? " (порядок прорисовки как у исходников)" : "");
            }
            if (st.ProxyWrapped > 0)
                ed.WriteMessage("\n   прокси в блоках: {0} в {1} блоках  (перенесены целиком и уменьшены вставкой — графика, заливки и цвета как были)",
                    st.ProxyWrapped, st.ProxyWrapBlocks);

            if (st.Ole > 0)
                ed.WriteMessage("\n   OLE-вставок    : {0}  (рамка пересчитана: положение и размер)", st.Ole);

            if (st.Tables > 0)
                ed.WriteMessage("\n   таблиц         : {0}  (высота текста доведена вручную в {1} ячейках)",
                    st.Tables, st.TableCells);

            if (st.MLeaders > 0)
                ed.WriteMessage("\n   мультивыносок  : {0}  (высота текста и стрелки выверены по видимому размеру)", st.MLeaders);

            if (st.DimPointsFixed > 0)
            {
                ed.WriteMessage("\n   размеров дотянуто: {0}  (опорные точки, до которых TransformBy не достал — иначе выносные линии тянулись бы к старым координатам)",
                    st.DimPointsFixed);
                int shown = 0;
                foreach (var kv in st.DimFixKinds)
                {
                    if (shown++ >= 12) break;
                    ed.WriteMessage("\n       {0} x{1}", kv.Key, kv.Value);
                }
                if (st.DimFixKinds.Count > 12)
                    ed.WriteMessage("\n       ... и ещё видов: {0}", st.DimFixKinds.Count - 12);
            }

            if (st.DimDisassociated > 0)
                ed.WriteMessage("\n   ассоциативность снята: {0}  (иначе AutoCAD после команды пересчитывает размер по геометрии и уносит подпись)",
                    st.DimDisassociated);

            if (st.DimTextRestored > 0)
                ed.WriteMessage("\n   подписей размеров возвращено на место: {0}  (их уносил пересчёт блока, пока высота шрифта в стиле ещё не была поделена)",
                    st.DimTextRestored);

            if (st.StylesScaled.Count > 0)
            {
                ed.WriteMessage("\n   стилей текста  : {0}  (фиксированная высота шрифта x{1})",
                    st.StylesScaled.Count, factor.ToString("0.##########", ci));
                for (int i = 0; i < st.StylesScaled.Count && i < 12; i++)
                    ed.WriteMessage("\n       {0}", st.StylesScaled[i]);
                if (st.StylesScaled.Count > 12)
                    ed.WriteMessage("\n       ... и ещё {0}", st.StylesScaled.Count - 12);
            }

            if (st.StylesCloned.Count > 0)
            {
                ed.WriteMessage("\n   копий стилей   : {0}  (стиль общий с листом, блоком или невыделенным — свои объекты переведены на копию, {1} шт.)",
                    st.StylesCloned.Count, st.StylesRepointed);
                for (int i = 0; i < st.StylesCloned.Count && i < 12; i++)
                    ed.WriteMessage("\n       {0}", st.StylesCloned[i]);
                if (st.StylesCloned.Count > 12)
                    ed.WriteMessage("\n       ... и ещё {0}", st.StylesCloned.Count - 12);
            }

            if (unlockedLayers > 0)
                ed.WriteMessage("\n Слоёв разблокировано на время операции: {0} (блокировка возвращена)", unlockedLayers);

            if (st.ModelLinetypeScale != null)
            {
                ed.WriteMessage("\n LTSCALE          : не тронут — штрихи на листах (PSLTSCALE=1) как были; в модели их уменьшил масштаб аннотаций модели {0} (MSLTSCALE=1)",
                    st.ModelLinetypeScale);
                if (st.Viewports != null && st.Viewports.ModelLinetypeLayouts.Count > 0)
                    ed.WriteMessage("\n ВНИМАНИЕ: PSLTSCALE=0 на листах: {0} — штрихи типов линий в их экранах станут в {1} раз крупнее. Поставьте на них PSLTSCALE=1.",
                        string.Join(", ", st.Viewports.ModelLinetypeLayouts), divisor.ToString("0.####", ci));
            }
            else if (wholeSpace)
            {
                ed.WriteMessage("\n LTSCALE          : {0} -> {1}",
                    oldLts.ToString("0.####", ci), newLts.ToString("0.##########", ci));
                if (st.MsltscaleOff)
                {
                    ed.WriteMessage("\n   (листы с PSLTSCALE=0: {0} — штрих в их экранах задаёт LTSCALE в единицах модели; MSLTSCALE выключен, чтобы штрих модели не поделился дважды)",
                        string.Join(", ", st.Viewports.ModelLinetypeLayouts));
                    if (st.Anno != null && Math.Abs(st.Anno.CannoBefore - 1.0) > 1e-9)
                        ed.WriteMessage("\n ВНИМАНИЕ: масштаб аннотаций модели был не 1:1, а штрихи модели от него зависели (MSLTSCALE=1) — на вкладке «Модель» штрихи станут мельче/крупнее; листы как были.");
                }
            }
            else if (st.LinetypeKept)
                ed.WriteMessage("\n LTSCALE          : не тронут; масштаб типа линий выбранных тоже — они показаны на листах с PSLTSCALE=1 ({0}), штрихи там как были",
                    string.Join(", ", st.Viewports.PaperLinetypeLayouts));
            else
                ed.WriteMessage("\n LTSCALE          : не тронут (общий на весь чертёж); масштаб типа линий выбранных объектов x{0}",
                    factor.ToString("0.##########", ci));
            if (pdChanged)
                ed.WriteMessage("\n PDSIZE           : {0} -> {1}",
                    oldPd.ToString("0.####", ci), newPd.ToString("0.##########", ci));
            if (tsChanged)
                ed.WriteMessage("\n TEXTSIZE         : {0} -> {1}",
                    oldTs.ToString("0.####", ci), newTs.ToString("0.##########", ci));

            if (failures.Count > 0)
            {
                int failed = 0;
                foreach (var kv in failures) failed += kv.Value;
                ed.WriteMessage("\n НЕ ОБРАБОТАНО    : {0}", failed);
                foreach (var kv in failures)
                    ed.WriteMessage("\n   {0} x{1}", kv.Key, kv.Value);
            }

            bool annoDone = st.Anno != null;
            if (annoDone && st.Anno.Objects > 0)
                ed.WriteMessage("\n   аннотативных   : {0} из {1}  (масштабы аннотаций x{2}: на листах и в модели в том же виде)",
                    st.Anno.Objects, annotative, (1.0 / factor).ToString("0.####", ci));
            if (annotative > 0 && (!annoDone || st.Anno.Objects < annotative))
                ed.WriteMessage("\n ВНИМАНИЕ: аннотативных объектов {0} — их размер задаётся масштабом аннотаций, проверьте вручную.",
                    annoDone ? annotative - st.Anno.Objects : annotative);

            if (st.ProxyLeft > 0)
                ed.WriteMessage("\n ВНИМАНИЕ: объектов-прокси {0} оставлено как есть — они НЕ отмасштабированы. Повторите с расчленением.", st.ProxyLeft);

            if (st.ProxyFailed > 0)
                ed.WriteMessage("\n ВНИМАНИЕ: прокси не поддалось расчленению {0} — эти объекты НЕ отмасштабированы.", st.ProxyFailed);
                foreach (var kv in st.ProxyFailKinds)
                    ed.WriteMessage("\n       {0} x{1}", kv.Key, kv.Value);

            if (st.ProxyOversize.Count > 0)
            {
                int over = 0;
                foreach (var kv in st.ProxyOversize) over += kv.Value;
                ed.WriteMessage("\n ВНИМАНИЕ: расчленение прокси дало {0} обломков крупнее самого прокси — это и есть линии через весь чертёж, которых до операции не было. Масштаб тут ни при чём: под закэшированной графикой прокси лежала геометрия больше, чем было видно.", over);
                if (st.ProxyInfinite > 0)
                    ed.WriteMessage("\n   из них бесконечных (XLINE/RAY): {0} — они рисуются через весь экран при любом зуме, уменьшить их нельзя в принципе.", st.ProxyInfinite);
                int shown = 0;
                foreach (var kv in st.ProxyOversize)
                {
                    if (shown++ >= 12) break;
                    ed.WriteMessage("\n       {0} x{1}", kv.Key, kv.Value);
                }
                if (st.ProxyOversize.Count > 12)
                    ed.WriteMessage("\n       ... и ещё видов: {0}", st.ProxyOversize.Count - 12);
            }

            if (st.OleFailed > 0)
                ed.WriteMessage("\n ВНИМАНИЕ: OLE-вставок {0} не поддалось пересчёту — их размер и положение придётся править вручную (двойной щелчок -> OLESCALE).", st.OleFailed);

            if (st.StylesShared.Count > 0)
            {
                ed.WriteMessage("\n ВНИМАНИЕ: стилей текста с фиксированной высотой {0} оставлено как есть — стиль общий с листом или блоком, а завести копию не вышло. Подписи такого стиля остались прежнего размера; лечится обнулением высоты в стиле (STYLE -> Высота = 0).",
                    st.StylesShared.Count);
                for (int i = 0; i < st.StylesShared.Count && i < 12; i++)
                    ed.WriteMessage("\n       {0}", st.StylesShared[i]);
                if (st.StylesShared.Count > 12)
                    ed.WriteMessage("\n       ... и ещё {0}", st.StylesShared.Count - 12);
            }

            if (st.StylesFailed.Count > 0)
                ed.WriteMessage("\n ВНИМАНИЕ: стилей текста {0} не поддалось правке — высота шрифта в них осталась прежней.",
                    st.StylesFailed.Count);

            if (annoViewport > 0 && st.Anno == null)
                ed.WriteMessage("\n ВНИМАНИЕ: аннотативных размеров и выносок {0} — их оформление задаёт масштаб аннотаций, DIMSCALE в отрисовке не участвует. Проверьте вручную.", annoViewport);

            ViewportRescale vr = st.Viewports;
            if (vr != null)
            {
                if (vr.Total > 0)
                {
                    ed.WriteMessage("\n Видовые экраны  : {0} из {1} пересчитаны (масштаб экранов x{2}){3}",
                        vr.Scaled, vr.Total, divisor.ToString("0.####", ci),
                        vr.Mixed.Count == 0 && vr.Failed.Count == 0 ? " — на листах всё как было" : "");
                    if (vr.Locked > 0)
                        ed.WriteMessage("\n   заблокированных: {0}  (блокировка снята на время и возвращена)", vr.Locked);
                    if (vr.Clipped > 0)
                        ed.WriteMessage("\n   с подрезкой    : {0}  (граница на листе, её трогать не нужно)", vr.Clipped);
                    if (vr.Untouched > 0)
                        ed.WriteMessage("\n   не тронуты     : {0}  (выбранного в них не видно)", vr.Untouched);
                }
                if (vr.Mixed.Count > 0)
                {
                    ed.WriteMessage("\n ВНИМАНИЕ: экранов {0}, где видно и выбранное, и невыбранное, — сохранить их вид нельзя (одна часть сжалась, другая нет), оставлены как были:",
                        vr.Mixed.Count);
                    foreach (string m in vr.Mixed)
                        ed.WriteMessage("\n       {0}", m);
                }
                foreach (string f in vr.Failed)
                    ed.WriteMessage("\n ВНИМАНИЕ: экран не пересчитан — {0}", f);
                if (vr.PaperLinetypeLayouts.Count > 0 && st.ModelLinetypeScale == null && !st.LinetypeKept)
                    ed.WriteMessage("\n ВНИМАНИЕ: PSLTSCALE=1 на листах: {0} — штрихи типов линий {3}в их экранах станут в {1} раз {2}. Их длину там задаёт LTSCALE прямо в единицах листа, а масштаб типа линий пришлось пересчитать ради модели; совместить оба нельзя. Если важен вид листа — поставьте на нём PSLTSCALE=0.",
                        string.Join(", ", vr.PaperLinetypeLayouts),
                        (divisor >= 1 ? divisor : 1.0 / divisor).ToString("0.####", ci),
                        divisor >= 1 ? "мельче" : "крупнее",
                        wholeSpace ? "" : "выбранных объектов ");
            }
            else
            {
                int layouts = CountLayouts(db);
                if (layouts > 0)
                    ed.WriteMessage("\n ВНИМАНИЕ: листов в чертеже {0} — видовые экраны не пересчитаны (выключено в палитре или сжималась не модель), пересчитайте их масштаб на {1}.",
                        layouts, divisor.ToString("0.####", ci));
            }

            ed.WriteMessage(wholeSpace
                ? "\n Откат операции   : команда U (отменяет геометрию; LTSCALE — вторым U)"
                : "\n Откат операции   : команда U");
            ed.WriteMessage("\n=============================\n");
        }

        private static string FormatPoint(Point3d p, CultureInfo ci)
        {
            string z = Math.Abs(p.Z) < 1e-9 ? "0" : p.Z.ToString("0.####", ci);
            return p.X.ToString("0.####", ci) + "; " + p.Y.ToString("0.####", ci) + "; " + z;
        }

        /// <summary>Число листов, не считая Model.</summary>
        private static int CountLayouts(Database db)
        {
            try
            {
                using (Transaction tr = db.TransactionManager.StartTransaction())
                {
                    var dict = (DBDictionary)tr.GetObject(db.LayoutDictionaryId, OpenMode.ForRead);
                    int n = 0;
                    foreach (DBDictionaryEntry e in dict)
                    {
                        if (!string.Equals(e.Key, "Model", StringComparison.OrdinalIgnoreCase))
                            n++;
                    }
                    tr.Commit();
                    return n;
                }
            }
            catch (System.Exception) { return 0; }
        }
    }
}
