// ClassicPanel.cs - WinCC Comfort, Advanced e Professional (HmiTarget).
//
// Le schermate qui non si costruiscono: si importano. Openness non espone
// nessuna Screens.Create(), e la tabella 8-5 del manuale elenca gli oggetti che
// l'import rifiuta (vista messaggi, curve f(t) e f(x), vista ricette, finestra
// di schermata, campi di testo modificabili, caselle combinate, ...).
//
// Due cose imparate sul campo e messe qui dentro, non nella documentazione:
//
//   1. Un import rifiutato per errore di schema puo far cadere TIA Portal. Al
//      primo fallimento questo codice si ferma e restituisce quanto fatto: non
//      insiste sui file successivi.
//   2. ImportOptions per le schermate ammette SOLO None e Override.
//      RenameOnConflict appartiene a DccImportOptions (grafici di azionamento),
//      non a questo. Un conflitto di numero di schermata non ha rimedio via API:
//      va evitato numerando.

using System;
using System.Collections.Generic;
using System.IO;
using Siemens.Engineering;
using Siemens.Engineering.Hmi;
using Siemens.Engineering.Hmi.Communication;
using Siemens.Engineering.Hmi.Screen;
using Siemens.Engineering.Hmi.Tag;
using Siemens.Engineering.Hmi.TextGraphicList;

namespace TiaMcp
{
    public class ClassicPanel : Panel
    {
        readonly HmiTarget target;

        public ClassicPanel(SoftwareRef r)
        {
            Ref = r;
            target = (HmiTarget)r.Software;
        }

        public override HmiFamily Family { get { return HmiFamily.Classic; } }
        public override string Name { get { return target.Name; } }

        // ---------------------------------------------------------- descrizione

        public override JObj Info()
        {
            var j = Identity();

            var attrs = new JObj();
            try
            {
                foreach (EngineeringAttributeInfo info in target.GetAttributeInfos())
                {
                    string v = TiaSession.Attr(target, info.Name);
                    if (!string.IsNullOrEmpty(v)) attrs.Set(info.Name, v);
                }
            }
            catch { }

            var screens = new List<ScreenAt>();
            CollectScreens(target.ScreenFolder, "", screens);

            int tagCount = 0;
            var tables = new List<TableAt>();
            CollectTables(target.TagFolder, "", tables);
            foreach (TableAt t in tables) tagCount += t.Table.Tags.Count;

            j.Set("device_type", DeviceType())
             .Set("counts", new JObj()
                 .Set("screens", screens.Count)
                 .Set("screen_folders", CountFolders(target.ScreenFolder))
                 .Set("tag_tables", tables.Count)
                 .Set("tags", tagCount)
                 .Set("text_lists", target.TextLists.Count)
                 .Set("graphic_lists", target.GraphicLists.Count)
                 .Set("connections", target.Connections.Count)
                 .Set("cycles", target.Cycles.Count))
             .Set("attributes", attrs)
             .Set("notes", new List<string> {
                 "Screens are handled by exporting and importing XML: Openness has no Screens.Create().",
                 "Alarms and alarm classes are NOT exposed by Openness in classic WinCC: " +
                 "they are configured in TIA, or imported from xlsx through its own editor."
             });
            return j;
        }

        // ------------------------------------------------------------ schermate

        class ScreenAt
        {
            public Screen Screen;
            public string Path;
            public ScreenComposition Owner;
        }

        void CollectScreens(ScreenSystemFolder f, string path, List<ScreenAt> into)
        {
            foreach (Screen s in f.Screens) into.Add(new ScreenAt { Screen = s, Path = path, Owner = f.Screens });
            foreach (ScreenUserFolder sub in f.Folders) CollectScreens(sub, Join(path, sub.Name), into);
        }

        void CollectScreens(ScreenUserFolder f, string path, List<ScreenAt> into)
        {
            foreach (Screen s in f.Screens) into.Add(new ScreenAt { Screen = s, Path = path, Owner = f.Screens });
            foreach (ScreenUserFolder sub in f.Folders) CollectScreens(sub, Join(path, sub.Name), into);
        }

        static string Join(string a, string b) { return a.Length == 0 ? b : a + "/" + b; }

        int CountFolders(ScreenSystemFolder f)
        {
            int n = 0;
            foreach (ScreenUserFolder sub in f.Folders) { n++; n += CountFolders(sub); }
            return n;
        }

        int CountFolders(ScreenUserFolder f)
        {
            int n = 0;
            foreach (ScreenUserFolder sub in f.Folders) { n++; n += CountFolders(sub); }
            return n;
        }

        public override List<JObj> Screens(List<string> names, bool details)
        {
            var all = new List<ScreenAt>();
            CollectScreens(target.ScreenFolder, "", all);

            var result = new List<JObj>();
            string tmp = details ? TempDir() : null;
            try
            {
                foreach (ScreenAt s in all)
                {
                    if (!Matches(names, s.Screen.Name)) continue;

                    var j = new JObj()
                        .Set("name", s.Screen.Name)
                        .Set("folder", s.Path.Length == 0 ? "/" : s.Path);

                    if (details)
                    {
                        // Il numero e le dimensioni non stanno nel modello a oggetti:
                        // l'unico posto dove esistono e l'XML che TIA scrive.
                        string file = Path.Combine(tmp, "s.xml");
                        try
                        {
                            if (File.Exists(file)) File.Delete(file);
                            s.Screen.Export(new FileInfo(file), ExportOptions.WithDefaults);
                            JObj attrs = Xml.FirstAttributeList(file, "Hmi.Screen.Screen");
                            j.Set("number", attrs.Str("Number"))
                             .Set("width", attrs.Str("Width"))
                             .Set("height", attrs.Str("Height"))
                             .Set("background", attrs.Str("BackColor"))
                             .Set("items", Xml.CountElements(file, "CompositionName=\"ScreenItems\""));
                        }
                        catch (Exception ex) { j.Set("details_error", Flat(ex)); }
                    }
                    result.Add(j);
                }
            }
            finally { if (tmp != null) Discard(tmp); }
            return result;
        }

        public override JObj ExportScreens(string outDir, List<string> names, bool withDefaults)
        {
            EnsureDir(outDir, "out_dir");
            var all = new List<ScreenAt>();
            CollectScreens(target.ScreenFolder, "", all);

            var files = new List<JObj>();
            var failed = new List<JObj>();
            ExportOptions opt = withDefaults ? ExportOptions.WithDefaults : ExportOptions.None;

            foreach (ScreenAt s in all)
            {
                if (!Matches(names, s.Screen.Name)) continue;
                string path = Path.Combine(outDir, s.Screen.Name + ".xml");
                try
                {
                    if (File.Exists(path)) File.Delete(path);
                    s.Screen.Export(new FileInfo(path), opt);
                    files.Add(new JObj().Set("name", s.Screen.Name).Set("folder", s.Path).Set("file", path));
                }
                catch (Exception ex)
                {
                    failed.Add(new JObj().Set("name", s.Screen.Name).Set("error", Flat(ex)));
                }
            }

            if (names != null && names.Count > 0)
                foreach (string want in names)
                {
                    bool seen = false;
                    foreach (JObj f in files) if (string.Equals(f.Str("name"), want, StringComparison.OrdinalIgnoreCase)) seen = true;
                    foreach (JObj f in failed) if (string.Equals(f.Str("name"), want, StringComparison.OrdinalIgnoreCase)) seen = true;
                    if (!seen) failed.Add(new JObj().Set("name", want).Set("error", "not present on this panel"));
                }

            return new JObj()
                .Set("exported", files.Count)
                .Set("failed", failed.Count)
                .Set("out_dir", outDir)
                .Set("with_defaults", withDefaults)
                .Set("files", files)
                .Set("errors", failed);
        }

        public override JObj ImportScreens(List<string> files, bool overwrite, List<string> keepSafe, string folder)
        {
            if (files == null || files.Count == 0) throw new McpError("No files to import.");

            ScreenComposition destination;
            string destName;
            if (string.IsNullOrEmpty(folder))
            {
                destination = target.ScreenFolder.Screens;
                destName = "/";
            }
            else
            {
                ScreenUserFolder dir = FindOrCreateFolder(folder);
                destination = dir.Screens;
                destName = folder;
            }

            ImportOptions opt = overwrite ? ImportOptions.Override : ImportOptions.None;

            var ok = new List<string>();
            var skipped = new List<JObj>();
            var errors = new List<JObj>();
            bool stopped = false;

            foreach (string file in files)
            {
                string screenName = Path.GetFileNameWithoutExtension(file);

                if (Matches(keepSafe, screenName) && keepSafe != null && keepSafe.Count > 0)
                {
                    skipped.Add(new JObj().Set("name", screenName).Set("why", "protected: listed in keep_safe"));
                    continue;
                }
                if (!File.Exists(file))
                {
                    errors.Add(new JObj().Set("name", screenName).Set("error", "file not found: " + file));
                    continue;
                }

                try
                {
                    destination.Import(new FileInfo(file), opt);
                    ok.Add(screenName);
                }
                catch (Exception ex)
                {
                    errors.Add(new JObj().Set("name", screenName).Set("error", Flat(ex)));
                    // un import rifiutato puo abbattere la sessione Openness:
                    // meglio fermarsi e riferire, che tentare i file successivi
                    stopped = true;
                    break;
                }
            }

            return new JObj()
                .Set("imported", ok.Count)
                .Set("skipped", skipped.Count)
                .Set("failed", errors.Count)
                .Set("folder", destName)
                .Set("mode", overwrite ? "Override" : "None")
                .Set("stopped_on_first_error", stopped)
                .Set("names", ok)
                .Set("protected", skipped)
                .Set("errors", errors)
                .Set("hint", stopped
                    ? "Stopped at the first failure: an XML rejected for schema reasons can take TIA Portal down. " +
                      "Fix the file and run again."
                    : null);
        }

        ScreenUserFolder FindOrCreateFolder(string path)
        {
            string[] parts = path.Replace('\\', '/').Split('/');
            ScreenUserFolder current = null;

            foreach (string raw in parts)
            {
                string part = raw.Trim();
                if (part.Length == 0) continue;

                ScreenUserFolderComposition folders = current == null
                    ? target.ScreenFolder.Folders
                    : current.Folders;

                ScreenUserFolder next = null;
                foreach (ScreenUserFolder f in folders)
                    if (string.Equals(f.Name, part, StringComparison.OrdinalIgnoreCase)) { next = f; break; }

                if (next == null) next = folders.Create(part);
                current = next;
            }

            if (current == null) throw new McpError("Not a valid folder path: " + path);
            return current;
        }

        public override JObj DeleteScreens(List<string> names, List<string> keepSafe)
        {
            if (names == null || names.Count == 0) throw new McpError("No screens to delete.");

            var all = new List<ScreenAt>();
            CollectScreens(target.ScreenFolder, "", all);

            var deleted = new List<string>();
            var refused = new List<JObj>();
            var missing = new List<string>(names);

            foreach (ScreenAt s in all)
            {
                string n = s.Screen.Name;
                if (!Matches(names, n)) continue;
                missing.RemoveAll(delegate(string x) { return string.Equals(x, n, StringComparison.OrdinalIgnoreCase); });

                if (keepSafe != null && keepSafe.Count > 0 && Matches(keepSafe, n))
                {
                    refused.Add(new JObj().Set("name", n).Set("why", "protected: listed in keep_safe"));
                    continue;
                }
                try { s.Screen.Delete(); deleted.Add(n); }
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

        class TableAt
        {
            public TagTable Table;
            public string Path;
            public TagTableComposition Owner;
        }

        void CollectTables(TagSystemFolder f, string path, List<TableAt> into)
        {
            foreach (TagTable t in f.TagTables) into.Add(new TableAt { Table = t, Path = path, Owner = f.TagTables });
            foreach (TagUserFolder sub in f.Folders) CollectTables(sub, Join(path, sub.Name), into);
        }

        void CollectTables(TagUserFolder f, string path, List<TableAt> into)
        {
            foreach (TagTable t in f.TagTables) into.Add(new TableAt { Table = t, Path = path, Owner = f.TagTables });
            foreach (TagUserFolder sub in f.Folders) CollectTables(sub, Join(path, sub.Name), into);
        }

        public override List<JObj> TagTables()
        {
            var tables = new List<TableAt>();
            CollectTables(target.TagFolder, "", tables);

            var result = new List<JObj>();
            foreach (TableAt t in tables)
                result.Add(new JObj()
                    .Set("name", t.Table.Name)
                    .Set("folder", t.Path.Length == 0 ? "/" : t.Path)
                    .Set("tags", t.Table.Tags.Count)
                    .Set("system", t.Table.IsSystemObject));
            return result;
        }

        public override List<JObj> Tags(string contains, int limit, bool details)
        {
            var tables = new List<TableAt>();
            CollectTables(target.TagFolder, "", tables);

            var result = new List<JObj>();
            string tmp = details ? TempDir() : null;
            try
            {
                foreach (TableAt t in tables)
                {
                    // L'export e per TABELLA, non per tag: una volta sola qui, e poi
                    // tutti i tag di quella tabella si leggono dallo stesso documento.
                    Dictionary<string, JObj> fromXml = null;
                    if (details)
                    {
                        string file = Path.Combine(tmp, "t.xml");
                        try
                        {
                            if (File.Exists(file)) File.Delete(file);
                            t.Table.Export(new FileInfo(file), ExportOptions.WithDefaults);
                            fromXml = Xml.TagsOf(file);
                        }
                        catch { fromXml = null; }
                    }

                    foreach (Siemens.Engineering.Hmi.Tag.Tag tag in t.Table.Tags)
                    {
                        if (!Contains(tag.Name, contains)) continue;

                        var j = new JObj()
                            .Set("name", tag.Name)
                            .Set("table", t.Table.Name)
                            .Set("folder", t.Path.Length == 0 ? "/" : t.Path);

                        JObj extra;
                        if (fromXml != null && fromXml.TryGetValue(tag.Name, out extra))
                            foreach (KeyValuePair<string, object> kv in extra) j.Set(kv.Key, kv.Value);
                        else if (details)
                            j.Set("details_error", "not found in the table export");

                        result.Add(j);
                        if (limit > 0 && result.Count >= limit) return result;
                    }
                }
            }
            finally { if (tmp != null) Discard(tmp); }
            return result;
        }

        public override JObj ExportTags(string outDir, List<string> names)
        {
            EnsureDir(outDir, "out_dir");
            var tables = new List<TableAt>();
            CollectTables(target.TagFolder, "", tables);

            var files = new List<JObj>();
            var errors = new List<JObj>();
            foreach (TableAt t in tables)
            {
                if (!Matches(names, t.Table.Name)) continue;
                string path = Path.Combine(outDir, t.Table.Name + ".xml");
                try
                {
                    if (File.Exists(path)) File.Delete(path);
                    t.Table.Export(new FileInfo(path), ExportOptions.WithDefaults);
                    files.Add(new JObj().Set("table", t.Table.Name).Set("folder", t.Path).Set("file", path));
                }
                catch (Exception ex) { errors.Add(new JObj().Set("table", t.Table.Name).Set("error", Flat(ex))); }
            }
            return new JObj().Set("exported", files.Count).Set("failed", errors.Count)
                             .Set("out_dir", outDir).Set("files", files).Set("errors", errors);
        }

        public override JObj ImportTags(List<string> files, bool overwrite)
        {
            if (files == null || files.Count == 0) throw new McpError("No files to import.");
            ImportOptions opt = overwrite ? ImportOptions.Override : ImportOptions.None;

            var ok = new List<string>();
            var errors = new List<JObj>();
            foreach (string file in files)
            {
                string name = Path.GetFileNameWithoutExtension(file);
                if (!File.Exists(file)) { errors.Add(new JObj().Set("file", file).Set("error", "not found")); continue; }
                try
                {
                    // la tabella va reimportata nella cartella dove gia sta, altrimenti
                    // ne nasce una seconda con lo stesso nome in radice
                    TagTableComposition into = OwnerOfTable(name);
                    into.Import(new FileInfo(file), opt);
                    ok.Add(name);
                }
                catch (Exception ex) { errors.Add(new JObj().Set("file", file).Set("error", Flat(ex))); }
            }
            return new JObj().Set("imported", ok.Count).Set("failed", errors.Count)
                             .Set("mode", overwrite ? "Override" : "None")
                             .Set("names", ok).Set("errors", errors);
        }

        TagTableComposition OwnerOfTable(string name)
        {
            var tables = new List<TableAt>();
            CollectTables(target.TagFolder, "", tables);
            foreach (TableAt t in tables)
                if (string.Equals(t.Table.Name, name, StringComparison.OrdinalIgnoreCase)) return t.Owner;
            return target.TagFolder.TagTables;
        }

        // ----------------------------------------------------------- liste testi

        public override List<JObj> TextLists()
        {
            var result = new List<JObj>();
            foreach (TextList t in target.TextLists)
                result.Add(new JObj()
                    .Set("name", t.Name)
                    .Set("selection", TiaSession.Attr(t, "Selection"))
                    .Set("comment", TiaSession.Attr(t, "Comment")));
            return result;
        }

        public override JObj ExportTextLists(string outDir, List<string> names)
        {
            EnsureDir(outDir, "out_dir");
            var files = new List<JObj>();
            var errors = new List<JObj>();
            foreach (TextList t in target.TextLists)
            {
                if (!Matches(names, t.Name)) continue;
                string path = Path.Combine(outDir, t.Name + ".xml");
                try
                {
                    if (File.Exists(path)) File.Delete(path);
                    t.Export(new FileInfo(path), ExportOptions.WithDefaults);
                    files.Add(new JObj().Set("name", t.Name).Set("file", path));
                }
                catch (Exception ex) { errors.Add(new JObj().Set("name", t.Name).Set("error", Flat(ex))); }
            }
            return new JObj().Set("exported", files.Count).Set("failed", errors.Count)
                             .Set("out_dir", outDir).Set("files", files).Set("errors", errors);
        }

        public override JObj ImportTextLists(List<string> files, bool overwrite)
        {
            if (files == null || files.Count == 0) throw new McpError("No files to import.");
            ImportOptions opt = overwrite ? ImportOptions.Override : ImportOptions.None;

            var ok = new List<string>();
            var errors = new List<JObj>();
            foreach (string file in files)
            {
                string name = Path.GetFileNameWithoutExtension(file);
                if (!File.Exists(file)) { errors.Add(new JObj().Set("file", file).Set("error", "not found")); continue; }
                try { target.TextLists.Import(new FileInfo(file), opt); ok.Add(name); }
                catch (Exception ex) { errors.Add(new JObj().Set("file", file).Set("error", Flat(ex))); }
            }
            return new JObj().Set("imported", ok.Count).Set("failed", errors.Count)
                             .Set("mode", overwrite ? "Override" : "None")
                             .Set("names", ok).Set("errors", errors);
        }

        // ----------------------------------------------------------- connessioni

        public override List<JObj> Connections()
        {
            var result = new List<JObj>();
            foreach (Connection c in target.Connections)
            {
                var attrs = new JObj();
                try
                {
                    foreach (EngineeringAttributeInfo i in c.GetAttributeInfos())
                    {
                        string v = TiaSession.Attr(c, i.Name);
                        if (!string.IsNullOrEmpty(v)) attrs.Set(i.Name, v);
                    }
                }
                catch { }
                result.Add(new JObj().Set("name", c.Name).Set("attributes", attrs));
            }
            return result;
        }

        // --------------------------------------------------------------- allarmi

        public override List<JObj> Alarms()
        {
            throw Unsupported("hmi_alarms",
                "Openness exposes no discrete alarms, analog alarms or alarm classes for classic " +
                "WinCC: the HmiTarget object model contains no alarm composition at all. " +
                "They are configured in TIA Portal, or imported from xlsx in its alarm editor. " +
                "Only WinCC Unified exposes them through the API.");
        }
    }
}
