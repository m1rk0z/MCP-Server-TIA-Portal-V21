// Tools.cs - il catalogo degli strumenti esposti via MCP.
//
// Un tool e: nome, descrizione, schema degli argomenti, e se modifica o no il
// progetto. La distinzione fra lettura e scrittura non e decorativa: con
// --read-only il server rifiuta tutto cio che scrive, e ci si puo agganciare al
// progetto di un cliente senza il timore di toccarlo.
//
// Le descrizioni e i messaggi d'errore sono in inglese: sono la superficie
// pubblica del server, quella che legge il modello e chiunque trovi il repo.

using System;
using System.Collections.Generic;
using System.IO;

namespace TiaMcp
{
    public class Tool
    {
        public string Name;
        public string Description;
        public JObj Properties = new JObj();
        public List<string> Required = new List<string>();
        public bool Writes;
        public Func<JObj, object> Run;

        public Tool Arg(string name, string type, string description)
        {
            Properties.Set(name, new JObj().Set("type", type).Set("description", description));
            return this;
        }

        public Tool ArgList(string name, string description)
        {
            Properties.Set(name, new JObj()
                .Set("type", "array")
                .Set("items", new JObj().Set("type", "string"))
                .Set("description", description));
            return this;
        }

        public Tool Need(string name, string type, string description)
        {
            Arg(name, type, description);
            Required.Add(name);
            return this;
        }

        public JObj Descriptor()
        {
            var schema = new JObj()
                .Set("type", "object")
                .Set("properties", Properties)
                .Set("required", Required);

            return new JObj()
                .Set("name", Name)
                .Set("description", Description)
                .Set("inputSchema", schema);
        }
    }

    public class Tools
    {
        readonly TiaSession session;
        readonly bool readOnly;
        readonly List<Tool> list = new List<Tool>();
        readonly Dictionary<string, Tool> byName = new Dictionary<string, Tool>(StringComparer.Ordinal);

        public Tools(TiaSession session, bool readOnly)
        {
            this.session = session;
            this.readOnly = readOnly;
            Register();
        }

        public List<JObj> Descriptors()
        {
            var result = new List<JObj>();
            foreach (Tool t in list) result.Add(t.Descriptor());
            return result;
        }

        public object Call(string name, JObj args)
        {
            Tool t;
            if (!byName.TryGetValue(name, out t))
                throw new McpError("Unknown tool: " + name);

            if (t.Writes && readOnly)
                throw new McpError("This server was started read-only (--read-only), and " + name +
                                   " would modify the project. Restart it without --read-only to use it.");

            return t.Run(args == null ? new JObj() : args);
        }

        Tool Add(string name, string description)
        {
            var t = new Tool { Name = name, Description = description };
            list.Add(t);
            byName[name] = t;
            return t;
        }

        // ------------------------------------------------------ scelta del target

        Panel PickPanel(JObj a)
        {
            string wanted = a.Str("panel");
            var panels = new List<Panel>();
            foreach (SoftwareRef r in session.Softwares())
            {
                Panel p = Panel.Wrap(r);
                if (p != null) panels.Add(p);
            }

            if (panels.Count == 0) throw new McpError("This project contains no HMI panel.");

            if (string.IsNullOrEmpty(wanted))
            {
                if (panels.Count == 1) return panels[0];
                var names = new List<string>();
                foreach (Panel p in panels) names.Add(p.Name + " (" + p.DeviceName + ")");
                throw new McpError("There are " + panels.Count + " panels: say which one with 'panel'. " +
                                   string.Join(", ", names.ToArray()));
            }

            foreach (Panel p in panels)
                if (string.Equals(p.Name, wanted, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(p.DeviceName, wanted, StringComparison.OrdinalIgnoreCase)) return p;

            var have = new List<string>();
            foreach (Panel p in panels) have.Add(p.Name);
            throw new McpError("No such panel: " + wanted + ". Available: " + string.Join(", ", have.ToArray()));
        }

        UnifiedPanel PickUnified(JObj a, string tool)
        {
            Panel p = PickPanel(a);
            var u = p as UnifiedPanel;
            if (u == null)
                throw new McpError(tool + " exists only on WinCC Unified. Panel '" + p.Name +
                                   "' is " + p.FamilyLabel + ".");
            return u;
        }

        Plc PickPlc(JObj a)
        {
            string wanted = a.Str("plc");
            var plcs = new List<Plc>();
            foreach (SoftwareRef r in session.Softwares())
                if (Plc.Is(r)) plcs.Add(new Plc(r));

            if (plcs.Count == 0) throw new McpError("This project contains no PLC.");

            if (string.IsNullOrEmpty(wanted))
            {
                if (plcs.Count == 1) return plcs[0];
                var names = new List<string>();
                foreach (Plc p in plcs) names.Add(p.DeviceName);
                throw new McpError("There are " + plcs.Count + " PLCs: say which one with 'plc'. " +
                                   string.Join(", ", names.ToArray()));
            }

            foreach (Plc p in plcs)
                if (string.Equals(p.Name, wanted, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(p.DeviceName, wanted, StringComparison.OrdinalIgnoreCase)) return p;

            var have = new List<string>();
            foreach (Plc p in plcs) have.Add(p.DeviceName);
            throw new McpError("No such PLC: " + wanted + ". Available: " + string.Join(", ", have.ToArray()));
        }

        /// <summary>files espliciti, oppure tutti i file di una cartella che corrispondono a un modello.</summary>
        static List<string> FileSet(JObj a)
        {
            var files = a.Strings("files");
            if (files.Count > 0) return files;

            string dir = a.Str("dir");
            if (string.IsNullOrEmpty(dir)) return files;
            if (!Directory.Exists(dir)) throw new McpError("No such directory: " + dir);

            string pattern = a.Str("pattern", "*.xml");
            var found = new List<string>(Directory.GetFiles(dir, pattern));
            found.Sort(StringComparer.OrdinalIgnoreCase);
            return found;
        }

        // ------------------------------------------------------------- catalogo

        void Register()
        {
            // ---------------------------------------------------------- sessione

            Add("tia_instances",
                "List the running TIA Portal instances and the project open in each. Attaches to " +
                "nothing: this call raises no confirmation dialog, so it is always safe to start here.")
                .Run = delegate { return new JObj().Set("instances", TiaSession.ListInstances()); };

            Add("tia_attach",
                "Attach to an already running TIA Portal instance and keep the session alive for the " +
                "rest of the conversation. Whoever is sitting at TIA Portal must accept ONCE; every " +
                "later tool call then runs with no further prompts. Without a pid, attaches to the " +
                "first instance that has a project open.")
                .Arg("pid", "integer", "Process id of the instance. Omit for the first one with a project open.")
                .Run = delegate(JObj a) { return session.Attach(a.Int("pid", 0)); };

            Add("tia_open_project",
                "Open a project file in a new TIA Portal instance. Headless (the default) is faster and " +
                "raises no dialog, but nothing is visible on screen.")
                .Need("path", "string", "Path to the project (.ap21, .zap21, ...).")
                .Arg("with_ui", "boolean", "true to open TIA Portal visibly. Default false.")
                .Writes = true;
            byName["tia_open_project"].Run =
                delegate(JObj a) { return session.OpenProject(a.Str("path"), a.Bool("with_ui", false)); };

            Add("tia_session",
                "Session state: whether it is attached, to which process and project, and which folder " +
                "the Openness assemblies were loaded from.")
                .Run = delegate
                {
                    JObj j = session.Describe();
                    j.Set("read_only", readOnly);
                    j.Set("assembly_search_path", Openness.SearchPath);
                    return j;
                };

            Add("tia_detach",
                "Close the Openness session. TIA Portal stays open; a project this server opened " +
                "headless is closed with it.")
                .Run = delegate { return session.Detach(); };

            Add("tia_save", "Save the project.")
                .Writes = true;
            byName["tia_save"].Run = delegate { return session.Save(); };

            Add("tia_compile",
                "Compile a device, a panel or a PLC and return the message tree. Target '*' compiles " +
                "every software in the project.")
                .Arg("target", "string", "Device or software name. '*' for all. Default '*'.")
                .Writes = true;
            byName["tia_compile"].Run = delegate(JObj a)
            {
                session.RequireAttached();
                string want = a.Str("target", "*");
                var results = new List<JObj>();
                foreach (SoftwareRef r in session.Softwares())
                {
                    string label = r.Device.Name + " / " + r.Software.Name;
                    if (want != "*" &&
                        !string.Equals(want, r.Device.Name, StringComparison.OrdinalIgnoreCase) &&
                        !string.Equals(want, r.Software.Name, StringComparison.OrdinalIgnoreCase)) continue;
                    try { results.Add(Browse.Compile(r.Software, label)); }
                    catch (McpError ex) { results.Add(new JObj().Set("target", label).Set("skipped", ex.Message)); }
                }
                if (results.Count == 0) throw new McpError("No software matches target '" + want + "'.");
                return new JObj().Set("compiled", results.Count).Set("results", results);
            };

            // ---------------------------------------------------------- progetto

            Add("tia_devices",
                "Every device in the project: type, order number, firmware, the software it hosts, and " +
                "the network addresses with the subnet each is attached to.")
                .Run = delegate { return new JObj().Set("devices", session.DeviceOverview()); };

            Add("tia_browse",
                "Walk the Openness object model. A path is segments separated by '/': each segment is a " +
                "property name, or an element name when you are standing on a collection, or [n] for an " +
                "index. Empty path is the project. Two shortcuts reach the software objects, which are " +
                "services rather than properties and cannot be reached any other way: 'Hmi/<panel>' and " +
                "'Plc/<plc>'. Use this for anything the dedicated tools do not cover - themes, recipes, " +
                "scripts, the scheduler, runtime security.")
                .Arg("path", "string", "e.g. 'Hmi/HMI_RT_1/ScreenFolder/Screens'. Empty for the project root.")
                .Arg("attributes", "boolean", "true to include the object's attributes as well.")
                .Run = delegate(JObj a)
                {
                    session.RequireAttached();
                    return Browse.Describe(session, a.Str("path", ""), a.Bool("attributes", false));
                };

            Add("tia_get_attributes",
                "Every readable attribute of the object at the end of the path. Openness publishes no " +
                "single list of attribute names: this reads them off the live object, so it is also how " +
                "you find out what an object actually exposes.")
                .Need("path", "string", "Object model path, same syntax as tia_browse.")
                .Run = delegate(JObj a)
                {
                    session.RequireAttached();
                    object o = Browse.Resolve(session, a.Str("path"));
                    var eo = o as Siemens.Engineering.IEngineeringObject;
                    if (eo == null) throw new McpError("That path does not lead to an object with attributes.");
                    var refused = new JObj();
                    JObj ok = Browse.AttributesOf(eo, refused);
                    return new JObj()
                        .Set("path", a.Str("path"))
                        .Set("type", o.GetType().Name)
                        .Set("attributes", ok)
                        .Set("unreadable", refused);
                };

            Add("tia_set_attributes",
                "Write attributes on the object at the end of the path. Many Openness attributes are " +
                "read-only (an HMI tag's LogicalAddress, for one): for those the route is export, edit " +
                "the XML, re-import.")
                .Need("path", "string", "Object model path.")
                .Need("values", "object", "Name/value pairs to write.")
                .Writes = true;
            byName["tia_set_attributes"].Run = delegate(JObj a)
            {
                session.RequireAttached();
                return Browse.SetAttributes(session, a.Str("path"), a.Obj("values"));
            };

            // --------------------------------------------------- HMI, entrambe le famiglie

            Add("hmi_panels",
                "The HMI panels in the project, each with its family: WinCC classic (Comfort, Advanced, " +
                "Professional) or WinCC Unified. The two families have different object models and " +
                "different capabilities, so ask this first.")
                .Run = delegate
                {
                    session.RequireAttached();
                    var result = new List<JObj>();
                    foreach (SoftwareRef r in session.Softwares())
                    {
                        Panel p = Panel.Wrap(r);
                        if (p != null) result.Add(p.Identity());
                    }
                    return new JObj().Set("panels", result);
                };

            Add("hmi_info",
                "Panel fact sheet: device type, counts (screens, tags, lists, connections, alarms where " +
                "they exist) and every readable attribute.")
                .Arg("panel", "string", "Panel or device name. Omit when there is only one.")
                .Run = delegate(JObj a) { return PickPanel(a).Info(); };

            Add("hmi_screens",
                "List the panel's screens. On WinCC classic the object model carries only the NAME of " +
                "a screen - Openness exposes no other attribute on it - so screen number, size and " +
                "background come back only with details=true, which exports each screen to a temporary " +
                "file and reads them from there. Narrow it with 'names' first: on a large panel that is " +
                "one export per screen.")
                .Arg("panel", "string", "Panel name.")
                .ArgList("names", "Restrict to these screens; empty means all.")
                .Arg("details", "boolean", "Read number, size and object count from an export. " +
                                           "Classic only - Unified always has them. Default false.")
                .Run = delegate(JObj a)
                {
                    Panel p = PickPanel(a);
                    bool details = a.Bool("details", false);
                    List<JObj> s = p.Screens(a.Strings("names"), details);
                    var j = new JObj().Set("panel", p.Name).Set("family", p.FamilyLabel)
                                      .Set("count", s.Count).Set("screens", s);
                    if (!details && p.Family == HmiFamily.Classic)
                        j.Set("note", "Names only: on WinCC classic, screen number and size live in the " +
                                      "exported XML, not in the object model. Ask again with details=true " +
                                      "to have them read out.");
                    return j;
                };

            Add("hmi_export_screens",
                "Export screens to XML (WinCC classic only). Without 'names' it exports all of them. " +
                "with_defaults=true also writes the default values, which is the export you need if the " +
                "file is going to be imported back.")
                .Arg("panel", "string", "Panel name.")
                .Need("out_dir", "string", "Destination folder, created if missing.")
                .ArgList("names", "Screen names; empty means all.")
                .Arg("with_defaults", "boolean", "Default true.")
                .Run = delegate(JObj a)
                {
                    return PickPanel(a).ExportScreens(a.Str("out_dir"), a.Strings("names"),
                                                      a.Bool("with_defaults", true));
                };

            Add("hmi_import_screens",
                "Import screens from XML (WinCC classic only). Give 'files', or 'dir' plus a 'pattern'. " +
                "'keep_safe' lists screens that must never be overwritten: it protects hand-built work " +
                "Openness cannot regenerate - alarm views, trend views - which an Override would wipe. " +
                "Stops at the first failure on purpose: an XML rejected for schema reasons can take " +
                "TIA Portal down with it.")
                .Arg("panel", "string", "Panel name.")
                .ArgList("files", "Paths of the XML files to import.")
                .Arg("dir", "string", "Instead of 'files': a folder to take the files from.")
                .Arg("pattern", "string", "Filename pattern inside 'dir'. Default *.xml.")
                .Arg("folder", "string", "Destination screen folder, created if missing. Empty means the root.")
                .Arg("overwrite", "boolean", "true = Override (default), false = None. Those are the only " +
                                             "two ImportOptions screens accept.")
                .ArgList("keep_safe", "Screens that must never be touched.")
                .Writes = true;
            byName["hmi_import_screens"].Run = delegate(JObj a)
            {
                return PickPanel(a).ImportScreens(FileSet(a), a.Bool("overwrite", true),
                                                  a.Strings("keep_safe"), a.Str("folder"));
            };

            Add("hmi_delete_screens",
                "Delete screens. 'keep_safe' applies here too: listed screens are refused rather than " +
                "deleted.")
                .Arg("panel", "string", "Panel name.")
                .ArgList("names", "Screens to delete.")
                .ArgList("keep_safe", "Screens that must never be touched.")
                .Writes = true;
            byName["hmi_delete_screens"].Run = delegate(JObj a)
            {
                return PickPanel(a).DeleteScreens(a.Strings("names"), a.Strings("keep_safe"));
            };

            Add("hmi_tag_tables", "The panel's tag tables, with their folder and how many tags each holds.")
                .Arg("panel", "string", "Panel name.")
                .Run = delegate(JObj a)
                {
                    Panel p = PickPanel(a);
                    List<JObj> t = p.TagTables();
                    return new JObj().Set("panel", p.Name).Set("count", t.Count).Set("tables", t);
                };

            Add("hmi_tags",
                "The panel's tags. On WinCC classic the object model carries only the tag NAME: the PLC " +
                "address, data type, connection, acquisition cycle and comment exist only in the " +
                "exported XML, so ask with details=true to get them. That export runs once per table, " +
                "not once per tag, so it stays affordable. On Unified everything is a live property and " +
                "details changes nothing.")
                .Arg("panel", "string", "Panel name.")
                .Arg("contains", "string", "Case-insensitive filter on the name.")
                .Arg("limit", "integer", "Maximum number of tags. Default 500, 0 means no limit.")
                .Arg("details", "boolean", "Read address, data type and connection from an export. Default true.")
                .Run = delegate(JObj a)
                {
                    Panel p = PickPanel(a);
                    List<JObj> t = p.Tags(a.Str("contains"), a.Int("limit", 500), a.Bool("details", true));
                    return new JObj().Set("panel", p.Name).Set("count", t.Count).Set("tags", t);
                };

            Add("hmi_export_tags",
                "Export tag tables. Classic writes one XML per table; Unified exports the whole tag " +
                "collection into the folder, because that is the shape of its API.")
                .Arg("panel", "string", "Panel name.")
                .Need("out_dir", "string", "Destination folder.")
                .ArgList("names", "Tables to export; empty means all.")
                .Run = delegate(JObj a) { return PickPanel(a).ExportTags(a.Str("out_dir"), a.Strings("names")); };

            Add("hmi_import_tags",
                "Re-import tag tables. This is also the way to change a tag's address, which Openness " +
                "exposes read-only: export the table, fix LogicalAddress in the XML, import back with " +
                "overwrite.")
                .Arg("panel", "string", "Panel name.")
                .ArgList("files", "XML files (classic) or the source folder (Unified).")
                .Arg("dir", "string", "Instead of 'files'.")
                .Arg("pattern", "string", "Filename pattern inside 'dir'. Default *.xml.")
                .Arg("overwrite", "boolean", "true = Override (default).")
                .Writes = true;
            byName["hmi_import_tags"].Run = delegate(JObj a)
            {
                return PickPanel(a).ImportTags(FileSet(a), a.Bool("overwrite", true));
            };

            Add("hmi_text_lists",
                "The panel's text lists. They are the only way to show a word instead of a number: " +
                "without a list, a value can only be coloured.")
                .Arg("panel", "string", "Panel name.")
                .Run = delegate(JObj a)
                {
                    Panel p = PickPanel(a);
                    List<JObj> t = p.TextLists();
                    return new JObj().Set("panel", p.Name).Set("count", t.Count).Set("text_lists", t);
                };

            Add("hmi_export_text_lists", "Export text lists.")
                .Arg("panel", "string", "Panel name.")
                .Need("out_dir", "string", "Destination folder.")
                .ArgList("names", "Lists to export; empty means all.")
                .Run = delegate(JObj a) { return PickPanel(a).ExportTextLists(a.Str("out_dir"), a.Strings("names")); };

            Add("hmi_import_text_lists", "Import text lists.")
                .Arg("panel", "string", "Panel name.")
                .ArgList("files", "Files to import.")
                .Arg("dir", "string", "Instead of 'files'.")
                .Arg("pattern", "string", "Filename pattern inside 'dir'. Default *.xml.")
                .Arg("overwrite", "boolean", "true = Override (default).")
                .Writes = true;
            byName["hmi_import_text_lists"].Run = delegate(JObj a)
            {
                return PickPanel(a).ImportTextLists(FileSet(a), a.Bool("overwrite", true));
            };

            Add("hmi_connections", "The panel's connections to the PLCs, with every driver parameter.")
                .Arg("panel", "string", "Panel name.")
                .Run = delegate(JObj a)
                {
                    Panel p = PickPanel(a);
                    List<JObj> c = p.Connections();
                    return new JObj().Set("panel", p.Name).Set("count", c.Count).Set("connections", c);
                };

            Add("hmi_alarms",
                "Discrete alarms, analog alarms and alarm classes. WinCC Unified only: in the classic " +
                "object model Openness exposes no alarm objects at all, and this tool says so rather " +
                "than returning an empty list you might believe.")
                .Arg("panel", "string", "Panel name.")
                .Run = delegate(JObj a)
                {
                    Panel p = PickPanel(a);
                    List<JObj> al = p.Alarms();
                    return new JObj().Set("panel", p.Name).Set("count", al.Count).Set("alarms", al);
                };

            // ------------------------------------------------- HMI, solo Unified

            Add("hmi_create_screen",
                "Create an empty screen. WinCC Unified only: the classic model has no such primitive, " +
                "where the only route is importing an XML.")
                .Arg("panel", "string", "Panel name.")
                .Need("name", "string", "Screen name.")
                .Arg("number", "integer", "Screen number.")
                .Writes = true;
            byName["hmi_create_screen"].Run = delegate(JObj a)
            {
                return PickUnified(a, "hmi_create_screen").CreateScreen(a.Str("name"), a.Int("number", 0));
            };

            Add("hmi_screen_items",
                "The objects inside a screen, with type and attributes. WinCC Unified only: in the " +
                "classic model you export the screen and read the XML.")
                .Arg("panel", "string", "Panel name.")
                .Need("screen", "string", "Screen name.")
                .Run = delegate(JObj a)
                {
                    UnifiedPanel u = PickUnified(a, "hmi_screen_items");
                    List<JObj> items = u.ScreenItems(a.Str("screen"));
                    return new JObj().Set("screen", a.Str("screen")).Set("count", items.Count).Set("items", items);
                };

            Add("hmi_create_screen_item",
                "Add an object to a screen and set its attributes. WinCC Unified only. Name the type as " +
                "a string; hmi_item_types lists what is available.")
                .Arg("panel", "string", "Panel name.")
                .Need("screen", "string", "Destination screen.")
                .Need("item_type", "string", "Object type, e.g. HmiRectangle, HmiIOField, HmiButton.")
                .Need("item_name", "string", "Object name.")
                .Arg("attributes", "object", "Attributes to set right after creation.")
                .Writes = true;
            byName["hmi_create_screen_item"].Run = delegate(JObj a)
            {
                return PickUnified(a, "hmi_create_screen_item")
                    .CreateScreenItem(a.Str("screen"), a.Str("item_type"), a.Str("item_name"), a.Obj("attributes"));
            };

            Add("hmi_item_types",
                "The screen object types in the WinCC Unified library: shapes, controls, widgets. Works " +
                "without an attached project - it reads the installed Openness assemblies.")
                .Arg("contains", "string", "Filter on the type name.")
                .Run = delegate(JObj a)
                {
                    List<JObj> types = UnifiedPanel.ItemTypes(a.Str("contains"));
                    return new JObj().Set("count", types.Count).Set("types", types);
                };

            // --------------------------------------------------------------- PLC

            Add("plc_list", "The PLCs in the project, with block and tag counts.")
                .Run = delegate
                {
                    session.RequireAttached();
                    var result = new List<JObj>();
                    foreach (SoftwareRef r in session.Softwares())
                        if (Plc.Is(r)) result.Add(new Plc(r).Info());
                    return new JObj().Set("plcs", result);
                };

            Add("plc_blocks",
                "The PLC's blocks - OB, FB, FC, DB - with number, language, consistency and know-how " +
                "protection.")
                .Arg("plc", "string", "PLC or device name. Omit when there is only one.")
                .Arg("contains", "string", "Filter on the name.")
                .Arg("kind", "string", "Filter on the type: OB, FB, FC, GlobalDB, InstanceDB.")
                .Arg("limit", "integer", "Maximum number of blocks. Default 500, 0 means no limit.")
                .Run = delegate(JObj a)
                {
                    Plc p = PickPlc(a);
                    List<JObj> b = p.Blocks(a.Str("contains"), a.Str("kind"), a.Int("limit", 500));
                    return new JObj().Set("plc", p.DeviceName).Set("count", b.Count).Set("blocks", b);
                };

            Add("plc_export_blocks",
                "Export PLC blocks to XML so their logic can be read. Know-how protected blocks cannot " +
                "be exported and are reported as such. The PLC side of this server is read-only by " +
                "design: nothing here writes a block back.")
                .Arg("plc", "string", "PLC name.")
                .Need("out_dir", "string", "Destination folder.")
                .ArgList("names", "Blocks to export.")
                .Run = delegate(JObj a) { return PickPlc(a).ExportBlocks(a.Str("out_dir"), a.Strings("names")); };

            Add("plc_tag_tables", "The PLC's tag tables.")
                .Arg("plc", "string", "PLC name.")
                .Run = delegate(JObj a)
                {
                    Plc p = PickPlc(a);
                    List<JObj> t = p.TagTables();
                    return new JObj().Set("plc", p.DeviceName).Set("count", t.Count).Set("tables", t);
                };

            Add("plc_tags",
                "The PLC's tags with absolute address and data type. Use it to check what an HMI tag " +
                "actually points at.")
                .Arg("plc", "string", "PLC name.")
                .Arg("contains", "string", "Filter on the name.")
                .Arg("limit", "integer", "Maximum number of tags. Default 500, 0 means no limit.")
                .Run = delegate(JObj a)
                {
                    Plc p = PickPlc(a);
                    List<JObj> t = p.Tags(a.Str("contains"), a.Int("limit", 500));
                    return new JObj().Set("plc", p.DeviceName).Set("count", t.Count).Set("tags", t);
                };
        }
    }
}
