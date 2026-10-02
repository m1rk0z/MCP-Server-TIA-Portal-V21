using System;
using System.IO;
using System.Text;
using System.Reflection;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Siemens.Engineering;
using Siemens.Engineering.Hmi;
using Siemens.Engineering.Hmi.Screen;
using Siemens.Engineering.HW;
using Siemens.Engineering.HW.Features;

namespace TiaBridge
{
    class Probe
    {
        static readonly string ROOT = Environment.GetEnvironmentVariable("TIA_TOOLS_OUT") ?? Environment.CurrentDirectory;
        static string PROBE_DIR = Path.Combine(ROOT, "Probe");
        static string SCHEMA_DIR = Path.Combine(ROOT, "ProbeSchema");
        static StringBuilder LOG = new StringBuilder();

        static void W(string s) { Console.WriteLine(s); LOG.AppendLine(s); }

        // tipi candidati: nome elemento XML -> attributi minimi extra oltre a geometria
        static readonly string[][] CANDIDATES = new string[][]
        {
            new[] { "Hmi.Screen.Rectangle",       "" },
            new[] { "Hmi.Screen.Ellipse",         "" },
            new[] { "Hmi.Screen.Circle",          "<Radius>30</Radius>" },
            new[] { "Hmi.Screen.Line",            "" },
            new[] { "Hmi.Screen.PolyLine",        "" },
            new[] { "Hmi.Screen.Polygon",         "" },
            new[] { "Hmi.Screen.TextField",       "" },
            new[] { "Hmi.Screen.IOField",         "" },
            new[] { "Hmi.Screen.SymbolicIOField", "" },
            new[] { "Hmi.Screen.GraphicIOField",  "" },
            new[] { "Hmi.Screen.DateTimeField",   "" },
            new[] { "Hmi.Screen.Bar",             "" },
            new[] { "Hmi.Screen.Gauge",           "" },
            new[] { "Hmi.Screen.Slider",          "" },
            new[] { "Hmi.Screen.Switch",          "" },
            new[] { "Hmi.Screen.Button",          "" },
            new[] { "Hmi.Screen.GraphicView",     "" },
            new[] { "Hmi.Screen.AlarmView",       "" },
            new[] { "Hmi.Screen.TrendView",       "" },
            new[] { "Hmi.Screen.UserView",        "" },
            new[] { "Hmi.Screen.Clock",           "" },
            new[] { "Hmi.Screen.RecipeView",      "" },
            new[] { "Hmi.Screen.ScreenWindow",    "" },
            new[] { "Hmi.Screen.SymbolLibrary",   "" },
            new[] { "Hmi.Screen.TextBox",         "" },
            new[] { "Hmi.Screen.WindowsSlider",   "" },
        };

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
            catch (Exception ex) { W("FATAL: " + ex.GetType().Name + ": " + ex.Message); W(ex.StackTrace); }
            finally { File.WriteAllText(Path.Combine(ROOT, "tia_probe_report.txt"), LOG.ToString(), Encoding.UTF8); }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static void Execute()
        {
            W("=== SONDA TIPI OGGETTO SCHERMATA ===  " + DateTime.Now);
            if (!Directory.Exists(PROBE_DIR)) Directory.CreateDirectory(PROBE_DIR);
            if (!Directory.Exists(SCHEMA_DIR)) Directory.CreateDirectory(SCHEMA_DIR);

            // genera un XML per ogni tipo candidato
            for (int i = 0; i < CANDIDATES.Length; i++)
                File.WriteAllText(Path.Combine(PROBE_DIR, Short(CANDIDATES[i][0]) + ".xml"), OneItemScreen(CANDIDATES[i][0], CANDIDATES[i][1], 900 + i), Encoding.UTF8);
            W("XML di sonda generati: " + CANDIDATES.Length);

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
                HmiTarget target = null;
                foreach (Device dev in proj.Devices)
                {
                    foreach (DeviceItem it in dev.DeviceItems)
                    {
                        var c = it.GetService<SoftwareContainer>();
                        if (c != null && c.Software is HmiTarget) { target = (HmiTarget)c.Software; break; }
                    }
                    if (target != null) break;
                }
                if (target == null) { W("ERRORE: pannello HMI non trovato."); return; }
                W("Pannello: " + target.Name);

                // cartella di sonda dedicata, cancellata alla fine
                ScreenFolder probeFolder = null;
                foreach (ScreenFolder sf in target.ScreenFolder.Folders)
                    if (sf.Name == "ZZ_PROBE") { probeFolder = sf; break; }
                if (probeFolder == null) probeFolder = target.ScreenFolder.Folders.Create("ZZ_PROBE");

                var ok = new List<string>();
                W("");
                W("--- esito import per tipo ---");
                foreach (var c in CANDIDATES)
                {
                    string type = c[0], sh = Short(type);
                    string file = Path.Combine(PROBE_DIR, sh + ".xml");
                    try
                    {
                        probeFolder.Screens.Import(new FileInfo(file), ImportOptions.Override);
                        W(string.Format("  OK    {0,-32}", type));
                        ok.Add(sh);
                    }
                    catch (Exception ex)
                    {
                        W(string.Format("  NO    {0,-32} {1}", type, First(ex)));
                    }
                }

                // riesporta gli accettati: TIA riempie tutti gli attributi di default
                W("");
                W("--- export dello schema canonico ---");
                foreach (Screen s in probeFolder.Screens)
                {
                    string p = Path.Combine(SCHEMA_DIR, s.Name + ".xml");
                    try
                    {
                        if (File.Exists(p)) File.Delete(p);
                        s.Export(new FileInfo(p), ExportOptions.WithDefaults);
                        W("  esportato " + s.Name);
                    }
                    catch (Exception ex) { W("  export fallito " + s.Name + ": " + First(ex)); }
                }

                // pulizia: via le schermate di sonda e la cartella
                W("");
                W("--- pulizia ---");
                var del = new List<Screen>();
                foreach (Screen s in probeFolder.Screens) del.Add(s);
                foreach (var s in del) { try { s.Delete(); } catch { } }
                var mi = probeFolder.GetType().GetMethod("Delete", Type.EmptyTypes);
                if (mi != null)
                {
                    try { mi.Invoke(probeFolder, null); W("  cartella ZZ_PROBE rimossa"); }
                    catch (Exception ex) { W("  ZZ_PROBE non rimossa (svuotata): " + First(ex)); }
                }
                else W("  ZZ_PROBE resta vuota: l API non espone Delete sulle cartelle, cancellala a mano");

                proj.Save();
                W("Progetto salvato (senza le schermate di sonda).");
            }
            W("");
            W("=== FINE === " + DateTime.Now);
        }

        static string Short(string t) { return "P_" + t.Replace("Hmi.Screen.", ""); }

        static string First(Exception ex)
        {
            string m = ex.Message;
            while (ex.InnerException != null) { ex = ex.InnerException; m += "  <<  " + ex.Message; }
            m = m.Replace("\r", " ").Replace("\n", " ");
            return m.Length > 260 ? m.Substring(0, 260) + "..." : m;
        }

        static string OneItemScreen(string type, string extra, int number)
        {
            var sb = new StringBuilder();
            sb.AppendLine("<?xml version=\"1.0\" encoding=\"utf-8\"?>");
            sb.AppendLine("<Document>");
            sb.AppendLine("  <Engineering version=\"V21\" />");
            sb.AppendLine("  <DocumentInfo>");
            sb.AppendLine("    <Created>2026-08-25T14:00:00.0000000Z</Created>");
            sb.AppendLine("    <ExportSetting>WithDefaults</ExportSetting>");
            sb.AppendLine("    <InstalledProducts>");
            sb.AppendLine("      <Product><DisplayName>Totally Integrated Automation Portal</DisplayName><DisplayVersion>V21</DisplayVersion></Product>");
            sb.AppendLine("      <Product><DisplayName>WinCC Basic/Comfort/Advanced</DisplayName><DisplayVersion>V21</DisplayVersion></Product>");
            sb.AppendLine("    </InstalledProducts>");
            sb.AppendLine("  </DocumentInfo>");
            sb.AppendLine("  <Hmi.Screen.Screen ID=\"0\">");
            sb.AppendLine("    <AttributeList>");
            sb.AppendLine("      <ActiveLayer>0</ActiveLayer>");
            sb.AppendLine("      <BackColor>255, 255, 255</BackColor>");
            sb.AppendLine("      <Height>480</Height>");
            sb.AppendLine("      <Name>" + Short(type) + "</Name>");
            sb.AppendLine("      <Number>" + number + "</Number>");
            sb.AppendLine("      <Visible>true</Visible>");
            sb.AppendLine("      <Width>800</Width>");
            sb.AppendLine("    </AttributeList>");
            sb.AppendLine("    <ObjectList>");
            sb.AppendLine("      <Hmi.Screen.ScreenLayer ID=\"1\" CompositionName=\"Layers\">");
            sb.AppendLine("        <AttributeList><Index>0</Index><Name /><VisibleES>true</VisibleES></AttributeList>");
            sb.AppendLine("        <ObjectList>");
            sb.AppendLine("          <" + type + " ID=\"2\" CompositionName=\"ScreenItems\">");
            sb.AppendLine("            <AttributeList>");
            sb.AppendLine("              <Height>60</Height>");
            sb.AppendLine("              <Left>40</Left>");
            sb.AppendLine("              <ObjectName>Probe_1</ObjectName>");
            sb.AppendLine("              <Top>40</Top>");
            sb.AppendLine("              <Width>200</Width>");
            if (extra.Length > 0) sb.AppendLine("              " + extra);
            sb.AppendLine("            </AttributeList>");
            sb.AppendLine("          </" + type + ">");
            sb.AppendLine("        </ObjectList>");
            sb.AppendLine("      </Hmi.Screen.ScreenLayer>");
            sb.AppendLine("    </ObjectList>");
            sb.AppendLine("  </Hmi.Screen.Screen>");
            sb.AppendLine("</Document>");
            return sb.ToString();
        }
    }
}
