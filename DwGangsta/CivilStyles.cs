using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;
using Autodesk.AutoCAD.DatabaseServices;

namespace DwGangsta
{
    /// <summary>
    /// Стили и имена объектов Civil 3D при сборке нескольких чертежей в один.
    ///
    /// Поверхность, трасса, линия характерных точек ссылаются на свой стиль. Обычный
    /// перенос объектов стиль толком не везёт: Civil 3D заводит объекту копию стиля,
    /// которую никуда не кладёт, — при сохранении сборки она выпадает, и объект остаётся
    /// без стиля: на чертеже его не видно, свойства не открываются. А у стиля, который
    /// всё-таки доехал, теряются вложенные стили — подписей, таблиц легенд.
    ///
    /// Поэтому стили везём отдельно, штатным переносом стилей Civil 3D, — он кладёт их
    /// на место вместе со всем вложенным. А после переноса объектов переводим каждый
    /// на стиль сборки с тем же именем.
    ///
    /// Заодно разводятся однофамильцы — так же, как StyleNames разводит слои и блоки.
    /// Стиль, который в сборке уже есть под тем же именем, но настроен иначе, едет
    /// под своим именем: «Стандартный (Ис картограмма)», — иначе объекты этого чертежа
    /// приняли бы чужой вид. Такой же — остаётся общим. Поверхности с занятым именем
    /// тоже получают приписку с именем файла: шесть «Откосов» в одной сборке не различить.
    ///
    /// Всё это делается в копии исходника в памяти, сам файл не трогается.
    ///
    /// Сборки Civil 3D подключаются отражением: плагин работает и в чистом AutoCAD,
    /// там этот класс просто ничего не делает.
    /// </summary>
    public class CivilStyles
    {
        static bool _looked;
        static Assembly _civil;
        static Type _entityType;            // Autodesk.Civil.DatabaseServices.Entity
        static Type _styleType;             // Autodesk.Civil.DatabaseServices.Styles.StyleBase
        static Type _surfaceType;           // Autodesk.Civil.DatabaseServices.Surface
        static PropertyInfo _styleId;
        static PropertyInfo _styleName;
        static PropertyInfo _styleRename;
        static PropertyInfo _surfaceName;
        static PropertyInfo _surfaceRename;
        static MethodInfo _exportTo;        // StyleBase.ExportTo(ids, база, как быть с совпадением)
        static object _keepExisting;        // совпавший вложенный стиль оставить тем, что в сборке
        static MethodInfo _civilDocument;   // CivilDocument.GetCivilDocument(база)
        static PropertyInfo _stylesRoot;    // CivilDocument.Styles

        /// <summary>Стили, которые лежат в сборке: «тип стиля|имя» → стиль.</summary>
        readonly Dictionary<string, ObjectId> _known = new Dictionary<string, ObjectId>(StringComparer.Ordinal);

        /// <summary>
        /// Слепки стилей, уже отправленных в сборку, — снятые с исходника, который
        /// стиль привёз. Сличать с самой сборкой нельзя: там стиль уже другой объект.
        /// </summary>
        readonly Dictionary<string, string> _marks = new Dictionary<string, string>(StringComparer.Ordinal);

        /// <summary>Имена поверхностей, уже занятые в сборке.</summary>
        readonly HashSet<string> _surfaces = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        static bool Ready()
        {
            if (_looked) return _entityType != null;
            _looked = true;

            Assembly assembly = CivilAssembly();
            if (assembly == null) return false;   // не Civil 3D

            try
            {
                Type entity = assembly.GetType("Autodesk.Civil.DatabaseServices.Entity");
                Type style = assembly.GetType("Autodesk.Civil.DatabaseServices.Styles.StyleBase");
                Type document = assembly.GetType("Autodesk.Civil.ApplicationServices.CivilDocument");
                Type resolver = assembly.GetType("Autodesk.Civil.StyleConflictResolverType");
                if (entity == null || style == null || document == null || resolver == null) return false;

                // Civil 3D переобъявляет свойства по уровням: где-то только запись,
                // где-то только чтение. Берём объявление, у которого есть нужное.
                _styleId = Readable(entity, "StyleId", typeof(ObjectId), true);
                _styleName = Readable(style, "Name", typeof(string), false);
                _styleRename = Writable(style, "Name", typeof(string));

                _exportTo = style.GetMethod("ExportTo", BindingFlags.Public | BindingFlags.Static, null,
                                            new[] { typeof(ObjectIdCollection), typeof(Database), resolver }, null);
                _keepExisting = Enum.IsDefined(resolver, "Ignore") ? Enum.Parse(resolver, "Ignore")
                                                                   : Enum.Parse(resolver, "Override");
                _civilDocument = document.GetMethod("GetCivilDocument", BindingFlags.Public | BindingFlags.Static,
                                                    null, new[] { typeof(Database) }, null);
                _stylesRoot = document.GetProperty("Styles");

                // Без поверхностей стили всё равно возвращаются — переименование
                // поверхностей просто не делается.
                _surfaceType = assembly.GetType("Autodesk.Civil.DatabaseServices.Surface");
                if (_surfaceType != null)
                {
                    _surfaceName = Readable(_surfaceType, "Name", typeof(string), false);
                    _surfaceRename = Writable(_surfaceType, "Name", typeof(string));
                }

                _civil = assembly;
                _styleType = style;
                if (_styleId != null && _styleName != null && _exportTo != null &&
                    _civilDocument != null && _stylesRoot != null)
                    _entityType = entity;
            }
            catch (System.Exception ex)
            {
                Trace.Write("стили Civil 3D недоступны: " + ex.Message);
            }
            return _entityType != null;
        }

        /// <summary>Объявление свойства, которое читается (и, если надо, пишется), — от типа вверх.</summary>
        static PropertyInfo Readable(Type type, string name, Type valueType, bool writable)
        {
            for (Type level = type; level != null; level = level.BaseType)
            {
                PropertyInfo property = level.GetProperty(name, BindingFlags.Public | BindingFlags.Instance |
                                                                BindingFlags.DeclaredOnly);
                if (property == null || property.PropertyType != valueType) continue;
                if (property.GetGetMethod() == null) continue;
                if (writable && property.GetSetMethod() == null) continue;
                return property;
            }
            return null;
        }

        /// <summary>Объявление свойства, которое пишется, — от типа вверх.</summary>
        static PropertyInfo Writable(Type type, string name, Type valueType)
        {
            for (Type level = type; level != null; level = level.BaseType)
            {
                PropertyInfo property = level.GetProperty(name, BindingFlags.Public | BindingFlags.Instance |
                                                                BindingFlags.DeclaredOnly);
                if (property == null || property.PropertyType != valueType) continue;
                if (property.GetSetMethod() != null) return property;
            }
            return null;
        }

        /// <summary>
        /// Управляемая сборка Civil 3D. Продукт подгружает её не сразу — в консольном
        /// ядре, например, её нет, пока кто-нибудь не попросит. Поэтому, если объекты
        /// Civil 3D в продукте зарегистрированы, а сборки ещё нет, грузим её сами
        /// из каталога продукта. В чистом AutoCAD объектов Civil нет — и грузить нечего.
        /// </summary>
        static Assembly CivilAssembly()
        {
            foreach (Assembly loaded in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (string.Equals(loaded.GetName().Name, "AeccDbMgd", StringComparison.OrdinalIgnoreCase)) return loaded;
            }

            try
            {
                if (!Autodesk.AutoCAD.Runtime.SystemObjects.ClassDictionary.Contains("AeccDbSurfaceTin")) return null;

                string product = System.IO.Path.GetDirectoryName(typeof(Database).Assembly.Location);
                string path = System.IO.Path.Combine(System.IO.Path.Combine(product, "C3D"), "AeccDbMgd.dll");
                return System.IO.File.Exists(path) ? Assembly.LoadFrom(path) : null;
            }
            catch (System.Exception ex)
            {
                Trace.Write("сборка Civil 3D не загружена: " + ex.Message);
                return null;
            }
        }

        // ---------------------------------------------------------------------------
        // До переноса: однофамильцы и стили
        // ---------------------------------------------------------------------------

        /// <summary>
        /// Готовит исходник к переносу: разводит однофамильцев и везёт в сборку стили,
        /// которых там ещё нет. Возвращает, сколько стилей и поверхностей переименовано.
        /// </summary>
        public int Prepare(Database source, Database target, string hint)
        {
            if (!Ready()) return 0;

            int renamed = 0;
            var export = new ObjectIdCollection();
            try
            {
                using (Transaction tr = source.TransactionManager.StartTransaction())
                {
                    var styles = new List<ObjectId>();
                    var surfaces = new List<DBObject>();
                    var seen = new HashSet<ObjectId>();

                    var blocks = (BlockTable)tr.GetObject(source.BlockTableId, OpenMode.ForRead);
                    foreach (ObjectId blockId in blocks)
                    {
                        var block = (BlockTableRecord)tr.GetObject(blockId, OpenMode.ForRead);
                        if (!block.IsLayout) continue;   // в сборку едут модель и листы

                        foreach (ObjectId id in block)
                        {
                            DBObject obj = tr.GetObject(id, OpenMode.ForRead);
                            ObjectId style = StyleOf(obj);
                            if (!style.IsNull && seen.Add(style)) styles.Add(style);
                            if (_surfaceType != null && _surfaceType.IsInstanceOfType(obj)) surfaces.Add(obj);
                        }
                    }

                    foreach (ObjectId id in styles) renamed += SortStyle(tr, id, hint, export);
                    foreach (DBObject surface in surfaces) renamed += RenameSurface(surface, hint);

                    tr.Commit();
                }

                if (export.Count > 0)
                {
                    _exportTo.Invoke(null, new object[] { export, target, _keepExisting });
                    Index(target);
                }
            }
            catch (System.Exception ex)
            {
                Trace.Write("  стили Civil 3D не подготовлены: " + Unwrap(ex).Message);
            }
            return renamed;
        }

        /// <summary>
        /// Решает судьбу стиля исходника. Такого в сборке нет — едет как есть. Есть
        /// и выглядит так же — остаётся общим. Есть, но выглядит иначе — получает своё
        /// имя и едет отдельным стилем. Возвращает 1, если стиль пришлось переименовать.
        /// </summary>
        int SortStyle(Transaction tr, ObjectId id, string hint, ObjectIdCollection export)
        {
            DBObject style = tr.GetObject(id, OpenMode.ForRead);
            var name = _styleName.GetValue(style, null) as string;
            if (name == null) return 0;

            string type = style.GetType().FullName;
            string key = type + "|" + name;
            string mark = Mark(style);

            string theirs;
            if (!_marks.TryGetValue(key, out theirs))
            {
                _marks[key] = mark;
                export.Add(id);
                return 0;
            }
            if (theirs == mark || _styleRename == null) return 0;   // такой же — пусть будет общим

            // Имена, занятые стилями этого вида в сборке.
            var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string prefix = type + "|";
            foreach (string used in _marks.Keys)
            {
                if (used.StartsWith(prefix, StringComparison.Ordinal)) taken.Add(used.Substring(prefix.Length));
            }

            string free = StyleNames.FreeName(name, hint, taken);
            try
            {
                style.UpgradeOpen();
                _styleRename.SetValue(style, free, null);
            }
            catch (System.Exception ex)
            {
                Trace.Write("  стиль Civil 3D «" + name + "» не переименован: " + Unwrap(ex).Message);
                return 0;
            }

            _marks[prefix + free] = mark;
            export.Add(id);
            Trace.Write("  стиль Civil 3D «" + name + "» → «" + free + "»");
            return 1;
        }

        /// <summary>
        /// Поверхность, чьё имя в сборке уже занято, получает приписку с именем файла.
        /// Первая по счёту сохраняет имя как есть.
        /// </summary>
        int RenameSurface(DBObject surface, string hint)
        {
            if (_surfaceName == null || _surfaceRename == null) return 0;

            var name = _surfaceName.GetValue(surface, null) as string;
            if (name == null) return 0;
            if (_surfaces.Add(name)) return 0;   // имя свободно — занимаем

            string free = StyleNames.FreeName(name, hint, _surfaces);
            try
            {
                surface.UpgradeOpen();
                _surfaceRename.SetValue(surface, free, null);
                _surfaces.Add(free);
                Trace.Write("  поверхность «" + name + "» → «" + free + "»");
                return 1;
            }
            catch (System.Exception ex)
            {
                Trace.Write("  поверхность «" + name + "» не переименована: " + Unwrap(ex).Message);
                return 0;
            }
        }

        /// <summary>
        /// Перечень стилей сборки. Обходит коллекции стилей Civil 3D — они устроены
        /// деревом: стили поверхностей, подписей, а у подписей ещё и по видам.
        /// </summary>
        void Index(Database target)
        {
            using (Transaction tr = target.TransactionManager.StartTransaction())
            {
                object root = _stylesRoot.GetValue(_civilDocument.Invoke(null, new object[] { target }), null);
                Walk(tr, root, 0, new HashSet<ObjectId>());
                tr.Commit();
            }
        }

        const int IndexDepth = 5;

        void Walk(Transaction tr, object node, int depth, HashSet<ObjectId> visited)
        {
            if (node == null || depth > IndexDepth) return;

            if (node is ObjectId)
            {
                var id = (ObjectId)node;
                if (id.IsNull || !visited.Add(id)) return;

                DBObject obj;
                try { obj = tr.GetObject(id, OpenMode.ForRead); }
                catch (System.Exception) { return; }
                if (!_styleType.IsInstanceOfType(obj)) return;

                var name = _styleName.GetValue(obj, null) as string;
                if (name != null) _known[obj.GetType().FullName + "|" + name] = id;

                // У стиля подписи бывают дочерние стили — тоже коллекция.
                Members(tr, obj, depth, visited);
                return;
            }

            if (node is string || node.GetType().Assembly != _civil) return;

            var many = node as IEnumerable;
            if (many != null)
            {
                foreach (object item in many) Walk(tr, item, depth + 1, visited);
                return;
            }
            Members(tr, node, depth, visited);
        }

        void Members(Transaction tr, object node, int depth, HashSet<ObjectId> visited)
        {
            foreach (PropertyInfo property in node.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (property.DeclaringType.Assembly != _civil || property.GetIndexParameters().Length > 0) continue;

                Type kind = property.PropertyType;
                bool branch = kind == typeof(ObjectId) || (kind.Assembly == _civil && !kind.IsEnum);
                if (!branch || kind == typeof(ObjectId) && _styleType.IsInstanceOfType(node)) continue;

                MethodInfo getter = property.GetGetMethod();
                if (getter == null) continue;

                object value;
                try { value = getter.Invoke(node, null); }
                catch (System.Exception) { continue; }
                Walk(tr, value, depth + 1, visited);
            }
        }

        // ---------------------------------------------------------------------------
        // Перенос: стили объектов
        // ---------------------------------------------------------------------------

        /// <summary>Каким стилем нарисован каждый объект Civil 3D из переносимых.</summary>
        public Dictionary<ObjectId, string> Remember(Database source, ObjectIdCollection ids)
        {
            var styled = new Dictionary<ObjectId, string>();
            if (!Ready()) return styled;

            using (Transaction tr = source.TransactionManager.StartTransaction())
            {
                foreach (ObjectId id in ids)
                {
                    string key = StyleKey(tr, tr.GetObject(id, OpenMode.ForRead));
                    if (key != null) styled[id] = key;
                }
                tr.Commit();
            }
            return styled;
        }

        /// <summary>
        /// Переводит перенесённые объекты на стили, которые лежат в сборке по-настоящему.
        /// Возвращает, скольким объектам стиль пришлось подставить.
        /// </summary>
        public int Restore(Database target, IdMapping mapping, Dictionary<ObjectId, string> styled)
        {
            if (styled.Count == 0) return 0;

            int restored = 0, lost = 0;
            using (Transaction tr = target.TransactionManager.StartTransaction())
            {
                foreach (KeyValuePair<ObjectId, string> pair in styled)
                {
                    if (!mapping.Contains(pair.Key)) continue;
                    IdPair clone = mapping[pair.Key];
                    if (!clone.IsCloned || clone.Value.IsNull) continue;

                    DBObject obj = tr.GetObject(clone.Value, OpenMode.ForRead);
                    ObjectId style;
                    if (!_known.TryGetValue(pair.Value, out style)) { lost++; continue; }
                    if (StyleOf(obj) == style) continue;   // и так на месте

                    try
                    {
                        obj.UpgradeOpen();
                        Setter(obj.GetType()).SetValue(obj, style, null);
                        restored++;
                    }
                    catch (System.Exception ex)
                    {
                        lost++;
                        Trace.Write("  стиль «" + pair.Value + "» не возвращён: " + Unwrap(ex).Message);
                    }
                }
                tr.Commit();
            }

            if (lost > 0) Trace.Write("  объектов Civil 3D без стиля: " + lost);
            return restored;
        }

        /// <summary>
        /// Чем менять стиль объекту. Некоторые объекты (площадка) не принимают стиль
        /// через общее свойство и требуют своё, — его и ищем, начиная с самого типа.
        /// </summary>
        static PropertyInfo Setter(Type type)
        {
            PropertyInfo own = Readable(type, "StyleId", typeof(ObjectId), true);
            return own ?? _styleId;
        }

        /// <summary>«Тип стиля|имя» — по нему стиль ищется в сборке. Не объект Civil 3D — null.</summary>
        static string StyleKey(Transaction tr, DBObject obj)
        {
            ObjectId styleId = StyleOf(obj);
            if (styleId.IsNull) return null;
            try
            {
                DBObject style = tr.GetObject(styleId, OpenMode.ForRead);
                var name = _styleName.GetValue(style, null) as string;
                return name == null ? null : style.GetType().FullName + "|" + name;
            }
            catch (System.Exception)
            {
                return null;
            }
        }

        static ObjectId StyleOf(DBObject obj)
        {
            if (obj == null || !_entityType.IsInstanceOfType(obj)) return ObjectId.Null;
            try { return (ObjectId)_styleId.GetValue(obj, null); }
            catch (System.Exception) { return ObjectId.Null; }   // у объекта стиля нет вовсе
        }

        // ---------------------------------------------------------------------------
        // Слепок стиля
        // ---------------------------------------------------------------------------

        /// <summary>
        /// Слепок стиля — всё, чем он задаёт вид: свойства Civil 3D вглубь и показ
        /// каждой составляющей (слой, цвет, тип линий, видимость) во всех видах.
        /// Имя, описание, автор и даты в слепок не входят: по ним стили не отличаются
        /// на чертеже.
        /// </summary>
        static string Mark(DBObject style)
        {
            var mark = new StringBuilder();
            Describe(style, mark, 0);
            return mark.ToString();
        }

        static bool NotLook(string property)
        {
            switch (property)
            {
                case "Name":
                case "Description":
                case "IsUsed":
                case "ObjectId":
                case "Id":
                case "Handle":
                case "OwnerId":
                case "Database":
                    return true;
            }
            return property.Contains("Date") || property.Contains("Create") ||
                   property.Contains("Modified") || property.Contains("Edited");
        }

        const int MarkDepth = 4;

        static void Describe(object value, StringBuilder mark, int depth)
        {
            if (value == null) { mark.Append('∅'); return; }

            if (value is double)
            {
                mark.Append(((double)value).ToString("0.######", CultureInfo.InvariantCulture));
                return;
            }
            if (value is string || value is Enum || value.GetType().IsPrimitive)
            {
                mark.Append(Convert.ToString(value, CultureInfo.InvariantCulture));
                return;
            }
            if (value is ObjectId) { mark.Append(NameOf((ObjectId)value)); return; }

            Type type = value.GetType();
            if (type.Assembly != _civil) { mark.Append(value); return; }   // цвет, точка и прочее AutoCAD
            if (depth > MarkDepth) { mark.Append('…'); return; }

            var properties = new List<PropertyInfo>(type.GetProperties(BindingFlags.Public | BindingFlags.Instance));
            properties.Sort(delegate(PropertyInfo a, PropertyInfo b) { return string.CompareOrdinal(a.Name, b.Name); });
            foreach (PropertyInfo property in properties)
            {
                if (property.DeclaringType.Assembly != _civil) continue;   // свойства самого DBObject
                if (property.GetIndexParameters().Length > 0 || NotLook(property.Name)) continue;
                MethodInfo getter = property.GetGetMethod();
                if (getter == null) continue;

                mark.Append(property.Name).Append('=');
                try { Describe(getter.Invoke(value, null), mark, depth + 1); }
                catch (System.Exception) { mark.Append('!'); }
                mark.Append(';');
            }

            // Показ составляющих стиля — слой, цвет, видимость — достаётся методами
            // вида GetDisplayStylePlan(тип составляющей): перебираем все составляющие.
            var methods = new List<MethodInfo>(type.GetMethods(BindingFlags.Public | BindingFlags.Instance));
            methods.Sort(delegate(MethodInfo a, MethodInfo b) { return string.CompareOrdinal(a.Name, b.Name); });
            foreach (MethodInfo method in methods)
            {
                if (!method.Name.StartsWith("GetDisplayStyle", StringComparison.Ordinal)) continue;
                ParameterInfo[] parameters = method.GetParameters();
                if (parameters.Length != 1 || !parameters[0].ParameterType.IsEnum) continue;

                foreach (object part in Enum.GetValues(parameters[0].ParameterType))
                {
                    mark.Append(method.Name).Append('(').Append(part).Append(")=");
                    try { Describe(method.Invoke(value, new[] { part }), mark, depth + 1); }
                    catch (System.Exception) { mark.Append('!'); }
                    mark.Append(';');
                }
            }
        }

        /// <summary>
        /// Ссылка внутри стиля — по имени того, на что она указывает: в разных чертежах
        /// у одного и того же слоя разные номера.
        /// </summary>
        static string NameOf(ObjectId id)
        {
            if (id.IsNull) return "∅";
            try
            {
                Transaction tr = id.Database.TransactionManager.TopTransaction;
                if (tr == null) return "?";
                DBObject obj = tr.GetObject(id, OpenMode.ForRead);

                var record = obj as SymbolTableRecord;
                if (record != null) return record.Name;
                if (_styleType.IsInstanceOfType(obj))
                    return obj.GetType().Name + ":" + _styleName.GetValue(obj, null);
                return obj.GetType().Name;
            }
            catch (System.Exception)
            {
                return "?";
            }
        }

        static System.Exception Unwrap(System.Exception ex)
        {
            var wrapped = ex as TargetInvocationException;
            return wrapped != null && wrapped.InnerException != null ? wrapped.InnerException : ex;
        }
    }
}
