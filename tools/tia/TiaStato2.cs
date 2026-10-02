// TiaStato2 - secondo passaggio, in sola lettura.
//
// Il primo passaggio ha mostrato che la connessione "PLC" espone il solo
// attributo Name. Qui si scava: si stampano TUTTI gli attributi, anche quelli
// vuoti (un attributo presente e vuoto e' esso stesso il risultato), si esporta
// la connessione in XML - l'unico posto dove Openness mette davvero l'indirizzo
// del partner - e si elencano le schermate per capire quale sia la 163esima.
//
// Non scrive nel progetto: esporta su file e basta. Nessun Save.

using System;
using System.IO;
using System.Text;
using System.Reflection;
using System.Collections.Generic;
using Siemens.Engineering;
using Siemens.Engineering.Hmi;
using Siemens.Engineering.Hmi.Screen;
using Siemens.Engineering.Hmi.Communication;
using Siemens.Engineering.HW;
using Siemens.Engineering.HW.Features;

namespace TiaBridge
{
    class TiaStato2
    {
        static readonly string ROOT = Environment.GetEnvironmentVariable("TIA_TOOLS_OUT") ?? Environment.CurrentDirectory;
        static string OUT = Path.Combine(ROOT, "tia_stato2");
        static StringBuilder LOG = new StringBuilder();
        static void W(string s) { Console.WriteLine(s); LOG.AppendLine(s); }

        [STAThread]
        static void Main(string[] args)
        {
            AppDomain.CurrentDomain.AssemblyResolve += delegate(object s, ResolveEventArgs e)
            {
                string n = new AssemblyName(e.Name).Name;
                string p1 = Path.Combine(@"C:\Program Files\Siemens\Automation\Portal V21\PublicAPI\V21\net48", n + ".dll");
                if (File.Exists(p1)) return Assembly.LoadFrom(p1);
                string p2 = Path.Combine(@"C:\Program Files\Siemens\Automation\Portal V21\Bin", n + ".dll");
                if (File.Exists(p2)) return Assembly.LoadFrom(p2);
                return null;
            };
            try { Execute(); }
            catch (Exception ex) { W("FATAL: " + ex.GetType().Name + ": " + Flat(ex)); }
            finally { File.WriteAllText(Path.Combine(ROOT, "tia_stato2.txt"), LOG.ToString(), Encoding.UTF8); }
        }

        static void Execute()
        {
            Directory.CreateDirectory(OUT);
            W("=== STATO DEL PROGETTO HMI, secondo passaggio ===  " + DateTime.Now.ToString("dd/MM/yyyy HH:mm:ss"));
            W("sola lettura: si esporta su file, non si modifica e non si salva");
            W("");

            TiaPortalProcess chosen = null;
            foreach (var p in TiaPortal.GetProcesses())
            {
                string path;
                try { path = p.ProjectPath == null ? "" : p.ProjectPath.FullName; } catch { path = ""; }
                if (chosen == null && path.Length > 0) { chosen = p; W("Istanza: PID " + p.Id + "  " + path); }
            }
            if (chosen == null) { W("ERRORE: nessuna istanza TIA con progetto aperto."); return; }

            using (var tia = chosen.Attach())
            {
                var proj = tia.Projects[0];

                HmiTarget hmi = null;
                var items = new List<DeviceItem>();
                foreach (Device dev in proj.Devices)
                    foreach (DeviceItem it in dev.DeviceItems) Walk(it, items, ref hmi);
                if (hmi == null) { W("ERRORE: pannello HMI non trovato."); return; }

                // ------------------------------------------------- connessioni
                W("--- connessioni: tutti gli attributi, vuoti compresi ---");
                foreach (Connection c in hmi.Connections)
                {
                    W("  connessione \"" + c.Name + "\"");
                    DumpAll(c, "    ");
                    // L'export e' l'unico posto dove Openness scrive l'indirizzo
                    // del partner di una connessione HMI.
                    string f = Path.Combine(OUT, "connessione_" + Safe(c.Name) + ".xml");
                    if (TryExport(c, f)) W("    esportata in " + f);
                    else W("    ATTENZIONE: export della connessione non riuscito");
                }
                W("");

                // -------------------------------------- dispositivo e interfaccia
                W("--- attributi dei DeviceItem del pannello ---");
                foreach (DeviceItem it in items)
                {
                    W("  " + it.Name);
                    DumpAll(it, "    ");
                    var ni = it.GetService<NetworkInterface>();
                    if (ni == null) continue;
                    W("    interfaccia di rete:");
                    DumpAll(ni, "      ");
                    foreach (Node n in ni.Nodes)
                    {
                        W("      nodo " + n.Name);
                        DumpAll(n, "        ");
                    }
                }
                W("");

                // ---------------------------------------------- sottoreti del progetto
                W("--- sottoreti definite nel progetto ---");
                int ns = 0;
                foreach (Subnet s in proj.Subnets) { ns++; W("  " + s.Name + "  [" + Attr(s, "TypeIdentifier") + "]"); }
                if (ns == 0) W("  NESSUNA SOTTORETE DEFINITA");
                W("");

                // --------------------------------------------------- schermate
                var scr = new List<Screen>();
                Collect(hmi.ScreenFolder, scr);
                var nomi = new List<string>();
                foreach (var s in scr) nomi.Add(s.Name);
                nomi.Sort();
                W("--- schermate (" + nomi.Count + ") ---");
                var fuori = new List<string>();
                foreach (string n in nomi)
                    if (!File.Exists(Path.Combine(ROOT, "ScreensV2", n + ".xml"))) fuori.Add(n);
                W("  generate dal toolchain: " + (nomi.Count - fuori.Count));
                W("  NON generate dal toolchain: " + fuori.Count);
                foreach (string n in fuori) W("      " + n);
            }

            W("");
            W("=== FINE ===");
        }

        static void Walk(DeviceItem it, List<DeviceItem> into, ref HmiTarget hmi)
        {
            into.Add(it);
            var c = it.GetService<SoftwareContainer>();
            if (c != null && hmi == null && c.Software is HmiTarget) hmi = (HmiTarget)c.Software;
            foreach (DeviceItem sub in it.DeviceItems) Walk(sub, into, ref hmi);
        }

        // Export non e' sulla stessa interfaccia per tutti i tipi: si cerca per
        // riflessione invece di legarsi a una firma che potrebbe non esserci.
        static bool TryExport(object o, string file)
        {
            foreach (MethodInfo m in o.GetType().GetMethods())
            {
                if (m.Name != "Export") continue;
                var pars = m.GetParameters();
                if (pars.Length != 2) continue;
                if (pars[0].ParameterType != typeof(FileInfo)) continue;
                try { m.Invoke(o, new object[] { new FileInfo(file), ExportOptions.WithDefaults }); return File.Exists(file); }
                catch (Exception ex) { W("      export fallito: " + Flat(ex)); return false; }
            }
            return false;
        }

        // Stampa ogni attributo dichiarato, anche se vuoto o nullo: un attributo
        // presente e vuoto e' un risultato, non un motivo per nascondere la riga.
        static void DumpAll(IEngineeringObject o, string ind)
        {
            List<EngineeringAttributeInfo> infos;
            try { infos = new List<EngineeringAttributeInfo>(o.GetAttributeInfos()); }
            catch (Exception ex) { W(ind + "(attributi non leggibili: " + ex.GetType().Name + ")"); return; }
            if (infos.Count == 0) { W(ind + "(nessun attributo dichiarato)"); return; }
            var righe = new List<string>();
            foreach (var i in infos)
            {
                object v; string txt;
                try { v = o.GetAttribute(i.Name); txt = v == null ? "(null)" : v.ToString(); }
                catch (Exception ex) { txt = "(non leggibile: " + ex.GetType().Name + ")"; }
                if (txt.Length == 0) txt = "(vuoto)";
                righe.Add(ind + i.Name.PadRight(30) + " = " + txt);
            }
            righe.Sort();
            foreach (string r in righe) W(r);
        }

        static string Attr(IEngineeringObject o, string name)
        {
            try { object v = o.GetAttribute(name); return v == null ? "" : v.ToString(); }
            catch { return ""; }
        }

        static string Safe(string s)
        {
            foreach (char c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
            return s;
        }

        static void Collect(ScreenFolder f, List<Screen> into)
        {
            foreach (Screen s in f.Screens) into.Add(s);
            foreach (ScreenUserFolder sub in f.Folders) Collect(sub, into);
        }

        static void Collect(ScreenUserFolder f, List<Screen> into)
        {
            foreach (Screen s in f.Screens) into.Add(s);
            foreach (ScreenUserFolder sub in f.Folders) Collect(sub, into);
        }

        static string Flat(Exception ex)
        {
            var sb = new StringBuilder();
            while (ex != null) { sb.Append(ex.Message.Replace("\r", " ").Replace("\n", " ")); ex = ex.InnerException; if (ex != null) sb.Append(" <- "); }
            return sb.ToString();
        }
    }
}
