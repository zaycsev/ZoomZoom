using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using AcAp = Autodesk.AutoCAD.ApplicationServices.Application;

namespace DwGangsta
{
    /// <summary>
    /// «Передвинуть с видовым»: обычный перенос по модели, только листы этого не
    /// замечают. Всякий видовой экран, в котором видно переносимое, сдвигает свой вид
    /// на то же смещение — и на листе всё стоит, как стояло.
    ///
    /// Если в таком экране изображено и то, что не выбрали, команда до переноса пишет,
    /// что это и где, и спрашивает: переместить только выбранное (остальное на листах
    /// уедет) или целиком — со всем, что изображено в этих экранах.
    /// </summary>
    public class MoveCommands
    {
        public enum Choice { Selected, Whole }

        /// <summary>Разбор переноса: всё, что нужно команде после того, как база закрыта.</summary>
        public sealed class Analysis
        {
            public List<ObjectId> Moving = new List<ObjectId>();
            public List<ObjectId> Extra = new List<ObjectId>();
            /// <summary>«Выбранное»: экраны, где выбранное видно.</summary>
            public List<ObjectId> ViewsSelected = new List<ObjectId>();
            /// <summary>«Всё в видовом» (и перенос без вопроса): все экраны цепочки.</summary>
            public List<ObjectId> ViewsWhole = new List<ObjectId>();
            public Dictionary<ObjectId, string> Sheet = new Dictionary<ObjectId, string>();
            public List<string> Report = new List<string>();
            public bool Clean;
            public int Stuck;
            public int Skipped3d;
            public Point3d Start;
            public ObjectId MarkerLayer;
            public long IndexMs, PlanMs;
            /// <summary>Сколько объектов было в индексе до переноса: всё, что дальше, родилось в нём.</summary>
            public int Existed;
            /// <summary>Где был угол рамки каждого переносимого объекта — чтобы сверить, что он уехал.</summary>
            public Dictionary<ObjectId, Point2d> Starts = new Dictionary<ObjectId, Point2d>();
            /// <summary>Сколько занял каждый этап разбора — для замеров.</summary>
            public string Phases = "";
            public bool IndexBuilt;
        }

        [CommandMethod("DWGMOVEVP", CommandFlags.Modal | CommandFlags.UsePickSet | CommandFlags.Redraw)]
        public void MoveWithViewports()
        {
            Document doc = AcAp.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            Editor ed = doc.Editor;
            Database db = doc.Database;

            var prompt = new PromptSelectionOptions
            {
                MessageForAdding = "\nОбъекты для переноса вместе с видовыми экранами: "
            };
            PromptSelectionResult picked = ed.GetSelection(prompt);
            if (picked.Status != PromptStatus.OK) return;

            Analysis a;
            ProgressMeter meter = null;
            try
            {
                int shown = 0;
                a = Analyze(db, picked.Value.GetObjectIds(), (done, total) =>
                {
                    // Первый разбор чертежа — с полосой: на старом ноутбуке это заметно.
                    if (meter == null)
                    {
                        ed.WriteMessage("\nЗагрузка чертежа для дальнейшего ускорения " +
                                        "(один раз на чертёж, Esc — прервать)...");
                        meter = new ProgressMeter();
                        meter.SetLimit(100);
                        meter.Start(ZoomZoom.RescaleApp.UiName + ZoomZoom.Lang.T(
                            ": загрузка чертежа для дальнейшего ускорения",
                            ": indexing the drawing to speed up further moves"));
                    }
                    int percent = total == 0 ? 100 : (int)(100L * done / total);
                    while (shown < percent) { meter.MeterProgress(); shown++; }
                }, () => HostApplicationServices.Current.UserBreak());
            }
            finally
            {
                if (meter != null) meter.Stop();
            }

            if (a == null) { ed.WriteMessage("\nПрервано.\n"); return; }
            if (a.Moving.Count == 0)
            {
                ed.WriteMessage("\nВыберите объекты модели — на вкладке «Модель» " +
                                "или изнутри видового экрана листа.\n");
                return;
            }

            Choice choice = Choice.Whole;
            if (!a.Clean)
            {
                foreach (string line in a.Report) ed.WriteMessage("\n" + line);
                Choice? asked = Ask(ed);
                if (asked == null) { ed.WriteMessage("\nОтменено, ничего не перемещено.\n"); return; }
                choice = asked.Value;
            }

            var moving = new List<ObjectId>(a.Moving);
            if (choice == Choice.Whole) moving.AddRange(a.Extra);
            List<ObjectId> views = choice == Choice.Whole ? a.ViewsWhole : a.ViewsSelected;

            int caught, refused;
            Vector3d move = Move(ed, db, moving, a.Start, a.MarkerLayer, out caught, out refused);
            if (move.IsZeroLength()) { ed.WriteMessage("\nПеренос отменён.\n"); return; }

            // Сдвинутое на самом деле: и выбранное, и то, что поехало следом само, —
            // подписи, профиль за видом профиля, концы труб за колодцем.
            var moved = new HashSet<ObjectId>(moving);
            moved.UnionWith(ModelIndex.For(db).TouchedIds);

            int shifted = Shift(db, views, move);
            ed.Regen();
            List<string> appeared = Appeared(db, views, new List<ObjectId>(moved), move, a.Existed);

            string message = "\nПеремещено объектов: " + Number(moving.Count) +
                             (caught > 0 ? " (из них на скрытых слоях — " + Number(caught) + ")" : "") +
                             ". Видовых экранов сдвинуто вслед: " + shifted + BySheets(views, a.Sheet) + ".";
            if (choice == Choice.Whole && a.Stuck > 0)
                message += " На заблокированных слоях осталось на месте: " + Number(a.Stuck) + ".";
            if (refused > 0)
                message += " Не сдвинулось " + Count(refused, "объект", "объекта", "объектов") +
                           ": отдельно их не переносит сам AutoCAD или Civil 3D (например, профиль без вида профиля).";
            if (a.Skipped3d > 0)
                message += " Экраны с 3D-видом или перспективой не проверялись: " + a.Skipped3d + ".";
            ed.WriteMessage(message + "\n");
            foreach (string line in appeared) ed.WriteMessage(line + "\n");

            // Сверка: всё ли, что _MOVE взяла, действительно уехало на смещение.
            List<ObjectId> behind;
            foreach (string line in Behind(db, a.Starts, moving, move, out behind)) ed.WriteMessage(line + "\n");
            if (behind.Count > 0)
            {
                try { ed.SetImpliedSelection(behind.ToArray()); }
                catch (System.Exception) { }
            }
        }

        /// <summary>
        /// После переноса: что из перенесённого не уехало на смещение — осталось на месте или
        /// сдвинулось иначе. _MOVE такие объекты взяла, но их вернуло обратно: обычно это
        /// реакторы LISP-программ (текст привязан к точке, ячейка — к сетке) или сам объект
        /// так устроен. Объекты Civil 3D не сверяются: их форму Civil пересчитывает сам.
        /// </summary>
        static List<string> Behind(Database db, Dictionary<ObjectId, Point2d> starts, List<ObjectId> moving,
                                   Vector3d move, out List<ObjectId> behind)
        {
            behind = new List<ObjectId>();
            var lines = new List<string>();
            double tolerance = Math.Max(1e-4, move.Length * 1e-7);
            using (Transaction tr = db.TransactionManager.StartOpenCloseTransaction())
            {
                ModelIndex index = ModelIndex.For(db);
                index.Refresh(tr);
                var byKind = new Dictionary<string, int>();
                int still = 0;
                foreach (ObjectId id in moving)
                {
                    Point2d start;
                    int at;
                    if (!starts.TryGetValue(id, out start) || id.IsErased || !index.TryFind(id, out at)) continue;
                    ref readonly ModelIndex.Item it = ref index[at];
                    if (it.Kind == ModelIndex.Kind.None) continue;
                    double dx = it.MinX - start.X, dy = it.MinY - start.Y;
                    if (Math.Abs(dx - move.X) <= tolerance && Math.Abs(dy - move.Y) <= tolerance) continue;

                    behind.Add(id);
                    if (Math.Abs(dx) <= tolerance && Math.Abs(dy) <= tolerance) still++;
                    var ent = tr.GetObject(id, OpenMode.ForRead) as Entity;
                    string kind = ent == null ? "?" : ent.GetType().Name + " на слое «" + ent.Layer + "»";
                    byKind[kind] = byKind.TryGetValue(kind, out int n) ? n + 1 : 1;
                }
                if (behind.Count > 0)
                {
                    lines.Add("Внимание: " + Count(behind.Count, "объект", "объекта", "объектов") +
                              " _MOVE взяла, но на смещение переноса " + (OneForm(behind.Count) ? "он не уехал" : "они не уехали") +
                              " (на старом месте осталось " + Number(still) + ", сдвинулось иначе " + Number(behind.Count - still) + "):");
                    foreach (var pair in byKind.OrderByDescending(p => p.Value).Take(5))
                        lines.Add("  " + pair.Key + " — " + Number(pair.Value));
                    if (byKind.Count > 5) lines.Add("  и ещё " + Count(byKind.Count - 5, "вид", "вида", "видов"));
                    lines.Add("Обычно их возвращает обратно LISP-программа с реакторами (текст привязан к точке, " +
                              "ячейка картограммы — к сетке). Они выделены — посмотрите их командой СПИСОК (_LIST). " +
                              "Отменить перенос целиком — Ctrl+Z.");
                }
                tr.Commit();
            }
            return lines;
        }

        static Choice? Ask(Editor ed)
        {
            // Варианты уже вписаны в вопрос — AutoCAD не должен дописывать их второй раз.
            var options = new PromptKeywordOptions("\nПереместить [Выбранное/Всё в видовом] <Всё в видовом>: ")
            {
                AllowNone = true,
                AppendKeywordsToMessage = false
            };
            options.Keywords.Add("Selected", "Выбранное", "Выбранное");
            options.Keywords.Add("Whole", "Всё", "Всё в видовом");   // в ключевом слове пробелов быть не может: набирать «Вс»
            options.Keywords.Default = "Whole";
            PromptResult answer = ed.GetKeywords(options);
            if (answer.Status == PromptStatus.None) return Choice.Whole;
            if (answer.Status != PromptStatus.OK) return null;
            return answer.StringResult == "Selected" ? Choice.Selected : Choice.Whole;
        }

        /// <summary>
        /// Перенос штатной _MOVE — с её орто, привязками и смещением числом. Смещение
        /// узнаётся по метке: точка посреди первого объекта едет вместе с выбором.
        /// Что _MOVE не взяла на замороженных и выключенных слоях, переносится следом на
        /// то же смещение — иначе разморозят слой, а объект на старом месте. Остальное, что
        /// она не взяла, не трогается: значит, двигать его отдельно не дал сам AutoCAD или
        /// Civil 3D (профиль без вида профиля, подпись без хозяина), и силой его не сдвинуть
        /// без порчи. Такие считаются в refused.
        /// </summary>
        static Vector3d Move(Editor ed, Database db, List<ObjectId> moving, Point3d start, ObjectId layer,
                             out int caught, out int refused)
        {
            caught = 0;
            refused = 0;
            ObjectId markerId;
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                var marker = new DBPoint(start) { LayerId = layer };
                var ms = (BlockTableRecord)tr.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(db), OpenMode.ForWrite);
                markerId = ms.AppendEntity(marker);
                tr.AddNewlyCreatedDBObject(marker, true);
                tr.Commit();
            }

            var all = new List<ObjectId>(moving) { markerId };
            try
            {
                ed.Command("_.MOVE", SelectionSet.FromObjectIds(all.ToArray()), "",
                           Editor.PauseToken, Editor.PauseToken);
            }
            catch (System.Exception)
            {
                // Esc посреди _MOVE: метка осталась на месте — ниже это и выяснится.
            }

            ModelIndex index = ModelIndex.For(db);
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                var marker = (DBPoint)tr.GetObject(markerId, OpenMode.ForWrite);
                Vector3d move = marker.Position - start;
                marker.Erase();

                if (!move.IsZeroLength())
                {
                    Matrix3d by = Matrix3d.Displacement(move);
                    foreach (ObjectId id in moving)
                    {
                        if (index.Touched(id) || id.IsErased) continue;
                        try
                        {
                            var ent = (Entity)tr.GetObject(id, OpenMode.ForRead);
                            var l = (LayerTableRecord)tr.GetObject(ent.LayerId, OpenMode.ForRead);
                            if (!l.IsFrozen && !l.IsOff && ent.Visible) { refused++; continue; }
                            ent.UpgradeOpen();
                            ent.TransformBy(by);
                            caught++;
                        }
                        catch (System.Exception)
                        {
                            // заблокированный слой или объект, который не двигается, — остаётся на месте
                            refused++;
                        }
                    }
                }
                tr.Commit();
                return move;
            }
        }

        /// <summary>Сдвигает виды экранов на смещение переноса. Возвращает, сколько сдвинуто.</summary>
        public static int Shift(Database db, List<ObjectId> views, Vector3d move)
        {
            int shifted = 0;
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                foreach (ObjectId id in views)
                {
                    var vp = (Viewport)tr.GetObject(id, OpenMode.ForWrite);
                    if (ViewRegion.ShiftView(vp, move)) shifted++;
                }
                tr.Commit();
            }
            return shifted;
        }

        /// <summary>
        /// Разбор переноса до самого переноса: какие экраны покажут выбранное и что
        /// ещё изображено в них. Без редактора — его можно вызвать и из автотестов.
        /// null — прервано по Esc.
        /// </summary>
        public static Analysis Analyze(Database db, IList<ObjectId> picked,
                                       Action<int, int> progress, Func<bool> cancelled)
        {
            var a = new Analysis();
            ModelIndex index = ModelIndex.For(db);
            var watch = System.Diagnostics.Stopwatch.StartNew();

            // Без отмены и учёта изменений: только читаем, и так в разы быстрее.
            using (Transaction tr = db.TransactionManager.StartOpenCloseTransaction())
            {
                if (!index.IsBuilt)
                {
                    a.IndexBuilt = true;
                    if (!index.Build(tr, progress, cancelled)) return null;
                }
                else index.Refresh(tr);
                a.IndexMs = watch.ElapsedMilliseconds;
                watch.Restart();

                ObjectId modelId = SymbolUtilityServices.GetBlockModelSpaceId(db);
                var selected = new List<int>();
                foreach (ObjectId id in picked)
                {
                    // Что есть в индексе — точно в модели: открывать десятки тысяч объектов
                    // ради этого незачем. Открываются только те, у кого нет габаритов.
                    int at;
                    if (index.TryFind(id, out at) && index[at].Kind != ModelIndex.Kind.None)
                    {
                        a.Moving.Add(id);
                        selected.Add(at);
                        continue;
                    }
                    var ent = tr.GetObject(id, OpenMode.ForRead) as Entity;
                    if (ent == null || ent.OwnerId != modelId) continue;   // на листе экраны двигать незачем
                    a.Moving.Add(id);
                }
                if (a.Moving.Count == 0) return a;

                // Метка — посреди первого объекта, на его слое: он точно не заблокирован.
                var first = (Entity)tr.GetObject(a.Moving[0], OpenMode.ForRead);
                a.MarkerLayer = first.LayerId;
                a.Start = Point3d.Origin;
                foreach (int i in selected)
                {
                    ModelIndex.Item it = index[i];
                    if (it.Kind == ModelIndex.Kind.Infinite) continue;
                    a.Start = new Point3d((it.MinX + it.MaxX) / 2, (it.MinY + it.MaxY) / 2, 0);
                    break;
                }

                using (MoveScene scene = MoveScene.Open(tr, db, index))
                {
                    a.Skipped3d = scene.Skipped3d;
                    var phase = System.Diagnostics.Stopwatch.StartNew();
                    MovePlan plan = MovePlan.Build(scene, selected, cancelled);
                    if (plan == null) return null;
                    a.Phases += "план " + phase.ElapsedMilliseconds + " (" + plan.Phases + ")";
                    phase.Restart();

                    // Где что лежит до переноса — для сверки после. Объекты Civil не сверяются:
                    // их форму Civil 3D пересчитывает сам.
                    foreach (List<int> list in new[] { selected, plan.Extra })
                        foreach (int i in list)
                        {
                            ref readonly ModelIndex.Item it = ref index[i];
                            if (it.Civil || it.Kind == ModelIndex.Kind.Infinite) continue;
                            a.Starts[it.Id] = new Point2d(it.MinX, it.MinY);
                        }

                    foreach (MoveScene.View v in scene.Views) a.Sheet[v.Id] = v.Layout;
                    foreach (MoveScene.View v in plan.Shown) a.ViewsSelected.Add(v.Id);
                    foreach (MoveScene.View v in plan.Whole) a.ViewsWhole.Add(v.Id);
                    a.Clean = plan.Clean;
                    if (!plan.Clean)
                    {
                        foreach (int i in plan.Extra) a.Extra.Add(index[i].Id);
                        a.Stuck = plan.Stuck.Count;
                        a.Report = Report(scene, plan, selected.Count);
                    }
                    a.Phases += ", отчёт " + phase.ElapsedMilliseconds;
                    phase.Restart();

                }
                a.PlanMs = watch.ElapsedMilliseconds;
                a.Existed = index.Count;
                tr.Commit();
            }
            return a;
        }

        /// <summary>
        /// После переноса: что стало видно на листах, чего там раньше не было. Место
        /// назначения известно только после второй точки, поэтому заранее этого не узнать:
        /// перенесли разрез туда, где уже что-то нарисовано, или в кадр чужого экрана, —
        /// и на листе молча появилось лишнее. Пусто — листы чисты.
        ///
        /// Запоминать заранее, что где было видно, не нужно: слои за время команды не
        /// менялись, а всё сдвинулось на одно смещение. Сдвинутый экран раньше смотрел на
        /// кадр минус смещение — неперенесённое, что видно сейчас, было ли видно там?
        /// Несдвинутый экран: перенесённое, что видно в нём сейчас, — было ли видно на
        /// старом месте, то есть в кадре плюс смещение?
        /// </summary>
        public static List<string> Appeared(Database db, List<ObjectId> shifted, List<ObjectId> moving, Vector3d move)
        {
            return Appeared(db, shifted, moving, move, int.MaxValue);
        }

        /// <param name="existed">
        /// Сколько объектов было в индексе до переноса. Что родилось во время него — не
        /// «чужое на листе», а побочный продукт самого переноса: Civil 3D, сдвигая
        /// профилирование, стирает его и создаёт заново уже на новом месте.
        /// </param>
        public static List<string> Appeared(Database db, List<ObjectId> shifted, List<ObjectId> moving, Vector3d move,
                                            int existed)
        {
            var lines = new List<string>();
            var bySheet = new Dictionary<string, HashSet<int>>();
            using (Transaction tr = db.TransactionManager.StartOpenCloseTransaction())
            {
                ModelIndex index = ModelIndex.For(db);
                index.Refresh(tr);
                var moved = new List<int>();
                var isMoved = new bool[index.Count];
                foreach (ObjectId id in moving)
                {
                    int at;
                    if (!index.TryFind(id, out at)) continue;
                    moved.Add(at);
                    isMoved[at] = true;
                }

                using (MoveScene scene = MoveScene.Open(tr, db, index))
                {
                    foreach (MoveScene.View v in scene.Views)
                    {
                        HashSet<int> set;
                        if (!bySheet.TryGetValue(v.Layout, out set)) set = new HashSet<int>();
                        if (shifted.Contains(v.Id))
                        {
                            using (ViewShape was = v.Shape.Offset(-move.X, -move.Y))
                                foreach (int i in MovePlan.VisibleIn(scene, v, null))
                                    if (i < existed && !isMoved[i] && scene.Classify(i, v, was) != MoveScene.Seen.Visible) set.Add(i);
                        }
                        else
                        {
                            using (ViewShape was = v.Shape.Offset(move.X, move.Y))
                                foreach (int i in moved)
                                    if (scene.Visible(i, v) && scene.Classify(i, v, was) != MoveScene.Seen.Visible) set.Add(i);
                        }
                        if (set.Count > 0) bySheet[v.Layout] = set;
                    }
                    if (bySheet.Count > 0)
                    {
                        lines.Add("Внимание: на листах появилось то, чего там раньше не было — " +
                                  "на новом месте в модели уже что-то лежало или перенесённое попало в кадр другого экрана:");
                        foreach (var pair in bySheet)
                            lines.Add("  «" + pair.Key + "»: " + Count(pair.Value.Count, "объект", "объекта", "объектов") +
                                      " (" + Layers(scene, pair.Value) + ").");
                        lines.Add("Отменить перенос целиком — Ctrl+Z (или _U).");
                    }
                }
                tr.Commit();
            }
            return lines;
        }

        // ------------------------------------------------------------- отчёт

        /// <summary>
        /// Отчёт для командной строки: что ещё изображено в экранах рядом с выбранным,
        /// и что именно случится с чертежом при каждом ответе.
        /// </summary>
        static List<string> Report(MoveScene scene, MovePlan plan, int selectedCount)
        {
            var lines = new List<string>();

            lines.Add("Вместе с выбранным в области видимости видовых экранов есть ещё много объектов:");
            foreach (var sheet in plan.Holding.Where(v => plan.Around.ContainsKey(v)).GroupBy(v => v.Layout))
            {
                var visible = new HashSet<int>();
                var hidden = new HashSet<int>();
                foreach (MoveScene.View v in sheet)
                {
                    visible.UnionWith(plan.Around[v].Visible);
                    hidden.UnionWith(plan.Around[v].Hidden);
                }
                hidden.ExceptWith(visible);
                string line = "  лист «" + sheet.Key + "», " + Count(sheet.Count(), "экран", "экрана", "экранов") + ": ";
                if (visible.Count > 0)
                    line += "видно " + Count(visible.Count, "объект", "объекта", "объектов") + " (" + Layers(scene, visible) + ")";
                if (hidden.Count > 0)
                    line += (visible.Count > 0 ? "; " : "") + "скрыто " + Number(hidden.Count) +
                            " — слои заморожены или выключены (" + Layers(scene, hidden) + ")";
                lines.Add(line + ".");
            }

            // «Выбранное»: что уедет со своего места на листах.
            var leaving = new HashSet<int>();
            foreach (MoveScene.View v in plan.Shown)
            {
                MovePlan.Others others;
                if (plan.Around.TryGetValue(v, out others)) leaving.UnionWith(others.Visible);
            }
            lines.Add("");
            lines.Add("Выбранное — переместить только выбранное (" + Count(selectedCount, "объект", "объекта", "объектов") +
                      ") и видовые экраны (" + plan.Shown.Count + ").");
            if (plan.Shown.Count > 0)
                lines.Add("    Видовые экраны сдвинутся вслед за ним: " + SheetsOf(plan.Shown) + ". " +
                          (leaving.Count > 0
                              ? "Всё остальное, что в них видно, — " + Count(leaving.Count, "объект", "объекта", "объектов") +
                                " — на листах уедет со своего места."
                              : "Остальное в них сейчас скрыто слоями, но, когда слои включат, окажется не на месте."));
            else
                lines.Add("    Выбранное нигде не видно — экраны не сдвинутся.");

            // «Всё в видовом»: что ещё переместится, по листам.
            int visibleExtra = plan.Extra.Count - plan.ExtraHidden;
            lines.Add("Всё в видовом — переместить выбранное вместе со всем, что в области видимости видовых экранов выбранного: ещё " +
                      Count(plan.Extra.Count, "объект", "объекта", "объектов") +
                      (plan.ExtraHidden > 0 ? " (видимых " + Number(visibleExtra) + ", скрытых слоями " + Number(plan.ExtraHidden) + ")" : "") + ".");
            var bySheet = plan.Whole.GroupBy(v => v.Layout).Select(g =>
            {
                var items = new HashSet<int>();
                foreach (MoveScene.View v in g)
                {
                    List<int> list;
                    if (plan.ExtraIn.TryGetValue(v, out list)) items.UnionWith(list);
                }
                items.ExceptWith(plan.Stuck);
                return "«" + g.Key + "» — " + Count(g.Count(), "экран", "экрана", "экранов") +
                       (items.Count > 0 ? ", " + Count(items.Count, "объект", "объекта", "объектов") : "");
            });
            lines.Add("    " + (OneForm(plan.Whole.Count) ? "Сдвинется " : "Сдвинутся ") +
                      Count(plan.Whole.Count, "экран", "экрана", "экранов") + ": " +
                      string.Join("; ", bySheet) + ".");
            int chained = plan.Whole.Count - plan.Holding.Count;
            if (chained > 0)
                lines.Add("    Из них " + Count(chained, "экран", "экрана", "экранов") +
                          " — по цепочке: в них изображено то, что переместится вместе с выбранным.");
            lines.Add("    Листы останутся такими же, как были, — и сейчас, и когда скрытые слои включат.");
            if (plan.Stuck.Count > 0)
            {
                var stuck = new HashSet<int>(plan.Stuck);
                lines.Add("    Кроме " + Count(stuck.Count, "объекта", "объектов", "объектов") +
                          " на заблокированных слоях (" + Layers(scene, stuck) +
                          "): их не переместить — разблокируйте слои, иначе на листах они останутся на старом месте.");
            }
            lines.Add("Enter — «Всё в видовом», Esc — отменить.");
            return lines;
        }

        /// <summary>««Июль» 1, «Август» 2».</summary>
        static string SheetsOf(IEnumerable<MoveScene.View> views)
        {
            return string.Join(", ", views.GroupBy(v => v.Layout)
                                          .Select(g => "«" + g.Key + "» " + g.Count()));
        }

        /// <summary>«Колонны_Март 800, Оси 400, ещё 2 слоя — 54».</summary>
        static string Layers(MoveScene scene, ICollection<int> items)
        {
            var byLayer = items.GroupBy(i => scene.LayerName(i))
                               .Select(g => new { Name = g.Key, Count = g.Count() })
                               .OrderByDescending(g => g.Count).ThenBy(g => g.Name)
                               .ToList();
            const int Shown = 3;
            var parts = byLayer.Take(Shown).Select(g => g.Name + " " + Number(g.Count)).ToList();
            if (byLayer.Count > Shown)
            {
                int rest = byLayer.Skip(Shown).Sum(g => g.Count);
                parts.Add("ещё " + Count(byLayer.Count - Shown, "слой", "слоя", "слоёв") + " — " + Number(rest));
            }
            return "по слоям: " + string.Join(", ", parts);
        }

        static string BySheets(List<ObjectId> views, Dictionary<ObjectId, string> sheet)
        {
            if (views.Count == 0) return "";
            var groups = views.Where(sheet.ContainsKey).GroupBy(v => sheet[v])
                              .Select(g => "«" + g.Key + "» " + g.Count());
            return " (" + string.Join(", ", groups) + ")";
        }

        static string Number(int n)
        {
            var format = new NumberFormatInfo { NumberGroupSeparator = " ", NumberGroupSizes = new[] { 3 } };
            return n.ToString("#,0", format);
        }

        /// <summary>Число, с которым глагол в единственном числе: 1, 21, 101 — «сдвинется», но 11 — «сдвинутся».</summary>
        static bool OneForm(int n) => n % 10 == 1 && n % 100 != 11;

        /// <summary>«1 экран», «3 экрана», «12 экранов».</summary>
        static string Count(int n, string one, string few, string many)
        {
            int last2 = n % 100, last = n % 10;
            string word = last2 >= 11 && last2 <= 14 ? many : last == 1 ? one : last >= 2 && last <= 4 ? few : many;
            return Number(n) + " " + word;
        }
    }
}
