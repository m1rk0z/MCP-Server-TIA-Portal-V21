// UnifiedPanel.cs - WinCC Unified (HmiSoftware).
//
// Il modello e l'opposto del classico. Qui le schermate SI COSTRUISCONO:
//   Screens.Create(nome)                      crea la schermata
//   screen.ScreenItems.Create<T>(nome)        aggiunge un oggetto
//   item.SetAttribute("Left", 42)             ne scrive le proprieta
// e in cambio non esiste alcun import/export di schermate: nel modello Unified
// export e import esistono solo per tag, liste testi, liste immagini e script,
// e lavorano su una CARTELLA, non su un singolo file.
//
// In compenso Unified espone quello che nel classico manca del tutto: allarmi
// discreti, allarmi analogici e classi di allarme.
//
// ScreenItems.Create<T>() e generico. Per creare un tipo scelto a runtime si
// passa da MakeGenericMethod: cosi il server supporta ogni oggetto della
// libreria Unified senza doverne elencare uno per uno i cento tipi.

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Siemens.Engineering;
using Siemens.Engineering.HmiUnified;
using Siemens.Engineering.HmiUnified.HmiAlarm;
using Siemens.Engineering.HmiUnified.HmiConnections;
using Siemens.Engineering.HmiUnified.HmiTags;
using Siemens.Engineering.HmiUnified.TextGraphicList;
using Siemens.Engineering.HmiUnified.UI.Base;
using Siemens.Engineering.HmiUnified.UI.Screens;

namespace TiaMcp
{
    public class UnifiedPanel : Panel
    {
        readonly HmiSoftware sw;

        public UnifiedPanel(SoftwareRef r)
        {
            Ref = r;
            sw = (HmiSoftware)r.Software;
        }

        public override HmiFamily Family { get { return HmiFamily.Unified; } }
        public override string Name { get { return sw.Name; } }

        // ---------------------------------------------------------- descrizione

        public override JObj Info()
        {
            var j = Identity();

            var attrs = new JObj();
            try
            {
                foreach (EngineeringAttributeInfo info in sw.GetAttributeInfos())
                {
                    string v = TiaSession.Attr(sw, info.Name);
                    if (!string.IsNullOrEmpty(v)) attrs.Set(info.Name, v);
                }
            }
            catch { }

            j.Set("device_type", DeviceType())
             .Set("counts", new JObj()
                 .Set("screens", Count(sw.Screens))
                 .Set("screen_groups", Count(sw.ScreenGroups))
                 .Set("tag_tables", Count(sw.TagTables))
                 .Set("tags", Count(sw.Tags))
                 .Set("text_lists", Count(sw.HmiTextLists))
                 .Set("graphic_lists", Count(sw.HmiGraphicLists))
                 .Set("connections", Count(sw.Connections))
                 .Set("discrete_alarms", Count(sw.DiscreteAlarms))
                 .Set("analog_alarms", Count(sw.AnalogAlarms))
                 .Set("alarm_classes", Count(sw.AlarmClasses))
                 .Set("scripts", Count(sw.Scripts)))
             .Set("attributes", attrs)
             .Set("notes", new List<string> {
                 "Screens are created and edited as objects (hmi_create_screen, hmi_create_screen_item, tia_set_attributes).",
                 "Screen import/export does NOT exist in Unified: it is available only for tags, lists and scripts, and works on folders.",
                 "Alarms and alarm classes are exposed by Openness here: hmi_alarms works on this family."
             });
            return j;
        }

        static int Count(object composition)
        {
            if (composition == null) return 0;
            PropertyInfo p = composition.GetType().GetProperty("Count");
            if (p == null) return 0;
            try { return (int)p.GetValue(composition, null); }
            catch { return 0; }
        }

        // ------------------------------------------------------------ schermate

        public override List<JObj> Screens(List<string> names, bool details)
        {
            // 'details' e ignorato: in Unified numero, dimensioni e sfondo sono
            // proprieta vere dell'oggetto, non qualcosa da andare a pescare in un export.
            var result = new List<JObj>();
            foreach (HmiScreen s in sw.Screens)
            {
                if (!Matches(names, s.Name)) continue;
                result.Add(new JObj()
                    .Set("name", s.Name)
                    .Set("folder", "/")
                    .Set("number", (int)s.ScreenNumber)
                    .Set("width", (int)s.Width)
                    .Set("height", (int)s.Height)
                    .Set("items", Count(s.ScreenItems)));
            }
            return result;
        }

        HmiScreen FindScreen(string name)
        {
            HmiScreen s = sw.Screens.Find(name);
            if (s == null) throw new McpError("No such screen: " + name);
            return s;
        }

        public override JObj CreateScreen(string name, int number)
        {
            if (string.IsNullOrEmpty(name)) throw new McpError("Missing screen name.");
            if (sw.Screens.Find(name) != null) throw new McpError("A screen named " + name + " already exists.");

            HmiScreen s = sw.Screens.Create(name);
            if (number > 0)
            {
                try { s.ScreenNumber = (ushort)number; }
                catch (Exception ex) { throw new McpError("Screen created, but screen number " + number + " was rejected: " + Flat(ex)); }
            }
            return new JObj()
                .Set("created", name)
                .Set("number", (int)s.ScreenNumber)
                .Set("width", (int)s.Width)
                .Set("height", (int)s.Height);
        }

        public override List<JObj> ScreenItems(string screen)
        {
            HmiScreen s = FindScreen(screen);
            var result = new List<JObj>();
            foreach (HmiScreenItemBase item in s.ScreenItems)
            {
                var attrs = new JObj();
                try
                {
                    foreach (EngineeringAttributeInfo i in item.GetAttributeInfos())
                    {
                        string v = TiaSession.Attr(item, i.Name);
                        if (!string.IsNullOrEmpty(v)) attrs.Set(i.Name, v);
                    }
                }
                catch { }
                result.Add(new JObj()
                    .Set("name", item.Name)
                    .Set("type", item.GetType().Name)
                    .Set("type_full", item.GetType().FullName)
                    .Set("attributes", attrs));
            }
            return result;
        }

        /// <summary>
        /// Crea un oggetto dentro una schermata. Il tipo si indica per nome, corto
        /// ("HmiRectangle") o completo: si cerca nell'assembly Unified e si invoca
        /// il Create&lt;T&gt; generico via reflection.
        /// </summary>
        public JObj CreateScreenItem(string screen, string typeName, string itemName, JObj attributes)
        {
            if (string.IsNullOrEmpty(typeName)) throw new McpError("Missing item_type.");
            if (string.IsNullOrEmpty(itemName)) throw new McpError("Missing item_name.");

            HmiScreen s = FindScreen(screen);
            Type t = ResolveItemType(typeName);

            MethodInfo create = null;
            foreach (MethodInfo m in s.ScreenItems.GetType().GetMethods())
            {
                if (m.Name != "Create" || !m.IsGenericMethodDefinition) continue;
                if (m.GetParameters().Length != 1) continue;
                create = m;
                break;
            }
            if (create == null) throw new McpError("Create<T>(name) not found on ScreenItems: unexpected Unified API.");

            object created;
            try { created = create.MakeGenericMethod(t).Invoke(s.ScreenItems, new object[] { itemName }); }
            catch (TargetInvocationException ex)
            {
                throw new McpError("Creating a " + t.Name + " was rejected: " +
                                   Flat(ex.InnerException != null ? ex.InnerException : ex));
            }

            var applied = new JObj();
            var refused = new List<JObj>();
            var obj = created as IEngineeringObject;
            if (obj != null && attributes != null)
            {
                foreach (KeyValuePair<string, object> kv in attributes)
                {
                    try { obj.SetAttribute(kv.Key, Coerce(obj, kv.Key, kv.Value)); applied.Set(kv.Key, kv.Value); }
                    catch (Exception ex) { refused.Add(new JObj().Set("attribute", kv.Key).Set("error", Flat(ex))); }
                }
            }

            return new JObj()
                .Set("screen", screen)
                .Set("created", itemName)
                .Set("type", t.FullName)
                .Set("attributes_set", applied)
                .Set("attributes_refused", refused);
        }

        /// <summary>Elenca i tipi di oggetto di schermata disponibili in Unified.</summary>
        public static List<JObj> ItemTypes(string contains)
        {
            var result = new List<JObj>();
            foreach (Type t in typeof(HmiScreen).Assembly.GetExportedTypes())
            {
                if (t.IsAbstract || !t.IsClass) continue;
                if (!typeof(HmiScreenItemBase).IsAssignableFrom(t)) continue;
                if (!Contains(t.Name, contains)) continue;
                result.Add(new JObj()
                    .Set("type", t.Name)
                    .Set("type_full", t.FullName)
                    .Set("category", LastNamespacePart(t.Namespace)));
            }
            result.Sort(delegate(JObj a, JObj b)
            {
                return string.Compare(a.Str("type"), b.Str("type"), StringComparison.Ordinal);
            });
            return result;
        }

        static string LastNamespacePart(string ns)
        {
            if (string.IsNullOrEmpty(ns)) return "";
            int i = ns.LastIndexOf('.');
            return i < 0 ? ns : ns.Substring(i + 1);
        }

        static Type ResolveItemType(string name)
        {
            Assembly asm = typeof(HmiScreen).Assembly;
            Type exact = asm.GetType(name);
            if (exact != null) return exact;

            var hits = new List<Type>();
            foreach (Type t in asm.GetExportedTypes())
            {
                if (t.IsAbstract || !t.IsClass) continue;
                if (!typeof(HmiScreenItemBase).IsAssignableFrom(t)) continue;
                if (string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase)) hits.Add(t);
            }
            if (hits.Count == 1) return hits[0];
            if (hits.Count == 0)
                throw new McpError("Unknown screen item type: " + name +
                                   ". Call hmi_item_types for the list of available types.");

            var names = new List<string>();
            foreach (Type t in hits) names.Add(t.FullName);
            throw new McpError("Ambiguous type name: " + name + " -> " + string.Join(", ", names.ToArray()));
        }

        static object Coerce(IEngineeringObject o, string attribute, object value)
        {
            // JSON conosce solo double: un attributo intero o enum va riportato al suo tipo
            if (!(value is double)) return value;
            double d = (double)value;
            try
            {
                foreach (EngineeringAttributeInfo i in o.GetAttributeInfos())
                {
                    if (i.Name != attribute) continue;
                    Type t = i.GetType().GetProperty("Type") != null
                        ? i.GetType().GetProperty("Type").GetValue(i, null) as Type
                        : null;
                    if (t == null) break;
                    if (t.IsEnum) return Enum.ToObject(t, (long)d);
                    if (t == typeof(int) || t == typeof(uint) || t == typeof(short) ||
                        t == typeof(ushort) || t == typeof(byte) || t == typeof(long))
                        return Convert.ChangeType(d, t);
                    break;
                }
            }
            catch { }
            return value;
        }

        public override JObj ExportScreens(string outDir, List<string> names, bool withDefaults)
        {
            throw Unsupported("hmi_export_screens",
                "The Unified model exposes no screen export: the Screens composition does not " +
                "implement file-based data exchange. Export and import exist only for tags, text " +
                "lists, graphic lists and scripts. Read screens with hmi_screen_items instead.");
        }

        public override JObj ImportScreens(List<string> files, bool overwrite, List<string> keepSafe, string folder)
        {
            throw Unsupported("hmi_import_screens",
                "The Unified model exposes no screen import. Screens are built as objects: " +
                "hmi_create_screen, then hmi_create_screen_item and tia_set_attributes.");
        }

        public override JObj DeleteScreens(List<string> names, List<string> keepSafe)
        {
            if (names == null || names.Count == 0) throw new McpError("No screens to delete.");

            var deleted = new List<string>();
            var refused = new List<JObj>();
            var missing = new List<string>(names);

            var victims = new List<HmiScreen>();
            foreach (HmiScreen s in sw.Screens) if (Matches(names, s.Name)) victims.Add(s);

            foreach (HmiScreen s in victims)
            {
                string n = s.Name;
                missing.RemoveAll(delegate(string x) { return string.Equals(x, n, StringComparison.OrdinalIgnoreCase); });
                if (keepSafe != null && keepSafe.Count > 0 && Matches(keepSafe, n))
                {
                    refused.Add(new JObj().Set("name", n).Set("why", "protected: listed in keep_safe"));
                    continue;
                }
                try { s.Delete(); deleted.Add(n); }
                catch (Exception ex) { refused.Add(new JObj().Set("name", n).Set("why", Flat(ex))); }
            }

            return new JObj()
                .Set("deleted", deleted.Count)
                .Set("refused", refused.Count)
                .Set("not_found", missing)
                .Set("names", deleted)
                .Set("details", refused);
        }

        // ------------------------------------------------------------------ tag

        public override List<JObj> TagTables()
        {
            var result = new List<JObj>();
            foreach (HmiTagTable t in sw.TagTables)
                result.Add(new JObj()
                    .Set("name", t.Name)
                    .Set("folder", "/")
                    .Set("tags", Count(t.Tags)));
            return result;
        }

        public override List<JObj> Tags(string contains, int limit, bool details)
        {
            var result = new List<JObj>();
            foreach (HmiTagTable table in sw.TagTables)
            {
                foreach (HmiTag tag in table.Tags)
                {
                    if (!Contains(tag.Name, contains)) continue;
                    result.Add(new JObj()
                        .Set("name", tag.Name)
                        .Set("table", table.Name)
                        .Set("folder", "/")
                        .Set("address", tag.Address)
                        .Set("data_type", tag.DataType)
                        .Set("hmi_data_type", tag.HmiDataType)
                        .Set("connection", tag.Connection)
                        .Set("plc_tag", tag.PlcTag)
                        .Set("plc_name", tag.PlcName)
                        .Set("acquisition_cycle", tag.AcquisitionCycle)
                        .Set("scope", tag.Scope.ToString()));
                    if (limit > 0 && result.Count >= limit) return result;
                }
            }
            return result;
        }

        // In Unified export e import lavorano su una CARTELLA e su un nome logico,
        // non su un file: la firma e diversa da quella del classico e non si presta
        // a essere nascosta sotto un'unica astrazione. Meglio dirlo chiaramente.

        public override JObj ExportTags(string outDir, List<string> names)
        {
            EnsureDir(outDir, "out_dir");
            var written = new List<string>();
            try
            {
                IEnumerable<FileInfo> files = names != null && names.Count == 1
                    ? sw.Tags.Export(new DirectoryInfo(outDir), names[0])
                    : sw.Tags.Export(new DirectoryInfo(outDir));
                foreach (FileInfo f in files) written.Add(f.FullName);
            }
            catch (Exception ex) { throw new McpError("Tag export failed: " + Flat(ex)); }

            return new JObj()
                .Set("exported", written.Count)
                .Set("out_dir", outDir)
                .Set("files", written)
                .Set("note", "In Unified, exporting tags writes the whole collection into the folder.");
        }

        public override JObj ImportTags(List<string> files, bool overwrite)
        {
            if (files == null || files.Count == 0) throw new McpError("Give the source folder in files[0].");
            string dir = files[0];
            if (File.Exists(dir)) dir = Path.GetDirectoryName(dir);
            if (!Directory.Exists(dir)) throw new McpError("Source folder not found: " + dir);

            bool ok;
            try { ok = sw.Tags.Import(new DirectoryInfo(dir)); }
            catch (Exception ex) { throw new McpError("Tag import failed: " + Flat(ex)); }

            return new JObj()
                .Set("imported", ok)
                .Set("source_dir", dir)
                .Set("note", "In Unified, importing tags reads a folder rather than a single file; " +
                             "ImportOptions does not apply.");
        }

        // ----------------------------------------------------------- liste testi

        public override List<JObj> TextLists()
        {
            var result = new List<JObj>();
            foreach (HmiTextList t in sw.HmiTextLists)
                result.Add(new JObj().Set("name", t.Name));
            return result;
        }

        public override JObj ExportTextLists(string outDir, List<string> names)
        {
            EnsureDir(outDir, "out_dir");
            string logical = names != null && names.Count > 0 ? names[0] : "TextLists";
            var written = new List<string>();
            try
            {
                foreach (FileInfo f in sw.HmiTextLists.Export(new DirectoryInfo(outDir), logical))
                    written.Add(f.FullName);
            }
            catch (Exception ex) { throw new McpError("Text list export failed: " + Flat(ex)); }

            return new JObj().Set("exported", written.Count).Set("out_dir", outDir)
                             .Set("name", logical).Set("files", written);
        }

        public override JObj ImportTextLists(List<string> files, bool overwrite)
        {
            if (files == null || files.Count == 0) throw new McpError("Give the source file or folder.");
            string first = files[0];
            string dir = File.Exists(first) ? Path.GetDirectoryName(first) : first;
            string logical = File.Exists(first) ? Path.GetFileNameWithoutExtension(first) : "TextLists";
            if (!Directory.Exists(dir)) throw new McpError("Source folder not found: " + dir);

            bool ok;
            try { ok = sw.HmiTextLists.Import(new DirectoryInfo(dir), logical); }
            catch (Exception ex) { throw new McpError("Text list import failed: " + Flat(ex)); }

            return new JObj().Set("imported", ok).Set("source_dir", dir).Set("name", logical);
        }

        // ----------------------------------------------------------- connessioni

        public override List<JObj> Connections()
        {
            var result = new List<JObj>();
            foreach (HmiConnection c in sw.Connections)
                result.Add(new JObj()
                    .Set("name", c.Name)
                    .Set("driver", c.CommunicationDriver)
                    .Set("partner", c.Partner)
                    .Set("station", c.Station)
                    .Set("node", c.Node)
                    .Set("initial_address", c.InitialAddress)
                    .Set("disabled_at_startup", c.DisabledAtStartup));
            return result;
        }

        // --------------------------------------------------------------- allarmi

        public override List<JObj> Alarms()
        {
            var result = new List<JObj>();

            foreach (HmiAlarmClass k in sw.AlarmClasses)
                result.Add(new JObj().Set("kind", "class").Set("name", k.Name).Set("attributes", AllAttributes(k)));

            foreach (HmiDiscreteAlarm a in sw.DiscreteAlarms)
                result.Add(new JObj().Set("kind", "discrete").Set("name", a.Name).Set("attributes", AllAttributes(a)));

            foreach (HmiAnalogAlarm a in sw.AnalogAlarms)
                result.Add(new JObj().Set("kind", "analog").Set("name", a.Name).Set("attributes", AllAttributes(a)));

            return result;
        }

        static JObj AllAttributes(IEngineeringObject o)
        {
            var j = new JObj();
            try
            {
                foreach (EngineeringAttributeInfo i in o.GetAttributeInfos())
                {
                    string v = TiaSession.Attr(o, i.Name);
                    if (!string.IsNullOrEmpty(v)) j.Set(i.Name, v);
                }
            }
            catch { }
            return j;
        }
    }
}
