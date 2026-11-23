// Plc.cs - lato PLC: lettura, export e import nel progetto offline.
//
// Il server nasce per il lavoro HMI, e il PLC e il suo contesto: per capire a
// cosa punta un tag di pannello serve vedere i blocchi e le tabelle di variabili
// che ci stanno dietro.
//
// Gli import (blocchi, sorgenti esterne, tabelle di variabili, tipi di dati)
// toccano SOLO il progetto offline. Un blocco sovrascritto per errore e un
// impianto fermo, quindi le regole sono piu strette che sul lato HMI:
//   - niente sovrascrittura se non e chiesta (overwrite e false di default);
//   - mai sopra un oggetto protetto da know-how o elencato in keep_safe;
//   - rifiuto se TIA e online con la CPU;
//   - ci si ferma al primo errore.
// Qui non si scarica, non si carica e non si cancella niente dalla CPU, e non
// si salva: il salvataggio resta tia_save, il download resta un lavoro da fare
// in TIA con gli occhi sopra.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using Siemens.Engineering;
using Siemens.Engineering.Compare;
using Siemens.Engineering.HW;
using Siemens.Engineering.HW.Features;
using Siemens.Engineering.Online;
using Siemens.Engineering.SW;
using Siemens.Engineering.SW.Blocks;
using Siemens.Engineering.SW.ExternalSources;
using Siemens.Engineering.SW.Tags;
using Siemens.Engineering.SW.Types;

namespace TiaMcp
{
    public class Plc
    {
        public readonly SoftwareRef Ref;
        readonly PlcSoftware sw;

        public Plc(SoftwareRef r)
        {
            Ref = r;
            sw = (PlcSoftware)r.Software;
        }

        public string Name { get { return sw.Name; } }
        public string DeviceName { get { return Ref.Device.Name; } }

        public static bool Is(SoftwareRef r) { return r.Software is PlcSoftware; }

        // --------------------------------------------------------------- blocchi

        class BlockAt
        {
            public PlcBlock Block;
            public string Path;
            public PlcBlockGroup Group;
        }

        void Collect(PlcBlockGroup g, string path, List<BlockAt> into)
        {
            foreach (PlcBlock b in g.Blocks) into.Add(new BlockAt { Block = b, Path = path, Group = g });
            foreach (PlcBlockUserGroup sub in g.Groups) Collect(sub, Join(path, sub.Name), into);
        }

        static string Join(string a, string b) { return a.Length == 0 ? b : a + "/" + b; }

        public JObj Info()
        {
            var blocks = new List<BlockAt>();
            Collect(sw.BlockGroup, "", blocks);

            int tags = 0, tables = 0;
            foreach (JObj t in TagTables()) { tables++; tags += t.Int("tags", 0); }

            return new JObj()
                .Set("plc", sw.Name)
                .Set("device", DeviceName)
                .Set("device_type", TiaSession.Attr(Ref.Item, "TypeIdentifier"))
                .Set("counts", new JObj()
                    .Set("blocks", blocks.Count)
                    .Set("tag_tables", tables)
                    .Set("tags", tags));
        }

        public List<JObj> Blocks(string contains, string kind, int limit)
        {
            var all = new List<BlockAt>();
            Collect(sw.BlockGroup, "", all);

            var result = new List<JObj>();
            foreach (BlockAt b in all)
            {
                string type = b.Block.GetType().Name;   // OB, FB, FC, GlobalDB, InstanceDB, ...
                if (contains != null && contains.Length > 0 &&
                    b.Block.Name.IndexOf(contains, StringComparison.OrdinalIgnoreCase) < 0) continue;
                if (kind != null && kind.Length > 0 &&
                    type.IndexOf(kind, StringComparison.OrdinalIgnoreCase) < 0) continue;

                result.Add(new JObj()
                    .Set("name", b.Block.Name)
                    .Set("type", type)
                    .Set("number", b.Block.Number)
                    .Set("group", b.Path.Length == 0 ? "/" : b.Path)
                    .Set("language", b.Block.ProgrammingLanguage.ToString())
                    .Set("consistent", b.Block.IsConsistent)
                    .Set("know_how_protected", b.Block.IsKnowHowProtected)
                    .Set("modified", Safe(b.Block.ModifiedDate)));

                if (limit > 0 && result.Count >= limit) break;
            }
            return result;
        }

        static string Safe(DateTime d)
        {
            try { return d.ToString("yyyy-MM-dd HH:mm:ss"); }
            catch { return null; }
        }

        public JObj ExportBlocks(string outDir, List<string> names)
        {
            if (names == null || names.Count == 0) throw new McpError("Name at least one block to export.");
            if (string.IsNullOrEmpty(outDir)) throw new McpError("Missing out_dir.");
            if (!Directory.Exists(outDir)) Directory.CreateDirectory(outDir);

            var all = new List<BlockAt>();
            Collect(sw.BlockGroup, "", all);

            var files = new List<JObj>();
            var errors = new List<JObj>();
            var missing = new List<string>(names);

            foreach (BlockAt b in all)
            {
                bool wanted = false;
                foreach (string n in names)
                    if (string.Equals(n, b.Block.Name, StringComparison.OrdinalIgnoreCase)) wanted = true;
                if (!wanted) continue;

                string bn = b.Block.Name;
                missing.RemoveAll(delegate(string x) { return string.Equals(x, bn, StringComparison.OrdinalIgnoreCase); });

                if (b.Block.IsKnowHowProtected)
                {
                    errors.Add(new JObj().Set("name", bn).Set("error", "know-how protected: cannot be exported"));
                    continue;
                }

                string file = Path.Combine(outDir, Sanitize(bn) + ".xml");
                try
                {
                    if (File.Exists(file)) File.Delete(file);
                    b.Block.Export(new FileInfo(file), ExportOptions.WithDefaults);
                    files.Add(new JObj().Set("name", bn).Set("group", b.Path).Set("file", file));
                }
                catch (Exception ex) { errors.Add(new JObj().Set("name", bn).Set("error", Flat(ex))); }
            }

            return new JObj()
                .Set("exported", files.Count)
                .Set("failed", errors.Count)
                .Set("not_found", missing)
                .Set("out_dir", outDir)
                .Set("files", files)
                .Set("errors", errors);
        }

        static string Sanitize(string name)
        {
            var sb = new System.Text.StringBuilder(name.Length);
            foreach (char c in name)
                sb.Append(Array.IndexOf(Path.GetInvalidFileNameChars(), c) >= 0 ? '_' : c);
            return sb.ToString();
        }

        // ------------------------------------------------- confronto online/offline

        // Il confronto e quello di TIA (Openness CompareToOnline): legge dalla CPU, non
        // carica e non scarica niente, non salva il progetto. La connessione e quella
        // memorizzata nel progetto; se manca o non porta alla CPU da questa macchina si
        // usa l'interfaccia PG/PC nella sottorete della CPU (o quella indicata). Se la
        // CPU era offline la si porta online solo per il confronto e poi offline.

        public JObj CompareOnline(bool includeIdentical, int limit, string modeName, string pcInterface)
        {
            OnlineProvider online = null;
            try { online = Ref.Item.GetService<OnlineProvider>(); } catch { online = null; }
            if (online == null)
                throw new McpError("The CPU '" + DeviceName + "' offers no online connection through Openness.");

            bool wentOnline = false;
            OnlineState before = online.State;
            JObj connection = null;
            if (before != OnlineState.Online)
            {
                connection = Configure(online.Configuration, modeName, pcInterface, false, CpuAddresses());
                OnlineState now;
                try { now = online.GoOnline(); }
                catch (Exception first)
                {
                    // la connessione memorizzata nel progetto non porta alla CPU da questa macchina:
                    // si riprova una volta con l'interfaccia nella sottorete della CPU
                    if (connection.Str("source") != "stored in the project") throw Fail(connection, first);
                    try
                    {
                        connection = Configure(online.Configuration, modeName, pcInterface, true, CpuAddresses());
                        now = online.GoOnline();
                    }
                    catch (McpError) { throw; }
                    catch (Exception ex) { throw Fail(connection, ex); }
                }
                wentOnline = true;
                if (now != OnlineState.Online)
                {
                    try { online.GoOffline(); } catch { }
                    throw new McpError("Going online with '" + DeviceName + "' ended in state " + now +
                                       (now == OnlineState.Protected ? " (the CPU needs a read access password: go online by hand in TIA Portal first)" : "") + ".");
                }
            }
            return Compare(online, before, wentOnline, connection, includeIdentical, limit);
        }

        McpError Fail(JObj connection, Exception ex)
        {
            return new McpError("Cannot go online with '" + DeviceName + "' (" + Json.Write(connection) + "): " + Flat(ex) +
                                ". Check that the CPU answers from this machine, or name the PG/PC interface with pc_interface.");
        }

        JObj Compare(OnlineProvider online, OnlineState before, bool wentOnline, JObj connection, bool includeIdentical, int limit)
        {
            var started = DateTime.Now;
            var items = new List<JObj>();
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            int listed = 0, total = 0;
            try
            {
                CompareResult result = sw.CompareToOnline();
                Walk(result.RootElement, "", includeIdentical, limit, items, counts, ref listed, ref total);
            }
            catch (McpError) { throw; }
            catch (Exception ex) { throw new McpError("Compare with the CPU failed: " + Flat(ex)); }
            finally
            {
                if (wentOnline) { try { online.GoOffline(); } catch { } }
            }

            var summary = new JObj();
            foreach (var kv in counts) summary.Set(kv.Key, kv.Value);
            return new JObj()
                .Set("plc", DeviceName)
                .Set("online_before", before.ToString())
                .Set("went_online_for_compare", wentOnline)
                .Set("connection", connection)
                .Set("duration_s", Math.Round((DateTime.Now - started).TotalSeconds, 1))
                .Set("summary", summary)
                .Set("objects", total)
                .Set("listed", items.Count)
                .Set("truncated", listed > items.Count)
                .Set("items", items)
                .Set("legend", new JObj()
                    .Set("left", "project (offline)")
                    .Set("right", "CPU (online)")
                    .Set("LeftMissing", "only in the CPU")
                    .Set("RightMissing", "only in the project"))
                .Set("note", "Read only: TIA compared the project with the CPU; nothing was downloaded, uploaded or saved.");
        }

        // Connessione online: quella gia memorizzata nel progetto, oppure (pc_interface
        // indicata, o nessuna configurata) l'interfaccia PG/PC di questa macchina che sta
        // nella stessa sottorete /24 dell'indirizzo della CPU. ApplyConfiguration cambia
        // solo l'impostazione in memoria: il progetto non viene salvato.
        static JObj Configure(Siemens.Engineering.Connection.ConnectionConfiguration cfg, string modeName, string pcInterface,
                              bool force, List<string> cpuAddresses)
        {
            if (cfg == null) return new JObj().Set("source", "none");
            if (cfg.IsConfigured && string.IsNullOrEmpty(pcInterface) && !force)
                return new JObj().Set("source", "stored in the project");

            string wantedMode = string.IsNullOrEmpty(modeName) ? "PN/IE" : modeName;
            Siemens.Engineering.Connection.ConfigurationMode mode = null;
            var modes = new List<string>();
            foreach (Siemens.Engineering.Connection.ConfigurationMode m in cfg.Modes)
            {
                modes.Add(m.Name);
                if (string.Equals(m.Name, wantedMode, StringComparison.OrdinalIgnoreCase)) mode = m;
            }
            if (mode == null)
                throw new McpError("No connection mode '" + wantedMode + "'. Available: " + string.Join(", ", modes.ToArray()));

            var seen = new List<string>();
            foreach (Siemens.Engineering.Connection.ConfigurationPcInterface pc in mode.PcInterfaces)
            {
                string label = pc.Name + " #" + pc.Number;
                // Openness non da gli IP delle schede del PC: si prendono da Windows
                List<string> own = LocalAddresses(pc.Name, pc.Number);
                foreach (Siemens.Engineering.Connection.ConfigurationAddress a in pc.Addresses) own.Add(a.Name);

                var targets = new List<string>();
                Siemens.Engineering.Connection.ConfigurationTargetInterface chosen = null;
                string chosenAddress = null;
                foreach (Siemens.Engineering.Connection.ConfigurationTargetInterface ti in pc.TargetInterfaces)
                {
                    var tAddr = new List<string>();
                    foreach (Siemens.Engineering.Connection.ConfigurationAddress ta in ti.Addresses) tAddr.Add(ta.Name);
                    targets.Add(ti.Name + (tAddr.Count > 0 ? " (" + string.Join(", ", tAddr.ToArray()) + ")" : ""));
                    if (tAddr.Count == 0) tAddr.AddRange(cpuAddresses);
                    foreach (string t in tAddr)
                        foreach (string o in own)
                            if (chosen == null && SameSubnet(o, t)) { chosen = ti; chosenAddress = t; }
                    if (chosen == null && !string.IsNullOrEmpty(pcInterface) && Matches(label, pcInterface))
                    {
                        chosen = ti;
                        chosenAddress = tAddr.Count > 0 ? tAddr[0] : null;
                    }
                }
                seen.Add(label + " [" + string.Join(", ", own.ToArray()) + "] -> " + string.Join(", ", targets.ToArray()));
                if (!string.IsNullOrEmpty(pcInterface) && !Matches(label, pcInterface)) continue;
                if (chosen == null || !cfg.ApplyConfiguration(chosen)) continue;
                return new JObj().Set("source", "set for this compare (not saved)")
                    .Set("mode", mode.Name).Set("pc_interface", label).Set("pc_addresses", own)
                    .Set("target_interface", chosen.Name).Set("cpu_address", chosenAddress);
            }
            throw new McpError("No PG/PC interface of this machine reaches the CPU (" + string.Join(", ", cpuAddresses.ToArray()) +
                               ") in mode " + mode.Name +
                               (string.IsNullOrEmpty(pcInterface) ? "" : " matching '" + pcInterface + "'") +
                               ". Interfaces: " + string.Join("; ", seen.ToArray()) + ". Name one with pc_interface (e.g. '#3').");
        }

        static bool Matches(string label, string wanted)
        {
            return label.IndexOf(wanted, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>IPv4 della scheda di Windows che TIA chiama "nome" numero n ("nome" per 1, "nome #n" per gli altri).</summary>
        static List<string> LocalAddresses(string name, int number)
        {
            var result = new List<string>();
            foreach (System.Net.NetworkInformation.NetworkInterface nic in
                     System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
            {
                string d = nic.Description ?? "";
                bool same = number <= 1 ? string.Equals(d, name, StringComparison.OrdinalIgnoreCase)
                                        : string.Equals(d, name + " #" + number, StringComparison.OrdinalIgnoreCase);
                if (!same) continue;
                foreach (System.Net.NetworkInformation.UnicastIPAddressInformation u in nic.GetIPProperties().UnicastAddresses)
                    if (u.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork) result.Add(u.Address.ToString());
            }
            return result;
        }

        /// <summary>Indirizzi IP delle interfacce della stazione della CPU, dal progetto.</summary>
        List<string> CpuAddresses()
        {
            var result = new List<string>();
            foreach (DeviceItem item in Ref.Device.DeviceItems) CollectAddresses(item, result);
            return result;
        }

        static void CollectAddresses(DeviceItem item, List<string> into)
        {
            NetworkInterface ni = null;
            try { ni = item.GetService<NetworkInterface>(); } catch { }
            if (ni != null)
                foreach (Node n in ni.Nodes)
                {
                    string a = TiaSession.Attr(n, "Address");
                    if (!string.IsNullOrEmpty(a) && a.Split('.').Length == 4 && !into.Contains(a)) into.Add(a);
                }
            foreach (DeviceItem sub in item.DeviceItems) CollectAddresses(sub, into);
        }

        static bool SameSubnet(string a, string b)
        {
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
            string[] x = a.Split('.'), y = b.Split('.');
            return x.Length == 4 && y.Length == 4 && x[0] == y[0] && x[1] == y[1] && x[2] == y[2];
        }

        static void Walk(CompareResultElement e, string path, bool includeIdentical, int limit, List<JObj> into,
                         Dictionary<string, int> counts, ref int listed, ref int total)
        {
            if (e == null) return;
            string name = !string.IsNullOrEmpty(e.LeftName) ? e.LeftName : e.RightName;
            string here = path.Length == 0 ? (name ?? "") : Join(path, name ?? "");
            CompareResultState state = e.ComparisonResult;
            bool folder = e.Elements != null && e.Elements.Count > 0;

            if (!folder && path.Length > 0)
            {
                total++;
                string key = state.ToString();
                int n; counts.TryGetValue(key, out n); counts[key] = n + 1;
                bool same = state == CompareResultState.ObjectsIdentical || state == CompareResultState.FolderContentsIdentical ||
                            state == CompareResultState.CompareIrrelevant;
                if (includeIdentical || !same)
                {
                    listed++;
                    if (limit <= 0 || into.Count < limit)
                    {
                        string detail = null;
                        try { detail = e.DetailedInformation; } catch { }
                        into.Add(new JObj()
                            .Set("object", name)
                            .Set("path", path)
                            .Set("state", key)
                            .Set("project", e.LeftName)
                            .Set("cpu", e.RightName)
                            .Set("detail", string.IsNullOrEmpty(detail) ? null : detail));
                    }
                }
            }
            else if (folder && (state == CompareResultState.FolderContainsDifferencesOwnStateDifferent ||
                                state == CompareResultState.FolderContentEqualOwnStateDifferent) && path.Length > 0)
            {
                // la cartella stessa (es. la CPU, una cartella di blocchi) e diversa: va detto
                listed++;
                if (limit <= 0 || into.Count < limit)
                    into.Add(new JObj().Set("object", name).Set("path", path).Set("state", state.ToString())
                                       .Set("project", e.LeftName).Set("cpu", e.RightName).Set("folder", true));
            }

            if (folder)
                foreach (CompareResultElement child in e.Elements)
                    Walk(child, here, includeIdentical, limit, into, counts, ref listed, ref total);
        }

        // ------------------------------------------------------------------ tag

        public List<JObj> TagTables()
        {
            var result = new List<JObj>();
            CollectTables(sw.TagTableGroup, "", result);
            return result;
        }

        void CollectTables(PlcTagTableGroup g, string path, List<JObj> into)
        {
            foreach (PlcTagTable t in g.TagTables)
                into.Add(new JObj()
                    .Set("name", t.Name)
                    .Set("group", path.Length == 0 ? "/" : path)
                    .Set("tags", t.Tags.Count));
            foreach (PlcTagTableUserGroup sub in g.Groups) CollectTables(sub, Join(path, sub.Name), into);
        }

        public List<JObj> Tags(string contains, int limit)
        {
            var result = new List<JObj>();
            CollectTags(sw.TagTableGroup, "", contains, limit, result);
            return result;
        }

        void CollectTags(PlcTagTableGroup g, string path, string contains, int limit, List<JObj> into)
        {
            foreach (PlcTagTable t in g.TagTables)
            {
                foreach (PlcTag tag in t.Tags)
                {
                    if (contains != null && contains.Length > 0 &&
                        tag.Name.IndexOf(contains, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    into.Add(new JObj()
                        .Set("name", tag.Name)
                        .Set("table", t.Name)
                        .Set("group", path.Length == 0 ? "/" : path)
                        .Set("address", tag.LogicalAddress)
                        .Set("data_type", tag.DataTypeName)
                        .Set("externally_accessible", tag.ExternalAccessible)
                        .Set("externally_writable", tag.ExternalWritable));
                    if (limit > 0 && into.Count >= limit) return;
                }
            }
            foreach (PlcTagTableUserGroup sub in g.Groups)
            {
                CollectTags(sub, Join(path, sub.Name), contains, limit, into);
                if (limit > 0 && into.Count >= limit) return;
            }
        }

        // ============================================================== import
        //
        // Tutto quello che segue scrive nel progetto offline. Le regole comuni
        // stanno in ImportXml; ogni tipo di oggetto dice solo dove cercare
        // l'omonimo e dove creare un oggetto nuovo.

        delegate void Importer(FileInfo file, ImportOptions options);

        class Existing
        {
            public string Path;
            public bool Protected;
            public Importer Into;
        }

        static bool Listed(List<string> names, string name)
        {
            if (names == null || name == null) return false;
            foreach (string n in names)
                if (string.Equals(n, name, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        static List<string> Segments(string path)
        {
            var result = new List<string>();
            if (string.IsNullOrEmpty(path)) return result;
            foreach (string raw in path.Replace('\\', '/').Split('/'))
            {
                string part = raw.Trim();
                if (part.Length > 0) result.Add(part);
            }
            return result;
        }

        /// <summary>Gli import si fanno solo offline: con la CPU online TIA confronta e segnala, e non e il momento di cambiare il progetto.</summary>
        void RequireOffline(string tool)
        {
            OnlineProvider online = null;
            try { online = Ref.Item.GetService<OnlineProvider>(); } catch { online = null; }
            if (online == null) return;

            OnlineState state;
            try { state = online.State; } catch { return; }
            if (state != OnlineState.Offline)
                throw new McpError(tool + " refused: TIA Portal is online with '" + DeviceName + "' (state " + state +
                                   "). Go offline first; this server only changes the offline project.");
        }

        /// <summary>
        /// Import di file XML uno alla volta. prefix e l'inizio del nome dell'elemento
        /// atteso (SW.Blocks., SW.Types., SW.Tags.PlcTagTable): un file di un altro
        /// genere viene rifiutato prima di arrivare a TIA.
        /// </summary>
        JObj ImportXml(string tool, string what, string prefix, List<string> files, bool overwrite,
                       List<string> keepSafe, string group, Func<string, Existing> find,
                       Func<Importer> destination, Action<JObj> annotate)
        {
            if (files == null || files.Count == 0) throw new McpError("No files to import.");
            RequireOffline(tool);

            ImportOptions opt = overwrite ? ImportOptions.Override : ImportOptions.None;
            var ok = new List<JObj>();
            var refused = new List<JObj>();
            var errors = new List<JObj>();
            bool stopped = false;
            Importer fresh = null;
            string freshPath = string.Join("/", Segments(group).ToArray());

            foreach (string file in files)
            {
                if (!File.Exists(file))
                {
                    errors.Add(new JObj().Set("file", file).Set("error", "file not found"));
                    continue;
                }

                string element, name;
                try
                {
                    JObj root = Xml.RootObject(file);
                    element = root.Str("element");
                    name = root.Str("name");
                }
                catch (Exception ex)
                {
                    errors.Add(new JObj().Set("file", file).Set("error", "not a readable XML: " + Flat(ex)));
                    continue;
                }

                if (element == null || !element.StartsWith(prefix, StringComparison.Ordinal))
                {
                    refused.Add(new JObj().Set("file", file)
                        .Set("why", "not a " + what + " export (found " + (element ?? "nothing") + ")"));
                    continue;
                }
                if (string.IsNullOrEmpty(name))
                {
                    refused.Add(new JObj().Set("file", file)
                        .Set("why", "cannot read the object name from the XML, so cannot tell what it would overwrite"));
                    continue;
                }
                if (Listed(keepSafe, name))
                {
                    refused.Add(new JObj().Set("file", file).Set("name", name).Set("why", "protected: listed in keep_safe"));
                    continue;
                }

                // si ricerca a ogni file: l'import precedente ha cambiato l'albero
                Existing existing = find(name);
                Importer into;
                string where;
                if (existing != null)
                {
                    if (existing.Protected)
                    {
                        refused.Add(new JObj().Set("file", file).Set("name", name)
                            .Set("why", "the " + what + " in the project is know-how protected"));
                        continue;
                    }
                    if (!overwrite)
                    {
                        refused.Add(new JObj().Set("file", file).Set("name", name)
                            .Set("why", "already in the project (group '" + (existing.Path.Length == 0 ? "/" : existing.Path) +
                                        "'): pass overwrite=true to replace it"));
                        continue;
                    }
                    // l'Override distrugge l'oggetto vecchio: si reimporta dove stava
                    into = existing.Into;
                    where = existing.Path;
                }
                else
                {
                    if (fresh == null) fresh = destination();
                    into = fresh;
                    where = freshPath;
                }

                try
                {
                    into(new FileInfo(file), opt);
                    ok.Add(new JObj().Set("name", name).Set("type", element.Substring(element.LastIndexOf('.') + 1))
                        .Set("group", where.Length == 0 ? "/" : where)
                        .Set("replaced", existing != null).Set("file", file));
                }
                catch (Exception ex)
                {
                    errors.Add(new JObj().Set("file", file).Set("name", name).Set("error", Flat(ex)));
                    // un import rifiutato puo abbattere la sessione Openness:
                    // meglio fermarsi e riferire, che tentare i file successivi
                    stopped = true;
                    break;
                }
            }

            if (annotate != null)
                foreach (JObj o in ok) { try { annotate(o); } catch { } }

            return new JObj()
                .Set("plc", DeviceName)
                .Set("imported", ok.Count)
                .Set("refused", refused.Count)
                .Set("failed", errors.Count)
                .Set("mode", overwrite ? "Override" : "None")
                .Set("stopped_on_first_error", stopped)
                .Set("objects", ok)
                .Set("refusals", refused)
                .Set("errors", errors)
                .Set("note", ok.Count == 0 ? null :
                    "Offline project only, in memory: nothing was compiled, saved or downloaded. " +
                    "Run tia_compile, check the result, then tia_save. The download to the CPU is done by hand in TIA Portal.");
        }

        // ------------------------------------------------------ import blocchi

        BlockAt FindBlock(string name)
        {
            var all = new List<BlockAt>();
            Collect(sw.BlockGroup, "", all);
            foreach (BlockAt b in all)
                if (string.Equals(b.Block.Name, name, StringComparison.OrdinalIgnoreCase)) return b;
            return null;
        }

        PlcBlockGroup BlockGroupAt(string path)
        {
            PlcBlockGroup current = sw.BlockGroup;
            foreach (string part in Segments(path))
            {
                PlcBlockUserGroup next = null;
                foreach (PlcBlockUserGroup g in current.Groups)
                    if (string.Equals(g.Name, part, StringComparison.OrdinalIgnoreCase)) { next = g; break; }
                if (next == null) next = current.Groups.Create(part);
                current = next;
            }
            return current;
        }

        public JObj ImportBlocks(List<string> files, bool overwrite, List<string> keepSafe, string group)
        {
            return ImportXml("plc_import_blocks", "block", "SW.Blocks.", files, overwrite, keepSafe, group,
                delegate(string name)
                {
                    BlockAt b = FindBlock(name);
                    if (b == null) return null;
                    PlcBlockComposition blocks = b.Group.Blocks;
                    return new Existing
                    {
                        Path = b.Path,
                        Protected = b.Block.IsKnowHowProtected,
                        Into = delegate(FileInfo f, ImportOptions o) { blocks.Import(f, o); }
                    };
                },
                delegate
                {
                    PlcBlockComposition blocks = BlockGroupAt(group).Blocks;
                    return delegate(FileInfo f, ImportOptions o) { blocks.Import(f, o); };
                },
                delegate(JObj o)
                {
                    BlockAt b = FindBlock(o.Str("name"));
                    if (b == null) return;
                    o.Set("number", b.Block.Number).Set("consistent", b.Block.IsConsistent);
                });
        }

        // ------------------------------------------------------ sorgenti esterne

        static string SourceExtension(string language)
        {
            switch ((language ?? "").ToUpperInvariant())
            {
                case "SCL": return ".scl";
                case "STL": return ".awl";
                case "DB": return ".db";
                default: return null;
            }
        }

        /// <summary>Sorgente di testo generata da TIA (SCL, AWL, DB, UDT): un file per oggetto. Non modifica il progetto.</summary>
        public JObj ExportSources(string outDir, List<string> names)
        {
            if (names == null || names.Count == 0) throw new McpError("Name at least one block or data type.");
            if (string.IsNullOrEmpty(outDir)) throw new McpError("Missing out_dir.");
            if (!Directory.Exists(outDir)) Directory.CreateDirectory(outDir);

            var files = new List<JObj>();
            var errors = new List<JObj>();
            var missing = new List<string>();

            foreach (string wanted in names)
            {
                BlockAt b = FindBlock(wanted);
                TypeAt t = b == null ? FindType(wanted) : null;
                if (b == null && t == null) { missing.Add(wanted); continue; }

                string name = b != null ? b.Block.Name : t.Type.Name;
                try
                {
                    string file;
                    if (b != null)
                    {
                        if (b.Block.IsKnowHowProtected) throw new McpError("know-how protected: no source can be generated");
                        string language = b.Block.ProgrammingLanguage.ToString();
                        string ext = SourceExtension(language);
                        if (ext == null)
                            throw new McpError("language " + language + " has no text source: use plc_export_blocks (XML)");
                        file = Path.Combine(outDir, Sanitize(name) + ext);
                        if (File.Exists(file)) File.Delete(file);
                        sw.ExternalSourceGroup.GenerateSource(new List<PlcBlock> { b.Block }, new FileInfo(file));
                        files.Add(new JObj().Set("name", name).Set("kind", b.Block.GetType().Name)
                            .Set("language", language).Set("file", file));
                    }
                    else
                    {
                        file = Path.Combine(outDir, Sanitize(name) + ".udt");
                        if (File.Exists(file)) File.Delete(file);
                        sw.ExternalSourceGroup.GenerateSource(new List<PlcType> { t.Type }, new FileInfo(file));
                        files.Add(new JObj().Set("name", name).Set("kind", "PlcType").Set("file", file));
                    }
                }
                catch (Exception ex) { errors.Add(new JObj().Set("name", name).Set("error", Flat(ex))); }
            }

            return new JObj()
                .Set("exported", files.Count)
                .Set("failed", errors.Count)
                .Set("not_found", missing)
                .Set("out_dir", outDir)
                .Set("files", files)
                .Set("errors", errors);
        }

        static readonly Regex Declaration = new Regex(
            "^\\s*(FUNCTION_BLOCK|FUNCTION|DATA_BLOCK|ORGANIZATION_BLOCK|TYPE)\\s+(?:\"([^\"]+)\"|([^\\s:;]+))",
            RegexOptions.IgnoreCase | RegexOptions.Multiline);

        /// <summary>I nomi dei blocchi e dei tipi che una sorgente dichiara: sono quelli che la generazione creerebbe o sovrascriverebbe.</summary>
        static List<string> Declared(string text)
        {
            var result = new List<string>();
            foreach (Match m in Declaration.Matches(text))
            {
                string name = m.Groups[2].Success ? m.Groups[2].Value : m.Groups[3].Value;
                if (name.Length > 0 && !result.Contains(name)) result.Add(name);
            }
            return result;
        }

        PlcExternalSource FindSource(string name)
        {
            foreach (PlcExternalSource s in sw.ExternalSourceGroup.ExternalSources)
                if (string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase)) return s;
            return null;
        }

        public JObj ImportSources(List<string> files, bool overwrite, List<string> keepSafe, bool keepSource)
        {
            if (files == null || files.Count == 0) throw new McpError("No files to import.");
            RequireOffline("plc_import_sources");

            var ok = new List<JObj>();
            var refused = new List<JObj>();
            var errors = new List<JObj>();
            bool stopped = false;

            foreach (string file in files)
            {
                if (!File.Exists(file))
                {
                    errors.Add(new JObj().Set("file", file).Set("error", "file not found"));
                    continue;
                }
                string ext = (Path.GetExtension(file) ?? "").ToLowerInvariant();
                if (ext != ".scl" && ext != ".awl" && ext != ".db" && ext != ".udt")
                {
                    refused.Add(new JObj().Set("file", file).Set("why", "not a source file (.scl, .awl, .db, .udt)"));
                    continue;
                }

                List<string> declared = Declared(File.ReadAllText(file));
                if (declared.Count == 0)
                {
                    refused.Add(new JObj().Set("file", file)
                        .Set("why", "no block or type declaration found, so cannot tell what it would overwrite"));
                    continue;
                }

                // cosa verrebbe sovrascritto: la generazione sostituisce senza chiedere
                string why = null;
                var replaces = new List<string>();
                foreach (string name in declared)
                {
                    if (Listed(keepSafe, name)) { why = "'" + name + "' is protected: listed in keep_safe"; break; }
                    BlockAt b = FindBlock(name);
                    TypeAt t = b == null ? FindType(name) : null;
                    if (b == null && t == null) continue;
                    if (b != null ? b.Block.IsKnowHowProtected : t.Protected)
                    {
                        why = "'" + name + "' in the project is know-how protected";
                        break;
                    }
                    if (!overwrite) { why = "'" + name + "' is already in the project: pass overwrite=true to replace it"; break; }
                    replaces.Add(name);
                }

                string sourceName = Path.GetFileName(file);
                PlcExternalSource old = why == null ? FindSource(sourceName) : null;
                if (old != null && !overwrite)
                    why = "an external source named '" + sourceName + "' is already in the project: pass overwrite=true to replace it";

                if (why != null)
                {
                    refused.Add(new JObj().Set("file", file).Set("declares", declared).Set("why", why));
                    continue;
                }

                PlcExternalSource source = null;
                try
                {
                    if (old != null) old.Delete();
                    source = sw.ExternalSourceGroup.ExternalSources.CreateFromFile(sourceName, file);
                    source.GenerateBlocksFromSource();
                    ok.Add(new JObj().Set("file", file).Set("generated", declared).Set("replaced", replaces)
                        .Set("source_kept", keepSource));
                }
                catch (Exception ex)
                {
                    errors.Add(new JObj().Set("file", file).Set("declares", declared).Set("error", Flat(ex)));
                    stopped = true;
                }
                finally
                {
                    if (source != null && !keepSource) { try { source.Delete(); } catch { } }
                }
                if (stopped) break;
            }

            foreach (JObj o in ok)
            {
                var state = new List<JObj>();
                foreach (string name in (List<string>)o.Get("generated"))
                {
                    BlockAt b = FindBlock(name);
                    if (b != null)
                        state.Add(new JObj().Set("name", b.Block.Name).Set("type", b.Block.GetType().Name)
                            .Set("number", b.Block.Number).Set("consistent", b.Block.IsConsistent));
                    else if (FindType(name) != null)
                        state.Add(new JObj().Set("name", name).Set("type", "PlcType"));
                    else
                        state.Add(new JObj().Set("name", name).Set("missing", true));
                }
                o.Set("objects", state);
            }

            return new JObj()
                .Set("plc", DeviceName)
                .Set("imported", ok.Count)
                .Set("refused", refused.Count)
                .Set("failed", errors.Count)
                .Set("overwrite", overwrite)
                .Set("stopped_on_first_error", stopped)
                .Set("sources", ok)
                .Set("refusals", refused)
                .Set("errors", errors)
                .Set("note", ok.Count == 0 ? null :
                    "Offline project only, in memory: nothing was compiled, saved or downloaded. " +
                    "Run tia_compile, check the result, then tia_save. The download to the CPU is done by hand in TIA Portal.");
        }

        // ------------------------------------------------- tabelle di variabili

        class TableAt
        {
            public PlcTagTable Table;
            public string Path;
            public PlcTagTableGroup Group;
        }

        void CollectTableObjects(PlcTagTableGroup g, string path, List<TableAt> into)
        {
            foreach (PlcTagTable t in g.TagTables) into.Add(new TableAt { Table = t, Path = path, Group = g });
            foreach (PlcTagTableUserGroup sub in g.Groups) CollectTableObjects(sub, Join(path, sub.Name), into);
        }

        PlcTagTableGroup TagTableGroupAt(string path)
        {
            PlcTagTableGroup current = sw.TagTableGroup;
            foreach (string part in Segments(path))
            {
                PlcTagTableUserGroup next = null;
                foreach (PlcTagTableUserGroup g in current.Groups)
                    if (string.Equals(g.Name, part, StringComparison.OrdinalIgnoreCase)) { next = g; break; }
                if (next == null) next = current.Groups.Create(part);
                current = next;
            }
            return current;
        }

        public JObj ExportTagTables(string outDir, List<string> names)
        {
            if (string.IsNullOrEmpty(outDir)) throw new McpError("Missing out_dir.");
            if (!Directory.Exists(outDir)) Directory.CreateDirectory(outDir);

            var all = new List<TableAt>();
            CollectTableObjects(sw.TagTableGroup, "", all);

            var files = new List<JObj>();
            var errors = new List<JObj>();
            var missing = names == null ? new List<string>() : new List<string>(names);

            foreach (TableAt t in all)
            {
                string tn = t.Table.Name;
                if (names != null && names.Count > 0 && !Listed(names, tn)) continue;
                missing.RemoveAll(delegate(string x) { return string.Equals(x, tn, StringComparison.OrdinalIgnoreCase); });

                string file = Path.Combine(outDir, Sanitize(tn) + ".xml");
                try
                {
                    if (File.Exists(file)) File.Delete(file);
                    t.Table.Export(new FileInfo(file), ExportOptions.WithDefaults);
                    files.Add(new JObj().Set("table", tn).Set("group", t.Path.Length == 0 ? "/" : t.Path).Set("file", file));
                }
                catch (Exception ex) { errors.Add(new JObj().Set("table", tn).Set("error", Flat(ex))); }
            }

            return new JObj()
                .Set("exported", files.Count)
                .Set("failed", errors.Count)
                .Set("not_found", missing)
                .Set("out_dir", outDir)
                .Set("files", files)
                .Set("errors", errors);
        }

        public JObj ImportTagTables(List<string> files, bool overwrite, List<string> keepSafe, string group)
        {
            return ImportXml("plc_import_tag_tables", "PLC tag table", "SW.Tags.PlcTagTable", files, overwrite, keepSafe, group,
                delegate(string name)
                {
                    var all = new List<TableAt>();
                    CollectTableObjects(sw.TagTableGroup, "", all);
                    foreach (TableAt t in all)
                    {
                        if (!string.Equals(t.Table.Name, name, StringComparison.OrdinalIgnoreCase)) continue;
                        PlcTagTableComposition tables = t.Group.TagTables;
                        return new Existing
                        {
                            Path = t.Path,
                            Protected = false,
                            Into = delegate(FileInfo f, ImportOptions o) { tables.Import(f, o); }
                        };
                    }
                    return null;
                },
                delegate
                {
                    PlcTagTableComposition tables = TagTableGroupAt(group).TagTables;
                    return delegate(FileInfo f, ImportOptions o) { tables.Import(f, o); };
                },
                null);
        }

        // ------------------------------------------------------- tipi di dati

        class TypeAt
        {
            public PlcType Type;
            public string Path;
            public PlcTypeGroup Group;
            public bool Protected { get { return TiaSession.Attr(Type, "IsKnowHowProtected") == "True"; } }
        }

        void CollectTypes(PlcTypeGroup g, string path, List<TypeAt> into)
        {
            foreach (PlcType t in g.Types) into.Add(new TypeAt { Type = t, Path = path, Group = g });
            foreach (PlcTypeUserGroup sub in g.Groups) CollectTypes(sub, Join(path, sub.Name), into);
        }

        TypeAt FindType(string name)
        {
            var all = new List<TypeAt>();
            CollectTypes(sw.TypeGroup, "", all);
            foreach (TypeAt t in all)
                if (string.Equals(t.Type.Name, name, StringComparison.OrdinalIgnoreCase)) return t;
            return null;
        }

        PlcTypeGroup TypeGroupAt(string path)
        {
            PlcTypeGroup current = sw.TypeGroup;
            foreach (string part in Segments(path))
            {
                PlcTypeUserGroup next = null;
                foreach (PlcTypeUserGroup g in current.Groups)
                    if (string.Equals(g.Name, part, StringComparison.OrdinalIgnoreCase)) { next = g; break; }
                if (next == null) next = current.Groups.Create(part);
                current = next;
            }
            return current;
        }

        public List<JObj> Types(string contains, int limit)
        {
            var all = new List<TypeAt>();
            CollectTypes(sw.TypeGroup, "", all);

            var result = new List<JObj>();
            foreach (TypeAt t in all)
            {
                if (contains != null && contains.Length > 0 &&
                    t.Type.Name.IndexOf(contains, StringComparison.OrdinalIgnoreCase) < 0) continue;
                result.Add(new JObj()
                    .Set("name", t.Type.Name)
                    .Set("group", t.Path.Length == 0 ? "/" : t.Path)
                    .Set("consistent", TiaSession.Attr(t.Type, "IsConsistent"))
                    .Set("know_how_protected", t.Protected)
                    .Set("modified", TiaSession.Attr(t.Type, "ModifiedDate")));
                if (limit > 0 && result.Count >= limit) break;
            }
            return result;
        }

        public JObj ExportTypes(string outDir, List<string> names)
        {
            if (string.IsNullOrEmpty(outDir)) throw new McpError("Missing out_dir.");
            if (!Directory.Exists(outDir)) Directory.CreateDirectory(outDir);

            var all = new List<TypeAt>();
            CollectTypes(sw.TypeGroup, "", all);

            var files = new List<JObj>();
            var errors = new List<JObj>();
            var missing = names == null ? new List<string>() : new List<string>(names);

            foreach (TypeAt t in all)
            {
                string tn = t.Type.Name;
                if (names != null && names.Count > 0 && !Listed(names, tn)) continue;
                missing.RemoveAll(delegate(string x) { return string.Equals(x, tn, StringComparison.OrdinalIgnoreCase); });

                if (t.Protected)
                {
                    errors.Add(new JObj().Set("name", tn).Set("error", "know-how protected: cannot be exported"));
                    continue;
                }

                string file = Path.Combine(outDir, Sanitize(tn) + ".xml");
                try
                {
                    if (File.Exists(file)) File.Delete(file);
                    t.Type.Export(new FileInfo(file), ExportOptions.WithDefaults);
                    files.Add(new JObj().Set("name", tn).Set("group", t.Path.Length == 0 ? "/" : t.Path).Set("file", file));
                }
                catch (Exception ex) { errors.Add(new JObj().Set("name", tn).Set("error", Flat(ex))); }
            }

            return new JObj()
                .Set("exported", files.Count)
                .Set("failed", errors.Count)
                .Set("not_found", missing)
                .Set("out_dir", outDir)
                .Set("files", files)
                .Set("errors", errors);
        }

        public JObj ImportTypes(List<string> files, bool overwrite, List<string> keepSafe, string group)
        {
            return ImportXml("plc_import_types", "PLC data type", "SW.Types.", files, overwrite, keepSafe, group,
                delegate(string name)
                {
                    TypeAt t = FindType(name);
                    if (t == null) return null;
                    PlcTypeComposition types = t.Group.Types;
                    return new Existing
                    {
                        Path = t.Path,
                        Protected = t.Protected,
                        Into = delegate(FileInfo f, ImportOptions o) { types.Import(f, o); }
                    };
                },
                delegate
                {
                    PlcTypeComposition types = TypeGroupAt(group).Types;
                    return delegate(FileInfo f, ImportOptions o) { types.Import(f, o); };
                },
                delegate(JObj o)
                {
                    TypeAt t = FindType(o.Str("name"));
                    if (t != null) o.Set("consistent", TiaSession.Attr(t.Type, "IsConsistent"));
                });
        }

        static string Flat(Exception ex)
        {
            var sb = new System.Text.StringBuilder();
            while (ex != null)
            {
                sb.Append(ex.Message.Replace("\r\n", " | "));
                ex = ex.InnerException;
                if (ex != null) sb.Append("  <<  ");
            }
            string s = sb.ToString();
            return s.Length > 600 ? s.Substring(0, 600) + " [...]" : s;
        }
    }
}
