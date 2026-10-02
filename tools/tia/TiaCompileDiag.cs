using System;
using System.IO;
using System.Text;
using System.Reflection;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Siemens.Engineering;
using Siemens.Engineering.Hmi;
using Siemens.Engineering.HW;
using Siemens.Engineering.HW.Features;
using Siemens.Engineering.Compiler;

namespace TiaBridge
{
    // Compila e stampa OGNI proprieta leggibile dei messaggi: i messaggi di TIA
    // hanno spesso Description vuota e il testo utile altrove.
    class CompileDiag
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
            catch (Exception ex) { W("FATAL: " + ex.GetType().Name + ": " + ex.Message); }
            finally { File.WriteAllText(Path.Combine(ROOT, "tia_compile_diag.txt"), LOG.ToString(), Encoding.UTF8); }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static void Execute()
        {
            W("=== DIAGNOSI COMPILAZIONE ===  " + DateTime.Now);
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

                var comp = target.GetService<ICompilable>();
                if (comp == null) { W("ICompilable non disponibile."); return; }

                W("Compilazione in corso...");
                var res = comp.Compile();
                W("Stato: " + res.State + "   errori: " + res.ErrorCount + "   avvisi: " + res.WarningCount);
                W("");
                W("--- messaggi completi (solo rami con errori) ---");
                Dump(res.Messages, "", 0);
                // niente Save: e una diagnosi, non deve lasciare traccia
            }
            W("");
            W("=== FINE === " + DateTime.Now);
        }

        static void Dump(object comp, string ind, int depth)
        {
            if (comp == null || depth > 6) return;
            var en = comp as IEnumerable;
            if (en == null) return;
            foreach (var m in en)
            {
                string state = Str(m, "State");
                // stampo tutte le proprieta scalari, cosi il testo vero salta fuori ovunque sia
                var bits = new List<string>();
                foreach (var pi in m.GetType().GetProperties())
                {
                    if (pi.Name == "Messages") continue;
                    object v;
                    try { v = pi.GetValue(m, null); } catch { continue; }
                    if (v == null) continue;
                    if (v is IEnumerable && !(v is string)) continue;
                    string s = v.ToString();
                    if (s.Length == 0) continue;
                    bits.Add(pi.Name + "=" + s);
                }
                W(ind + string.Join("  |  ", bits.ToArray()));
                var mp = m.GetType().GetProperty("Messages");
                if (mp != null)
                {
                    object child = null;
                    try { child = mp.GetValue(m, null); } catch { }
                    Dump(child, ind + "    ", depth + 1);
                }
            }
        }

        static string Str(object o, string p)
        {
            var pi = o.GetType().GetProperty(p);
            if (pi == null) return "";
            try { var v = pi.GetValue(o, null); return v == null ? "" : v.ToString(); }
            catch { return ""; }
        }
    }
}
