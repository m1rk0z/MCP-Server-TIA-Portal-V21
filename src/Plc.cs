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
