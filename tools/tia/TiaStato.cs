// TiaStato - fotografia in sola lettura della configurazione del progetto HMI.
//
// Risponde alle domande che le schermate non possono rispondere: a quale PLC e
// a quale indirizzo punta il pannello, che dispositivo e, da quale schermata
// parte. Non scrive nulla: nessun Save, nessuna modifica di attributi.
//
// Si attacca all'istanza di TIA Portal gia aperta: UNA sola conferma manuale.

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
    class TiaStato
    {
        static readonly string ROOT = Environment.GetEnvironmentVariable("TIA_TOOLS_OUT") ?? Environment.CurrentDirectory;
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
            finally { File.WriteAllText(Path.Combine(ROOT, "tia_stato.txt"), LOG.ToString(), Encoding.UTF8); }
        }

        static void Execute()
        {
            W("=== STATO DEL PROGETTO HMI ===  " + DateTime.Now.ToString("dd/MM/yyyy HH:mm:ss"));
            W("sola lettura: nessuna modifica, nessun salvataggio");
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
                W("Progetto: " + proj.Name);
                W("");

                // ---------------------------------------------------- dispositivi
                W("--- dispositivi nel progetto ---");
                HmiTarget hmi = null;
                foreach (Device dev in proj.Devices)
                {
                    W("  " + dev.Name + "   [" + Attr(dev, "TypeIdentifier") + "]");
                    foreach (DeviceItem it in dev.DeviceItems) Walk(it, "    ", ref hmi);
                }
                W("");

                if (hmi == null) { W("ERRORE: pannello HMI non trovato."); return; }

                // ------------------------------------------------ pannello e RT
                W("--- pannello ---");
                W("  nome        : " + hmi.Name);
                foreach (string a in new string[] { "Type", "ScreenWidth", "ScreenHeight",
                                                    "StartScreen", "RuntimeVersion", "Version" })
                {
                    string v = Attr(hmi, a);
                    if (v != null) W("  " + a.PadRight(12) + ": " + v);
                }
                Dump(hmi, "  attributi del pannello");
                W("");

                // ---------------------------------------------------- connessioni
                W("--- connessioni verso il PLC ---");
                int nc = 0;
                foreach (Connection c in hmi.Connections)
                {
                    nc++;
                    W("  connessione: " + c.Name);
                    Dump(c, "    attributi");
                }
                if (nc == 0) W("  NESSUNA CONNESSIONE DEFINITA");
                W("");

                // -------------------------------------------- indirizzi di rete
                W("--- indirizzi di rete dei dispositivi ---");
                foreach (Device dev in proj.Devices)
                    foreach (DeviceItem it in dev.DeviceItems) Net(it, dev.Name);
                W("");

                W("--- conteggi ---");
                var scr = new List<Screen>();
                Collect(hmi.ScreenFolder, scr);
                W("  schermate: " + scr.Count);
                int nt = 0;
                foreach (var tt in hmi.TagFolder.TagTables) nt += tt.Tags.Count;
                foreach (var f in hmi.TagFolder.Folders) foreach (var tt in f.TagTables) nt += tt.Tags.Count;
                W("  tag (tabelle di primo e secondo livello): " + nt);
            }

            W("");
            W("=== FINE ===");
        }

        // Scende nell'albero dei DeviceItem cercando il pannello, e stampa cosa trova.
        static void Walk(DeviceItem it, string ind, ref HmiTarget hmi)
        {
            string ord = Attr(it, "TypeIdentifier");
            if (it.Name.Length > 0)
                W(ind + it.Name + (ord == null ? "" : "   [" + ord + "]"));
            var c = it.GetService<SoftwareContainer>();
            if (c != null)
            {
                W(ind + "  software: " + c.Software.GetType().Name);
                if (hmi == null && c.Software is HmiTarget) hmi = (HmiTarget)c.Software;
            }
            foreach (DeviceItem sub in it.DeviceItems) Walk(sub, ind + "  ", ref hmi);
        }

        // Indirizzi IP e di sottorete di ogni interfaccia.
        static void Net(DeviceItem it, string devName)
        {
            var ni = it.GetService<NetworkInterface>();
            if (ni != null)
            {
                foreach (Node n in ni.Nodes)
                {
                    string addr = Attr(n, "Address");
                    string sub = "";
                    try { sub = n.ConnectedSubnet == null ? "(non collegata a sottorete)" : n.ConnectedSubnet.Name; }
                    catch { sub = "?"; }
                    W("  " + devName + " / " + it.Name + " : " + (addr == null ? "?" : addr) + "   sottorete: " + sub);
                }
            }
            foreach (DeviceItem sub in it.DeviceItems) Net(sub, devName);
        }

        // Stampa tutti gli attributi leggibili di un oggetto, senza indovinarne i nomi.
        static void Dump(IEngineeringObject o, string titolo)
        {
            List<EngineeringAttributeInfo> infos;
            try { infos = new List<EngineeringAttributeInfo>(o.GetAttributeInfos()); }
            catch { return; }
            var righe = new List<string>();
            foreach (var i in infos)
            {
                string v = Attr(o, i.Name);
                if (v == null || v.Length == 0) continue;
                righe.Add("      " + i.Name.PadRight(28) + " = " + v);
            }
            if (righe.Count == 0) return;
            righe.Sort();
            W(titolo + ":");
            foreach (string r in righe) W(r);
        }

        static string Attr(IEngineeringObject o, string name)
        {
            try { object v = o.GetAttribute(name); return v == null ? null : v.ToString(); }
            catch { return null; }
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
