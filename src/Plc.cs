// Plc.cs - lato PLC, in sola lettura.
//
// Il server nasce per il lavoro HMI, e il PLC e il suo contesto: per capire a
// cosa punta un tag di pannello serve vedere i blocchi e le tabelle di variabili
// che ci stanno dietro. Qui si legge e si esporta, non si scrive: un blocco
// sovrascritto per errore e un impianto fermo, e nessun risparmio di tempo lo
// giustifica. La scrittura PLC resta un lavoro da fare in TIA, con gli occhi
// sopra.

using System;
using System.Collections.Generic;
using System.IO;
using Siemens.Engineering;
using Siemens.Engineering.Compare;
using Siemens.Engineering.HW;
using Siemens.Engineering.HW.Features;
using Siemens.Engineering.Online;
using Siemens.Engineering.SW;
using Siemens.Engineering.SW.Blocks;
using Siemens.Engineering.SW.Tags;

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
        }

        void Collect(PlcBlockGroup g, string path, List<BlockAt> into)
        {
            foreach (PlcBlock b in g.Blocks) into.Add(new BlockAt { Block = b, Path = path });
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
