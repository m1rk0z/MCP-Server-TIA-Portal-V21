// Browse.cs - navigazione generica del modello a oggetti, e compilazione.
//
// Openness espone tutto attraverso un unico modello: IEngineeringObject con i
// suoi attributi, e composizioni che si comportano da elenchi. Un navigatore
// generico copre percio anche cio che questo server non ha previsto con un tool
// dedicato - temi, ricette, script, pianificatore, sicurezza runtime - senza
// dover scrivere un metodo per ognuno.
//
// Sintassi del percorso, segmenti separati da '/':
//     Devices/PLC_1/DeviceItems/[1]/SoftwareContainer
//   - un segmento e il nome di una PROPRIETA dell'oggetto corrente,
//   - oppure, se l'oggetto corrente e una composizione, il NOME di un elemento,
//   - oppure [n], l'indice dentro una composizione.
// Il percorso vuoto e il progetto.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Siemens.Engineering;
using Siemens.Engineering.Compiler;

namespace TiaMcp
{
    public static class Browse
    {
        public static object Resolve(TiaSession session, string path)
        {
            object current = session.Project;
            if (string.IsNullOrEmpty(path)) return current;

            var segments = new List<string>();
            foreach (string raw in path.Replace('\\', '/').Split('/'))
            {
                string seg = raw.Trim();
                if (seg.Length > 0) segments.Add(seg);
            }
            if (segments.Count == 0) return current;

            // Scorciatoia per i software. Senza, non ci si arriva: un HmiTarget non
            // e una proprieta del DeviceItem, e un SERVIZIO (GetService<SoftwareContainer>),
            // e nessun percorso fatto di sole proprieta lo raggiunge.
            //   Hmi/<pannello>      il pannello, classico o Unified
            //   Plc/<plc>           il software del PLC
            //   Software/<nome>     uno qualunque dei due
            string head = segments[0];
            if (string.Equals(head, "Hmi", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(head, "Plc", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(head, "Software", StringComparison.OrdinalIgnoreCase))
            {
                if (segments.Count < 2)
                    throw new McpError("After '" + head + "/' a name is required: try hmi_panels or plc_list.");
                current = FindSoftware(session, head, segments[1]);
                segments.RemoveRange(0, 2);
            }

            foreach (string seg in segments) current = Step(current, seg, path);
            return current;
        }

        static object FindSoftware(TiaSession session, string kind, string name)
        {
            bool wantHmi = !string.Equals(kind, "Plc", StringComparison.OrdinalIgnoreCase);
            bool wantPlc = !string.Equals(kind, "Hmi", StringComparison.OrdinalIgnoreCase);

            var available = new List<string>();
            foreach (SoftwareRef r in session.Softwares())
            {
                bool isPlc = r.Software is Siemens.Engineering.SW.PlcSoftware;
                bool isHmi = r.Software is Siemens.Engineering.Hmi.HmiTarget ||
                             r.Software is Siemens.Engineering.HmiUnified.HmiSoftware;
                if (isPlc && !wantPlc) continue;
                if (isHmi && !wantHmi) continue;
                if (!isPlc && !isHmi) continue;

                available.Add(r.Software.Name + " (" + r.Device.Name + ")");
                if (string.Equals(r.Software.Name, name, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(r.Device.Name, name, StringComparison.OrdinalIgnoreCase))
                    return r.Software;
            }
            throw new McpError("No software named '" + name + "' under " + kind + "/. Available: " +
                               (available.Count == 0 ? "(none)" : string.Join(", ", available.ToArray())));
        }

        static object Step(object current, string seg, string fullPath)
        {
            if (current == null) throw new McpError("Path hit a null before '" + seg + "' in " + fullPath);

            // [n] - indice dentro una composizione
            if (seg.Length > 2 && seg[0] == '[' && seg[seg.Length - 1] == ']')
            {
                int n;
                if (!int.TryParse(seg.Substring(1, seg.Length - 2), out n))
                    throw new McpError("Not a valid index: " + seg);
                int i = 0;
                IEnumerable seq = current as IEnumerable;
                if (seq == null) throw new McpError("'" + Describe(current) + "' is not a collection, so [" + n + "] does not apply.");
                foreach (object o in seq) { if (i++ == n) return o; }
                throw new McpError("Index out of range: " + seg);
            }

            // proprieta dell'oggetto corrente
            PropertyInfo p = FindProperty(current.GetType(), seg);
            if (p != null)
            {
                try { return p.GetValue(current, null); }
                catch (TargetInvocationException ex)
                {
                    throw new McpError("Reading '" + seg + "' failed: " +
                                       (ex.InnerException != null ? ex.InnerException.Message : ex.Message));
                }
            }

            // elemento di una composizione, per nome
            IEnumerable items = current as IEnumerable;
            if (items != null)
            {
                foreach (object o in items)
                {
                    string name = NameOf(o);
                    if (name != null && string.Equals(name, seg, StringComparison.OrdinalIgnoreCase)) return o;
                }
                throw new McpError("No element named '" + seg + "' in " + Describe(current) +
                                   ". Browse the parent path to see what is there.");
            }

            throw new McpError("'" + Describe(current) + "' has neither a property nor an element called '" + seg + "'.");
        }

        static PropertyInfo FindProperty(Type t, string name)
        {
            PropertyInfo p = t.GetProperty(name,
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
            if (p != null && p.GetIndexParameters().Length == 0) return p;
            return null;
        }

        static string NameOf(object o)
        {
            if (o == null) return null;
            PropertyInfo p = o.GetType().GetProperty("Name");
            if (p == null) return null;
            try { return p.GetValue(o, null) as string; }
            catch { return null; }
        }

        static string Describe(object o)
        {
            return o == null ? "null" : o.GetType().Name;
        }

        /// <summary>Contenuto di un percorso: figli navigabili e attributi leggibili.</summary>
        public static JObj Describe(TiaSession session, string path, bool withAttributes)
        {
            object o = Resolve(session, path);
            var j = new JObj()
                .Set("path", path == null ? "" : path)
                .Set("type", o == null ? null : o.GetType().Name)
                .Set("type_full", o == null ? null : o.GetType().FullName)
                .Set("name", NameOf(o));

            if (o == null) return j;

            // elementi, se e una composizione
            IEnumerable seq = o as IEnumerable;
            if (seq != null && !(o is string))
            {
                var items = new List<JObj>();
                int i = 0;
                foreach (object child in seq)
                {
                    items.Add(new JObj()
                        .Set("index", i++)
                        .Set("name", NameOf(child))
                        .Set("type", child == null ? null : child.GetType().Name));
                    if (items.Count >= 500) break;
                }
                j.Set("items", items);
                j.Set("item_count", items.Count);
            }

            // proprieta navigabili
            var children = new List<JObj>();
            foreach (PropertyInfo p in o.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (p.GetIndexParameters().Length > 0) continue;
                if (p.Name == "Parent") continue;
                Type pt = p.PropertyType;
                bool navigable = !pt.IsPrimitive && pt != typeof(string) && !pt.IsEnum;
                if (!navigable) continue;
                children.Add(new JObj().Set("property", p.Name).Set("type", pt.Name));
            }
            j.Set("properties", children);

            if (withAttributes)
            {
                var obj = o as IEngineeringObject;
                j.Set("attributes", obj == null ? new JObj() : AttributesOf(obj));
            }
            return j;
        }

        public static JObj AttributesOf(IEngineeringObject o) { return AttributesOf(o, null); }

        /// <summary>
        /// Gli attributi leggibili. Quelli che GetAttributeInfos dichiara ma
        /// GetAttribute rifiuta finiscono in 'unreadable' con il motivo, invece di
        /// sparire: un attributo assente e un attributo che esplode sono due
        /// diagnosi diverse, e il silenzio le confonde.
        /// </summary>
        public static JObj AttributesOf(IEngineeringObject o, JObj unreadable)
        {
            var j = new JObj();
            List<EngineeringAttributeInfo> infos;
            try { infos = new List<EngineeringAttributeInfo>(o.GetAttributeInfos()); }
            catch (Exception ex)
            {
                if (unreadable != null) unreadable.Set("(GetAttributeInfos)", Root(ex).Message);
                return j;
            }

            infos.Sort(delegate(EngineeringAttributeInfo a, EngineeringAttributeInfo b)
            {
                return string.Compare(a.Name, b.Name, StringComparison.Ordinal);
            });

            foreach (EngineeringAttributeInfo i in infos)
            {
                try
                {
                    object v = o.GetAttribute(i.Name);
                    j.Set(i.Name, v == null ? null : v.ToString());
                }
                catch (Exception ex)
                {
                    if (unreadable != null) unreadable.Set(i.Name, Root(ex).Message);
                }
            }
            return j;
        }

        public static JObj SetAttributes(TiaSession session, string path, JObj values)
        {
            object o = Resolve(session, path);
            var obj = o as IEngineeringObject;
            if (obj == null) throw new McpError("That path does not lead to an object with attributes: " + path);
            if (values == null || values.Count == 0) throw new McpError("No attributes to write.");

            var done = new JObj();
            var failed = new List<JObj>();
            foreach (KeyValuePair<string, object> kv in values)
            {
                try
                {
                    obj.SetAttribute(kv.Key, Coerce(obj, kv.Key, kv.Value));
                    done.Set(kv.Key, kv.Value);
                }
                catch (Exception ex)
                {
                    failed.Add(new JObj().Set("attribute", kv.Key).Set("error", Root(ex).Message));
                }
            }
            return new JObj()
                .Set("path", path)
                .Set("set", done)
                .Set("failed", failed)
                .Set("hint", failed.Count > 0
                    ? "Many Openness attributes are read-only (an HMI tag's LogicalAddress, for one): " +
                      "for those the route is export, edit the XML, re-import."
                    : null);
        }

        // JSON conosce solo double e string: prima di scrivere si riporta il valore
        // al tipo dichiarato dall'attributo, altrimenti Openness lo rifiuta.
        static object Coerce(IEngineeringObject o, string attribute, object value)
        {
            if (value == null) return null;
            try
            {
                foreach (EngineeringAttributeInfo i in o.GetAttributeInfos())
                {
                    if (i.Name != attribute) continue;
                    object current = null;
                    try { current = o.GetAttribute(attribute); }
                    catch { }
                    if (current == null) return value;

                    Type t = current.GetType();
                    if (t.IsEnum)
                    {
                        string s = value as string;
                        if (s != null) return Enum.Parse(t, s, true);
                        return Enum.ToObject(t, (long)Convert.ToDouble(value));
                    }
                    if (t == typeof(string)) return Convert.ToString(value);
                    if (t == typeof(bool)) return value is bool ? value : Convert.ToBoolean(value);
                    return Convert.ChangeType(value, t);
                }
            }
            catch { }
            return value;
        }

        static Exception Root(Exception ex)
        {
            while (ex.InnerException != null) ex = ex.InnerException;
            return ex;
        }

        // ------------------------------------------------------------ compilare

        public static JObj Compile(object what, string label)
        {
            ICompilable c = null;
            var provider = what as IEngineeringServiceProvider;
            if (provider != null)
            {
                try { c = provider.GetService<ICompilable>(); }
                catch { }
            }
            if (c == null) throw new McpError("'" + label + "' is not compilable: it offers no ICompilable service.");

            CompilerResult res = c.Compile();

            var messages = new List<JObj>();
            Flatten(res.Messages, messages, 0);

            return new JObj()
                .Set("target", label)
                .Set("state", res.State.ToString())
                .Set("errors", res.ErrorCount)
                .Set("warnings", res.WarningCount)
                .Set("messages", messages);
        }

        static void Flatten(object messages, List<JObj> into, int depth)
        {
            if (messages == null || depth > 4) return;
            IEnumerable seq = messages as IEnumerable;
            if (seq == null) return;

            foreach (object m in seq)
            {
                string state = Get(m, "State");
                string text = Get(m, "Description");
                string where = Get(m, "Path");
                // gli avvisi senza testo esistono: senza il percorso sono introvabili
                if (text.Trim().Length == 0 && where.Length > 0) text = "(no text) raised by: " + where;

                into.Add(new JObj()
                    .Set("state", state)
                    .Set("text", text)
                    .Set("path", where)
                    .Set("depth", depth));

                if (into.Count > 2000) return;

                PropertyInfo p = m.GetType().GetProperty("Messages");
                if (p != null) Flatten(p.GetValue(m, null), into, depth + 1);
            }
        }

        static string Get(object o, string prop)
        {
            PropertyInfo p = o.GetType().GetProperty(prop);
            if (p == null) return "";
            try { object v = p.GetValue(o, null); return v == null ? "" : v.ToString(); }
            catch { return ""; }
        }
    }
}
