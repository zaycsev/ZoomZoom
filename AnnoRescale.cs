using System;
using System.Collections.Generic;
using System.Globalization;
using Autodesk.AutoCAD.DatabaseServices;

namespace ZoomZoom
{
    /// <summary>
    /// Аннотативные объекты при Зум-зуме всего пространства модели.
    ///
    /// Размер аннотативного текста, размера, выноски, блока задаёт не DIMSCALE
    /// и не высота, а масштаб аннотаций: на листе — высота в мм, в модели —
    /// она же, делённая на масштаб (1:200 — в 200 раз крупнее). Геометрия
    /// сжалась в 1000 раз, а масштаб остался 1:200 — оформление осталось
    /// прежнего размера, «размеры остались как были».
    ///
    /// Поэтому каждый масштаб аннотаций, которым пользуется модель, меняется
    /// на масштаб в 1/factor раз крупнее (1:200 -> 5:1): у аннотативных
    /// объектов модели, у видовых экранов, у CANNOSCALE. Масштабы,
    /// которых в чертеже нет, заводятся. Лист не масштабируется — его
    /// аннотативные объекты не трогаем.
    /// </summary>
    internal sealed class AnnoRescale
    {
        private readonly Database _db;
        private readonly double _factor;
        private readonly Dictionary<string, AnnotationScale> _map =
            new Dictionary<string, AnnotationScale>(StringComparer.OrdinalIgnoreCase);

        public int Objects;              // аннотативных объектов модели переведено
        public double CannoBefore = 1.0; // значение CANNOSCALE до (для штрихов модели)
        public string CannoChange;       // «1:1 -> 1000:1» для отчёта

        public AnnoRescale(Database db, double factor)
        {
            _db = db;
            _factor = factor;
        }

        /// <summary>Масштаб в 1/factor раз крупнее данного (заводится при нужде).</summary>
        public AnnotationScale Map(AnnotationScale old)
        {
            if (old == null)
                return null;
            AnnotationScale found;
            if (_map.TryGetValue(old.Name, out found))
                return found;

            found = ScaleOf(old.Scale / _factor);
            _map[old.Name] = found;
            return found;
        }

        /// <summary>Масштаб аннотаций с данным значением (бумага/чертёж); нет — заводится.</summary>
        public AnnotationScale ScaleOf(double want)
        {
            var ci = CultureInfo.InvariantCulture;
            string name = want >= 1
                ? want.ToString("0.######", ci) + ":1"
                : "1:" + (1.0 / want).ToString("0.######", ci);

            // Масштабов с одним значением в чертеже бывает несколько (например,
            // 1:1 называется и «1:1», и «0.944»). Берём тот, чьё название
            // говорит, что это за масштаб: «1:1», потом «1:1_XREF» и т. п.,
            // и только потом — что попадётся.
            AnnotationScale found = null, named = null, alike = null;
            ObjectContextCollection scales =
                _db.ObjectContextManager.GetContextCollection("ACDB_ANNOTATIONSCALES");
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (ObjectContext oc in scales)
            {
                var a = oc as AnnotationScale;
                if (a == null)
                    continue;
                names.Add(a.Name);
                if (Math.Abs(a.Scale - want) > 1e-9 * want)
                    continue;
                if (found == null)
                    found = a;
                string n = (a.Name ?? "").Replace(" ", "");
                if (named == null && string.Equals(n, name, StringComparison.OrdinalIgnoreCase))
                    named = a;
                else if (alike == null && n.StartsWith(name, StringComparison.OrdinalIgnoreCase))
                    alike = a;
            }
            found = named ?? alike ?? found;
            if (found == null)
            {
                while (names.Contains(name))
                    name += " ZZ";
                found = new AnnotationScale
                {
                    Name = name,
                    PaperUnits = want >= 1 ? want : 1.0,
                    DrawingUnits = want >= 1 ? 1.0 : 1.0 / want,
                };
                scales.AddContext(found);
                // В коллекции живёт свой экземпляр — берём его.
                foreach (ObjectContext oc in scales)
                {
                    var a = oc as AnnotationScale;
                    if (a != null && string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase))
                    {
                        found = a;
                        break;
                    }
                }
            }
            return found;
        }

        /// <summary>
        /// Штрихи модели при MSLTSCALE=0: включить MSLTSCALE и поставить
        /// масштаб аннотаций модели 1/factor (1000:1) — штрих модели станет в
        /// 1/factor раз мельче, LTSCALE и листы не меняются. Можно, только если
        /// CANNOSCALE и так был 1:1 или в модели нет аннотативных объектов —
        /// иначе их вид задаёт CANNOSCALE, и трогать его нельзя.
        /// </summary>
        public bool TakeOverLinetypes(int annotativeInModel)
        {
            if (Math.Abs(CannoBefore - 1.0) >= 1e-9 && annotativeInModel > 0)
                return false;
            try
            {
                AnnotationScale before = _db.Cannoscale;
                AnnotationScale next = ScaleOf(1.0 / _factor);
                if (next == null)
                    return false;
                _db.Cannoscale = next;
                Autodesk.AutoCAD.ApplicationServices.Application.SetSystemVariable("MSLTSCALE", 1);
                CannoChange = (CannoChange != null ? CannoChange.Split(' ')[0] : before.Name) + " -> " + next.Name;
                return true;
            }
            catch (System.Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// Аннотативные объекты модели (из обработанных) и CANNOSCALE. У
        /// объекта сначала добавляются новые масштабы, потом снимаются старые:
        /// объект без масштаба аннотаций AutoCAD не допускает.
        /// </summary>
        public void ApplyToModel(Transaction tr, IEnumerable<ObjectId> ids)
        {
            List<AnnotationScale> all = AllScales(_db);

            foreach (ObjectId id in ids)
            {
                Entity ent;
                try { ent = tr.GetObject(id, OpenMode.ForRead, false, true) as Entity; }
                catch (System.Exception) { continue; }
                if (ent == null || ent.IsErased)
                    continue;
                try
                {
                    if (ent.Annotative != AnnotativeStates.True)
                        continue;
                }
                catch (System.Exception) { continue; }

                try
                {
                    var had = new List<AnnotationScale>();
                    foreach (AnnotationScale a in all)
                        if (ent.HasContext(a))
                            had.Add(a);
                    if (had.Count == 0)
                        continue;

                    var now = new List<AnnotationScale>();
                    foreach (AnnotationScale a in had)
                        now.Add(Map(a));

                    // Ограничение: новую запись масштаба мультивыноски AutoCAD
                    // раскладывает заново по стилю — текст встаёт в стандартное
                    // место у конца полки. Если в каком-то масштабе текст был
                    // сдвинут с этого места, сдвиг теряется (бывает до 9 мм на
                    // листе). Вернуть его через TextLocation, переключая CANNOSCALE,
                    // нельзя: свойство пишет не в ту запись масштаба.
                    ent.UpgradeOpen();
                    foreach (AnnotationScale a in now)
                        if (!ent.HasContext(a))
                            ent.AddContext(a);
                    foreach (AnnotationScale a in had)
                        if (!now.Exists(n => string.Equals(n.Name, a.Name, StringComparison.OrdinalIgnoreCase)))
                            ent.RemoveContext(a);

                    var dim = ent as Dimension;
                    if (dim != null)
                        dim.RecomputeDimensionBlock(true);
                    Objects++;
                }
                catch (System.Exception) { }
            }

            try
            {
                AnnotationScale canno = _db.Cannoscale;
                if (canno != null)
                {
                    CannoBefore = canno.Scale;
                    AnnotationScale next = Map(canno);
                    if (next != null)
                    {
                        _db.Cannoscale = next;
                        CannoChange = canno.Name + " -> " + next.Name;
                    }
                }
            }
            catch (System.Exception) { }
        }

        /// <summary>Все масштабы аннотаций чертежа.</summary>
        public static List<AnnotationScale> AllScales(Database db)
        {
            var all = new List<AnnotationScale>();
            ObjectContextCollection scales =
                db.ObjectContextManager.GetContextCollection("ACDB_ANNOTATIONSCALES");
            foreach (ObjectContext oc in scales)
            {
                var a = oc as AnnotationScale;
                if (a != null)
                    all.Add(a);
            }
            return all;
        }

        /// <summary>
        /// У объекта есть хоть один масштаб аннотаций — значит, ApplyToModel его
        /// переведёт. Бывают аннотативные без единого масштаба (так выгружает
        /// Revit): их видимый размер — их собственный, его масштабирует TransformBy.
        /// </summary>
        public static bool HasAnyScale(Entity ent, List<AnnotationScale> all)
        {
            foreach (AnnotationScale a in all)
                if (ent.HasContext(a))
                    return true;
            return false;
        }

        /// <summary>Масштаб аннотаций пересчитанного видового экрана.</summary>
        public void ApplyToViewport(Viewport vp)
        {
            try
            {
                AnnotationScale next = Map(vp.AnnotationScale);
                if (next != null)
                    vp.AnnotationScale = next;
            }
            catch (System.Exception) { }
        }
    }
}
