using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

namespace DwGangsta
{
    /// <summary>
    /// Разводит одноимённое, но разное: стили текста и размеров, блоки, слои и типы линий.
    ///
    /// AutoCAD плодит стили с автоматическими именами — «ISOCPEUR_5», «2_5__0_01__2», —
    /// и в каждом чертеже под одним и тем же именем оказывается своё. При сборке в один
    /// файл таблица стилей одна: первый чертёж занимает имя, остальные к нему приводятся,
    /// и надписи меняют размер. Особенно заметно на стилях с фиксированной высотой: она
    /// теряется, в дело идёт высота из самого объекта, и размеры на чертеже становятся
    /// огромными.
    ///
    /// То же и с блоками: под именем «Узел 1» в двух чертежах лежит совсем разное,
    /// а в сборке блок один — и второй чертёж принимает вид первого.
    ///
    /// И то же со слоями и типами линий: «Оси» у одного красные сплошные, у другого
    /// синие штриховые. Таблица слоёв в файле одна, и объекты второго чертежа
    /// перекрасились бы в чужой цвет.
    ///
    /// Поэтому перед переносом запись, которая в этом чертеже не такая, как уже лежащая
    /// в сборке под тем же именем, переименовывается — в копии чертежа в памяти, сам файл
    /// не трогается. Одинаковые остаются общими, чтобы сборка не распухала.
    /// </summary>
    public class StyleNames
    {
        /// <summary>
        /// Слепки блоков сборки, посчитанные один раз. Блок описывают его объекты, читать
        /// их заново на каждый чертёж — дорого, а в сборке блоки только добавляются.
        /// </summary>
        readonly Dictionary<string, string> _blockMarks = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Приводит имена стилей и блоков исходного чертежа так, чтобы перенос ничего
        /// не переписал. Возвращает, сколько записей пришлось переименовать.
        /// </summary>
        public int MakeUnique(Database source, Database target, string hint)
        {
            int renamed = 0;
            renamed += Resolve(source, target, hint, source.TextStyleTableId, target.TextStyleTableId, TextStyleMark, null);
            renamed += Resolve(source, target, hint, source.DimStyleTableId, target.DimStyleTableId, DimStyleMark, null);
            renamed += Resolve(source, target, hint, source.BlockTableId, target.BlockTableId, BlockMark, _blockMarks);
            renamed += Resolve(source, target, hint, source.LayerTableId, target.LayerTableId, LayerMark, null);
            renamed += Resolve(source, target, hint, source.LinetypeTableId, target.LinetypeTableId, LinetypeMark, null);
            return renamed;
        }

        delegate string Mark(SymbolTableRecord record, Transaction tr);

        static int Resolve(Database source, Database target, string hint,
                           ObjectId sourceTableId, ObjectId targetTableId, Mark mark,
                           Dictionary<string, string> cache)
        {
            int renamed = 0;
            try
            {
                var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var targetMarks = cache ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

                using (Transaction tr = target.TransactionManager.StartTransaction())
                {
                    var table = (SymbolTable)tr.GetObject(targetTableId, OpenMode.ForRead);
                    foreach (ObjectId id in table)
                    {
                        var record = (SymbolTableRecord)tr.GetObject(id, OpenMode.ForRead);
                        taken.Add(record.Name);
                        if (!targetMarks.ContainsKey(record.Name)) targetMarks[record.Name] = mark(record, tr);
                    }
                    tr.Commit();
                }

                using (Transaction tr = source.TransactionManager.StartTransaction())
                {
                    var table = (SymbolTable)tr.GetObject(sourceTableId, OpenMode.ForRead);

                    var mine = new List<ObjectId>();
                    foreach (ObjectId id in table) mine.Add(id);
                    foreach (ObjectId id in mine)
                    {
                        var record = (SymbolTableRecord)tr.GetObject(id, OpenMode.ForRead);
                        if (Service(record)) continue;

                        string theirs;
                        if (!targetMarks.TryGetValue(record.Name, out theirs)) { taken.Add(record.Name); continue; }
                        if (theirs == mark(record, tr)) continue;   // такой же — пусть будет общим

                        string free = FreeName(record.Name, hint, taken);
                        try
                        {
                            record.UpgradeOpen();
                            record.Name = free;
                            taken.Add(free);
                            renamed++;
                        }
                        catch (System.Exception ex)
                        {
                            Trace.Write("  стиль «" + record.Name + "» не переименован: " + ex.Message);
                        }
                    }
                    tr.Commit();
                }
            }
            catch (System.Exception ex)
            {
                Trace.Write("  стили не разведены: " + ex.Message);
            }
            return renamed;
        }

        /// <summary>
        /// Записи, которые переименовывать нельзя и не нужно: пространства модели и листов,
        /// внешние ссылки (их содержимое живёт в чужом файле) и безымянные блоки — имена
        /// вида «*D12» AutoCAD раздаёт сам и сам же разводит при переносе.
        /// </summary>
        static bool Service(SymbolTableRecord record)
        {
            // Записи из внешней ссылки живут в чужом файле — их имена не наши.
            if (record.IsDependent) return true;

            var block = record as BlockTableRecord;
            if (block != null)
            {
                if (block.IsLayout || block.IsFromExternalReference || block.IsFromOverlayReference) return true;
                return block.Name.StartsWith("*", StringComparison.Ordinal);
            }

            // Слой «0» в чертеже один и тот же по определению, а на «Defpoints» держится
            // уговор: он не печатается. Переименуешь — и точки размеров полезут на печать.
            var layer = record as LayerTableRecord;
            if (layer != null) return Named(layer.Name, "0", "Defpoints");

            // «По слою», «по блоку» и «сплошная» — служебные, их имена трогать нельзя.
            var linetype = record as LinetypeTableRecord;
            if (linetype != null) return Named(linetype.Name, "ByLayer", "ByBlock", "Continuous");

            return false;
        }

        static bool Named(string name, params string[] reserved)
        {
            foreach (string one in reserved)
            {
                if (string.Equals(name, one, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        /// <summary>
        /// Слепок слоя — всё, чем он задаёт вид: цвет, тип линий, вес, печать и то,
        /// включён ли он. Два чертежа держат под именем «Оси» совсем разные слои,
        /// и в общей таблице второй принял бы вид первого.
        /// </summary>
        static string LayerMark(SymbolTableRecord record, Transaction tr)
        {
            var layer = (LayerTableRecord)record;

            string linetype = "—";
            if (!layer.LinetypeObjectId.IsNull)
            {
                var type = tr.GetObject(layer.LinetypeObjectId, OpenMode.ForRead) as LinetypeTableRecord;
                if (type != null) linetype = type.Name;
            }

            return layer.Color.ColorNameForDisplay + "|" + linetype + "|" + layer.LineWeight +
                   "|" + (layer.IsPlottable ? "печать" : "без печати") +
                   "|" + (layer.IsOff ? "выключен" : "включён") +
                   "|" + (layer.IsFrozen ? "заморожен" : "разморожен");
        }

        /// <summary>Слепок типа линий — его штрихи: под одним именем бывают разные.</summary>
        static string LinetypeMark(SymbolTableRecord record, Transaction tr)
        {
            var type = (LinetypeTableRecord)record;

            var marks = new StringBuilder();
            marks.Append(type.PatternLength.ToString("0.###", CultureInfo.InvariantCulture));
            for (int i = 0; i < type.NumDashes; i++)
            {
                marks.Append('|');
                marks.Append(type.DashLengthAt(i).ToString("0.###", CultureInfo.InvariantCulture));
            }
            return marks.ToString();
        }

        /// <summary>
        /// Слепок блока — по его содержимому. Два чертежа сплошь и рядом держат под одним
        /// именем совсем разные блоки: в сборке второй принял бы вид первого, потому что
        /// таблица блоков одна. Слепок это ловит: состав объектов, их слои и габариты.
        /// </summary>
        static string BlockMark(SymbolTableRecord record, Transaction tr)
        {
            var block = (BlockTableRecord)record;
            var parts = new List<string>();

            try
            {
                foreach (ObjectId id in block)
                {
                    var ent = tr.GetObject(id, OpenMode.ForRead) as Entity;
                    if (ent == null) continue;

                    var piece = new StringBuilder();
                    piece.Append(ent.GetRXClass().DxfName).Append(':').Append(ent.Layer).Append(':');

                    Extents3d ext;
                    if (EntityBounds.TryGet(ent, out ext))
                    {
                        piece.Append(string.Format(CultureInfo.InvariantCulture, "{0:0.###},{1:0.###},{2:0.###},{3:0.###}",
                            ext.MinPoint.X, ext.MinPoint.Y, ext.MaxPoint.X, ext.MaxPoint.Y));
                    }

                    var attribute = ent as AttributeDefinition;
                    if (attribute != null) piece.Append(':').Append(attribute.Tag);

                    var text = ent as DBText;
                    if (text != null) piece.Append(':').Append(text.TextString);

                    parts.Add(piece.ToString());
                }
            }
            catch (System.Exception ex)
            {
                Trace.Write("  блок «" + block.Name + "» не прочитан: " + ex.Message);
                return "?" + block.Name;   // не сравнить — считаем своим, чтобы не смешать
            }

            // Порядок объектов в блоке ни на что не влияет — сравниваем состав.
            parts.Sort(StringComparer.Ordinal);
            return string.Join("|", parts.ToArray());
        }

        /// <summary>Имя вида «ISOCPEUR_5 (Опалубка дна)», свободное и в чертеже, и в сборке.</summary>
        internal static string FreeName(string name, string hint, HashSet<string> taken)
        {
            string tail = Clean(hint);
            string candidate = name + " (" + tail + ")";
            if (!taken.Contains(candidate)) return candidate;

            for (int index = 2; ; index++)
            {
                string numbered = name + " (" + tail + " " + index + ")";
                if (!taken.Contains(numbered)) return numbered;
            }
        }

        /// <summary>
        /// Приставка к имени стиля: без знаков, запрещённых в таблицах символов
        /// (запятые в именах файлов встречаются сплошь и рядом), и покороче —
        /// имя целиком ограничено 255 знаками.
        /// </summary>
        static string Clean(string hint)
        {
            const string forbidden = "<>/\\\":;?*|,=`";
            var text = new StringBuilder(hint.Length);
            foreach (char c in hint) text.Append(forbidden.IndexOf(c) >= 0 ? ' ' : c);

            string result = text.ToString().Trim();
            if (result.Length > 60) result = result.Substring(0, 60).Trim();
            return result.Length > 0 ? result : "чертёж";
        }

        static string TextStyleMark(SymbolTableRecord record, Transaction tr)
        {
            var style = (TextStyleTableRecord)record;
            var mark = new StringBuilder();
            Add(mark, style.FileName);
            Add(mark, style.BigFontFileName);
            Add(mark, style.TextSize);
            Add(mark, style.XScale);
            Add(mark, style.ObliquingAngle);
            Add(mark, style.IsVertical ? 1 : 0);
            Add(mark, style.IsShapeFile ? 1 : 0);
            return mark.ToString();
        }

        static string DimStyleMark(SymbolTableRecord record, Transaction tr)
        {
            var style = (DimStyleTableRecord)record;
            var mark = new StringBuilder();

            // Всё, от чего зависит, каким размер выйдет на чертеже.
            Add(mark, style.Dimscale);
            Add(mark, style.Dimtxt);
            Add(mark, style.Dimasz);
            Add(mark, style.Dimexe);
            Add(mark, style.Dimexo);
            Add(mark, style.Dimgap);
            Add(mark, style.Dimdli);
            Add(mark, style.Dimlfac);
            Add(mark, style.Dimdec);
            Add(mark, style.Dimtad);
            Add(mark, style.Dimjust);
            Add(mark, style.Dimtih ? 1 : 0);
            Add(mark, style.Dimtoh ? 1 : 0);
            Add(mark, style.Dimclrd.ColorIndex);
            Add(mark, style.Dimclre.ColorIndex);
            Add(mark, style.Dimclrt.ColorIndex);

            string textStyle = "";
            if (!style.Dimtxsty.IsNull)
            {
                var used = tr.GetObject(style.Dimtxsty, OpenMode.ForRead) as TextStyleTableRecord;
                if (used != null) textStyle = used.Name;
            }
            Add(mark, textStyle);
            return mark.ToString();
        }

        static void Add(StringBuilder mark, string value)
        {
            mark.Append(value == null ? "" : value.ToLowerInvariant()).Append('|');
        }

        static void Add(StringBuilder mark, double value)
        {
            mark.Append(value.ToString("0.######", CultureInfo.InvariantCulture)).Append('|');
        }

        static void Add(StringBuilder mark, int value)
        {
            mark.Append(value.ToString(CultureInfo.InvariantCulture)).Append('|');
        }
    }
}
