using System;
using System.IO;
using System.Text;
using System.Reflection;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Siemens.Engineering;
using Siemens.Engineering.Hmi;
using Siemens.Engineering.Hmi.Screen;
using Siemens.Engineering.HW;
using Siemens.Engineering.HW.Features;

namespace TiaBridge
{
    // Solo lettura ed export. Non importa nulla, non salva, non puo far cadere la sessione.
    class SchemaDump
    {
        static readonly string ROOT = Environment.GetEnvironmentVariable("TIA_TOOLS_OUT") ?? Environment.CurrentDirectory;
        const string TARGET_SCREEN = "ZZ_SCHEMA";
        static string OUT = Path.Combine(ROOT, "ProbeSchema");
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
            finally { File.WriteAllText(Path.Combine(ROOT, "tia_schema_report.txt"), LOG.ToString(), Encoding.UTF8); }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static void Execute()
        {
            W("=== DUMP SCHEMA OGGETTI (sola lettura) ===  " + DateTime.Now);
            if (!Directory.Exists(OUT)) Directory.CreateDirectory(OUT);

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

                DumpStyles(proj, target);

                ExportTagTables(target);

                var found = new List<Screen>();
                Collect(target.ScreenFolder, "", found);

                W("");
                W("Schermate presenti nel progetto (nome e numero):");
                foreach (var s in found)
                {
                    string num = "?";
                    var eo = s as IEngineeringObject;
                    if (eo != null)
                    {
                        try { var v = eo.GetAttribute("Number"); if (v != null) num = v.ToString(); } catch { }
                    }
                    W("   " + num.PadLeft(6) + "  " + s.Name);
                }

                W("");
                bool got = false;
                foreach (var s in found)
                {
                    if (s.Name != TARGET_SCREEN) continue;
                    string p = Path.Combine(OUT, TARGET_SCREEN + ".xml");
                    if (File.Exists(p)) File.Delete(p);
                    s.Export(new FileInfo(p), ExportOptions.WithDefaults);
                    W("ESPORTATA " + TARGET_SCREEN + " -> " + p);
                    got = true;
                }
                if (!got)
                    W("La schermata " + TARGET_SCREEN + " non esiste: creala in TIA e rilancia.");
            }
            W("");
            W("=== FINE === " + DateTime.Now);
        }

        // dove vivono gli stili: libreria di progetto, attributi del pannello, modelli
        static void DumpStyles(Project proj, HmiTarget target)
        {
            W("");
            W("======== LIBRERIA DI PROGETTO ========");
            DumpFolder(GetProp(proj, "ProjectLibrary"), "  ");

            W("");
            W("======== ATTRIBUTI DEL PANNELLO CON Style/Design ========");
            var eo = target as IEngineeringObject;
            if (eo == null) W("  (pannello non interrogabile)");
            else
            {
                bool any = false;
                foreach (var ai in eo.GetAttributeInfos())
                {
                    string n = ai.Name;
                    if (n.IndexOf("Style", StringComparison.OrdinalIgnoreCase) < 0 &&
                        n.IndexOf("Design", StringComparison.OrdinalIgnoreCase) < 0 &&
                        n.IndexOf("Theme", StringComparison.OrdinalIgnoreCase) < 0) continue;
                    any = true;
                    string v;
                    try { var o = eo.GetAttribute(n); v = o == null ? "<null>" : o.ToString(); }
                    catch (Exception ex) { v = "<err " + ex.Message + ">"; }
                    W("  " + n + " = " + v);
                }
                if (!any) W("  nessun attributo Style/Design sul pannello");
            }

            W("");
            W("======== MODELLI DI SCHERMATA ========");
            int nt = 0;
            foreach (var t in Items(GetProp(target.ScreenTemplateFolder, "ScreenTemplates"))) { nt++; W("  " + GetProp(t, "Name")); }
            if (nt == 0) W("  nessun modello definito");
        }

        static void DumpFolder(object folder, string ind)
        {
            if (folder == null) { W(ind + "(assente)"); return; }
            foreach (string comp in new string[] { "Types", "TypeFolder", "Folders", "MasterCopies", "MasterCopyFolder" })
            {
                object c = GetProp(folder, comp);
                if (c == null) continue;
                int n = 0;
                foreach (var it in Items(c))
                {
                    n++;
                    W(ind + comp + ": " + GetProp(it, "Name") + "   [" + it.GetType().Name + "]");
                    if (comp.EndsWith("Folder") || comp == "Folders") DumpFolder(it, ind + "   ");
                }
                if (n == 0 && (comp == "Types" || comp == "MasterCopies")) W(ind + comp + ": vuoto");
            }
        }

        // Openness espone i contenuti via GetComposition/GetAttribute, non come proprieta .NET:
        // provo prima la via Openness e solo dopo la riflessione.
        static object GetProp(object o, string name)
        {
            if (o == null) return null;
            var eo = o as IEngineeringObject;
            if (eo != null)
            {
                try { var c = eo.GetComposition(name); if (c != null) return c; } catch { }
                try { var a = eo.GetAttribute(name); if (a != null) return a; } catch { }
            }
            var pi = o.GetType().GetProperty(name);
            if (pi == null) return null;
            try { return pi.GetValue(o, null); } catch { return null; }
        }

        static System.Collections.IEnumerable Items(object o)
        {
            var e = o as System.Collections.IEnumerable;
            return e == null ? new object[0] : e;
        }

        // Le tabelle variabili si esportano (TagTable.Export): serve per vedere se
        // e come vengono scritti gli eventi sui tag, tipo "cambio valore".
        static void ExportTagTables(HmiTarget target)
        {
            W("");
            W("======== EXPORT TABELLE VARIABILI (solo SISTEMA) ========");
            string dir = Path.Combine(ROOT, "ProbeTags");
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            int n = 0;
            n += ExportFrom(target.TagFolder, dir);
            if (n == 0) W("  nessuna tabella SISTEMA trovata");
        }

        static int ExportFrom(object folder, string dir)
        {
            int n = 0;
            var eo = folder as IEngineeringObject;
            if (eo == null) return 0;
            System.Collections.IEnumerable tabs = null, subs = null;
            try { tabs = eo.GetComposition("TagTables") as System.Collections.IEnumerable; } catch { }
            try { subs = eo.GetComposition("Folders") as System.Collections.IEnumerable; } catch { }
            if (tabs != null)
                foreach (var t in tabs)
                {
                    var teo = t as IEngineeringObject;
                    if (teo == null) continue;
                    string nm = "";
                    try { nm = teo.GetAttribute("Name").ToString(); } catch { }
                    if (nm.ToUpper().IndexOf("SISTEMA") < 0) continue;
                    string p = Path.Combine(dir, nm + ".xml");
                    try
                    {
                        if (File.Exists(p)) File.Delete(p);
                        var mi = t.GetType().GetMethod("Export", new Type[] { typeof(FileInfo), typeof(ExportOptions) });
                        if (mi == null) { W("  " + nm + ": Export non disponibile"); continue; }
                        mi.Invoke(t, new object[] { new FileInfo(p), ExportOptions.WithDefaults });
                        W("  esportata " + nm);
                        n++;
                    }
                    catch (Exception ex) { W("  " + nm + ": " + ex.Message); }
                }
            if (subs != null) foreach (var s in subs) n += ExportFrom(s, dir);
            return n;
        }

        static void Collect(ScreenFolder f, string path, List<Screen> acc)
        {
            foreach (Screen s in f.Screens) acc.Add(s);
            foreach (ScreenFolder sub in f.Folders) Collect(sub, path + "/" + sub.Name, acc);
        }
    }
}
