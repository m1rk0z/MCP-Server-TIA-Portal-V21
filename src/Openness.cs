// Openness.cs - trova e carica le assembly di TIA Portal Openness.
//
// Dalla V21 Siemens ha spezzato Siemens.Engineering.dll in piu assembly
// (Base, Step7, WinCC, WinCCUnified, ...), ha cambiato il public key token
// (d29ec89bac048f84 -> 29bfe5fdf4ba5d3b) e le ha spostate in
//   C:\Program Files\Siemens\Automation\Portal V21\PublicAPI\V21\net48
// Un programma compilato per una versione precedente non parte piu: il
// riferimento va rifatto. Qui si risolve a runtime, cosi lo stesso eseguibile
// trova le assembly ovunque siano installate.
//
// L'ordine di ricerca e: variabile d'ambiente, poi le cartelle PublicAPI di ogni
// Portal installato (dalla piu recente), poi il Bin del Portal.

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

namespace TiaMcp
{
    public static class Openness
    {
        static readonly Dictionary<string, Assembly> Cache =
            new Dictionary<string, Assembly>(StringComparer.OrdinalIgnoreCase);

        static readonly List<string> Probed = new List<string>();

        /// <summary>Cartella da cui sono state caricate le assembly, per la diagnostica.</summary>
        public static string ResolvedFrom { get; private set; }

        /// <summary>Versione di Portal dedotta dal percorso, es. "V21".</summary>
        public static string PortalVersion { get; private set; }

        /// <summary>
        /// Da installare PRIMA di toccare qualunque tipo di Siemens.Engineering:
        /// il JIT risolve l'assembly al primo metodo che la nomina, non alla prima riga.
        /// </summary>
        public static void Install()
        {
            foreach (string dir in CandidateDirectories()) Probed.Add(dir);

            AppDomain.CurrentDomain.AssemblyResolve += delegate(object sender, ResolveEventArgs e)
            {
                string name = new AssemblyName(e.Name).Name;

                Assembly hit;
                if (Cache.TryGetValue(name, out hit)) return hit;

                foreach (string dir in Probed)
                {
                    string file = Path.Combine(dir, name + ".dll");
                    if (!File.Exists(file)) continue;
                    Assembly asm = Assembly.LoadFrom(file);
                    Cache[name] = asm;
                    if (ResolvedFrom == null)
                    {
                        ResolvedFrom = dir;
                        PortalVersion = GuessVersion(dir);
                    }
                    return asm;
                }

                // Il null va messo in cache: senza, un tipo mancante fa ripartire la
                // ricerca a ogni riferimento e il server sembra bloccato.
                Cache[name] = null;
                return null;
            };
        }

        public static List<string> SearchPath { get { return new List<string>(Probed); } }

        static IEnumerable<string> CandidateDirectories()
        {
            var seen = new List<string>();

            // 1. imposizione esplicita: serve quando convivono piu installazioni
            string forced = Environment.GetEnvironmentVariable("TIA_OPENNESS_PATH");
            if (!string.IsNullOrEmpty(forced))
                foreach (string part in forced.Split(';'))
                    Add(seen, part.Trim());

            // 2. le PublicAPI di ogni Portal installato, dalla piu recente
            foreach (string root in PortalRoots())
            {
                string publicApi = Path.Combine(root, "PublicAPI");
                if (Directory.Exists(publicApi))
                {
                    var versions = new List<string>(Directory.GetDirectories(publicApi));
                    versions.Sort(CompareDescending);
                    foreach (string v in versions)
                    {
                        // dalla V19 c'e un livello in piu per il target framework
                        Add(seen, Path.Combine(v, "net48"));
                        Add(seen, v);
                    }
                }
                Add(seen, Path.Combine(root, "Bin"));
            }

            return seen;
        }

        static IEnumerable<string> PortalRoots()
        {
            var roots = new List<string>();
            foreach (string program in new[] { @"C:\Program Files\Siemens\Automation",
                                               @"C:\Program Files (x86)\Siemens\Automation" })
            {
                if (!Directory.Exists(program)) continue;
                var dirs = new List<string>();
                foreach (string d in Directory.GetDirectories(program))
                    if (Path.GetFileName(d).StartsWith("Portal", StringComparison.OrdinalIgnoreCase))
                        dirs.Add(d);
                dirs.Sort(CompareDescending);
                roots.AddRange(dirs);
            }
            return roots;
        }

        // "Portal V21" prima di "Portal V19": si ordina al contrario sul nome, che per
        // le versioni a due cifre e sufficiente e non richiede di interpretare nulla.
        static int CompareDescending(string a, string b)
        {
            return string.Compare(Path.GetFileName(b), Path.GetFileName(a),
                                  StringComparison.OrdinalIgnoreCase);
        }

        static void Add(List<string> into, string dir)
        {
            if (string.IsNullOrEmpty(dir)) return;
            if (!Directory.Exists(dir)) return;
            foreach (string existing in into)
                if (string.Equals(existing, dir, StringComparison.OrdinalIgnoreCase)) return;
            into.Add(dir);
        }

        static string GuessVersion(string dir)
        {
            var d = new DirectoryInfo(dir);
            while (d != null)
            {
                string n = d.Name;
                if (n.Length >= 2 && (n[0] == 'V' || n[0] == 'v') && char.IsDigit(n[1])) return n.ToUpperInvariant();
                if (n.StartsWith("Portal ", StringComparison.OrdinalIgnoreCase)) return n.Substring(7).Trim();
                d = d.Parent;
            }
            return "?";
        }
    }
}
