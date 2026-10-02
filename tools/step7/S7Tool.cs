// S7Tool - lettura del progetto STEP7 V5 attraverso l'interfaccia di automazione.
//
// PERCHE' ESISTE
// L'interfaccia (Simatic.Simatic.1, S7BIN\S7ABATCX.DLL) e un server COM
// in-process a 32 bit. Un processo a 64 bit non puo caricarla: non e un
// problema di registrazione, e l'architettura. Questo programma si compila
// con /platform:x86 e fa da ponte, cosi il chiamante puo essere qualunque
// cosa - PowerShell a 64 bit, bash, un altro programma.
//
//   csc /platform:x86 /r:Microsoft.CSharp.dll /out:S7Tool.exe S7Tool.cs
//
// SOLA LETTURA
// Usa solo Export e le proprieta di lettura. Non apre editor, non genera
// sorgenti, non compila, non scarica, non salva. Non c'e nessun percorso di
// codice che scriva nel progetto.

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;

class S7Tool
{
    static void Uso()
    {
        Console.WriteLine();
        Console.WriteLine("S7Tool - lettura del progetto STEP7 V5 (sola lettura)");
        Console.WriteLine();
        Console.WriteLine("  S7Tool progetti");
        Console.WriteLine("        elenca i progetti registrati in SIMATIC Manager");
        Console.WriteLine();
        Console.WriteLine("  S7Tool programmi <progetto>");
        Console.WriteLine("        elenca i programmi del progetto e dice qual e quello con i blocchi");
        Console.WriteLine();
        Console.WriteLine("  S7Tool blocchi <progetto> <file.tsv>");
        Console.WriteLine("        nome, linguaggio, protezione, dimensione, data, simbolo, intestazione");
        Console.WriteLine();
        Console.WriteLine("  S7Tool simboli <progetto> <file.sdf>");
        Console.WriteLine("        tabella dei simboli (l'estensione decide il formato: .sdf .asc .seq)");
        Console.WriteLine();
        Console.WriteLine("  S7Tool struttura <progetto> <file.txt>");
        Console.WriteLine("        albero delle chiamate. I blocchi a rientro minimo sono le radici,");
        Console.WriteLine("        cioe quelli che nessuno chiama");
        Console.WriteLine();
        Console.WriteLine("  S7Tool sorgenti <progetto> <cartella>");
        Console.WriteLine("        esporta tutti i sorgenti SCL/AWL del progetto");
        Console.WriteLine();
        Console.WriteLine("  S7Tool date <progetto>");
        Console.WriteLine("        confronta la data di ogni sorgente con quella del blocco compilato");
        Console.WriteLine();
        Console.WriteLine("Il nome del progetto e quello che si vede in SIMATIC Manager,");
        Console.WriteLine("per esempio PLC_LINEA_1. Basta un pezzo del nome.");
        Console.WriteLine();
    }

    static int Main(string[] args)
    {
        if (IntPtr.Size != 4)
        {
            Console.Error.WriteLine("ERRORE: questo programma va compilato con /platform:x86.");
            Console.Error.WriteLine("L'interfaccia di STEP7 e un server COM in-process a 32 bit.");
            return 2;
        }
        if (args.Length == 0) { Uso(); return 1; }

        try { return Esegui(args); }
        catch (Exception e)
        {
            Console.Error.WriteLine("ERRORE: " + e.Message.Split('\r')[0]);
            return 3;
        }
    }

    static dynamic simatic;

    static int Esegui(string[] args)
    {
        Type t = Type.GetTypeFromProgID("Simatic.Simatic.1");
        if (t == null)
        {
            Console.Error.WriteLine("Simatic.Simatic.1 non e registrato. STEP7 V5 e installato?");
            return 3;
        }
        simatic = Activator.CreateInstance(t);
        // Niente finestre di dialogo: se STEP7 volesse chiedere qualcosa,
        // fallisce invece di restare appeso in attesa di un click.
        simatic.UnattendedServerMode = true;

        string cmd = args[0].ToLowerInvariant();

        if (cmd == "progetti")
        {
            foreach (dynamic p in Enum(simatic.Projects))
                Console.WriteLine("{0,-28} {1}", p.Name, p.LogPath);
            return 0;
        }

        if (args.Length < 2) { Uso(); return 1; }
        dynamic prj = TrovaProgetto(args[1]);
        if (prj == null) return 3;

        if (cmd == "programmi")
        {
            int i = 0;
            foreach (dynamic pg in Enum(prj.Programs))
            {
                i++;
                string cont = "";
                try { foreach (dynamic c in Enum(pg.Next)) cont += (cont.Length > 0 ? ", " : "") + c.Name; }
                catch { }
                Console.WriteLine("{0,3}  {1,-20} {2}", i, pg.Name, cont.Length > 0 ? "[" + cont + "]" : "");
            }
            return 0;
        }

        dynamic prog = TrovaProgramma(prj);
        if (prog == null)
        {
            Console.Error.WriteLine("Nessun programma con contenitore di blocchi in questo progetto.");
            return 3;
        }

        if (cmd == "blocchi")
        {
            if (args.Length < 3) { Uso(); return 1; }
            var righe = new List<string>();
            righe.Add("nome\tlinguaggio\tprotetto\tbyte\tmodificato\tsimbolo\tintestazione");
            foreach (dynamic b in Enum(Contenitore(prog, "blocchi").Next)
                     )
                righe.Add(string.Join("\t", new string[] {
                    Str(() => b.Name), Str(() => b.Language),
                    Str(() => b.KnowHowProtection ? "si" : ""),
                    Str(() => b.Size.ToString()),
                    Str(() => ((DateTime)b.Modified).ToString("yyyy-MM-dd HH:mm:ss")),
                    Str(() => b.SymbolicName), Str(() => b.HeaderName) }));
            File.WriteAllLines(args[2], righe.ToArray(), new System.Text.UTF8Encoding(false));
            Console.WriteLine("{0} blocchi in {1}", righe.Count - 1, args[2]);
            return 0;
        }

        if (cmd == "simboli")
        {
            if (args.Length < 3) { Uso(); return 1; }
            prog.SymbolTable.Export(Path.GetFullPath(args[2]));
            Console.WriteLine("simboli in {0}", args[2]);
            return 0;
        }

        if (cmd == "struttura")
        {
            if (args.Length < 3) { Uso(); return 1; }
            prog.ExportProgramStructure(Path.GetFullPath(args[2]), true, 2);
            Console.WriteLine("struttura in {0}", args[2]);
            return 0;
        }

        if (cmd == "sorgenti")
        {
            if (args.Length < 3) { Uso(); return 1; }
            Directory.CreateDirectory(args[2]);
            int n = 0;
            foreach (dynamic s in Enum(Contenitore(prog, "sorgenti").Next))
            {
                string nome = s.Name;
                foreach (char c in Path.GetInvalidFileNameChars()) nome = nome.Replace(c, '_');
                // 1122310 = SCL, tutto il resto e sorgente in lista istruzioni
                string est = ((int)s.ConcreteType == 1122310) ? ".scl" : ".awl";
                s.Export(Path.GetFullPath(Path.Combine(args[2], nome + est)));
                Console.WriteLine("  " + nome + est);
                n++;
            }
            Console.WriteLine("{0} sorgenti in {1}", n, args[2]);
            return 0;
        }

        if (cmd == "date")
        {
            // A cosa serve: per un blocco SCL l'unico modo di cambiarlo e
            // ricompilare il sorgente. Se il blocco e piu recente del sorgente
            // e il sorgente non e stato toccato dopo, il blocco che gira e la
            // compilazione di quel testo. Se invece il sorgente e piu recente,
            // qualcuno lo ha modificato senza ricompilare: il codice che legge
            // non e quello che gira.
            // L'accoppiamento NON si fa per vicinanza di data: sarebbe un indovinello
            // e sbaglia. Si legge ogni sorgente e si guarda quali blocchi dichiara.
            var diChi = new Dictionary<string, KeyValuePair<string, DateTime>>(StringComparer.OrdinalIgnoreCase);
            string tmp = Path.Combine(Path.GetTempPath(), "s7tool_" + System.Diagnostics.Process.GetCurrentProcess().Id);
            Directory.CreateDirectory(tmp);
            try
            {
                foreach (dynamic s in Enum(Contenitore(prog, "sorgenti").Next))
                {
                    string nome = s.Name;
                    foreach (char c in Path.GetInvalidFileNameChars()) nome = nome.Replace(c, '_');
                    // STEP7 decide il formato dall'estensione: con una qualunque rifiuta.
                    string f = Path.Combine(tmp, nome + (((int)s.ConcreteType == 1122310) ? ".scl" : ".awl"));
                    try { s.Export(f); } catch { continue; }
                    var quando = new KeyValuePair<string, DateTime>(s.Name, (DateTime)s.Modified);
                    foreach (string dich in Dichiarati(f))
                        if (!diChi.ContainsKey(dich)) diChi[dich] = quando;
                }
            }
            finally { try { Directory.Delete(tmp, true); } catch { } }

            Console.WriteLine("{0,-26} {1,-19}   {2,-8} {3,-19}  {4}",
                              "sorgente", "salvato", "blocco", "compilato", "esito");
            foreach (dynamic b in Enum(Contenitore(prog, "blocchi").Next))
            {
                if (Str(() => b.Language) != "SCL") continue;
                DateTime bm = (DateTime)b.Modified;
                string nb = Str(() => b.Name), sb = Str(() => b.SymbolicName);

                KeyValuePair<string, DateTime> s7;
                bool trovato = (sb.Length > 0 && diChi.TryGetValue(sb, out s7)) || diChi.TryGetValue(nb, out s7);
                if (!trovato)
                {
                    Console.WriteLine("{0,-26} {1,-19}   {2,-8} {3,-19}  {4}",
                                      "(nessun sorgente)", "", nb, bm.ToString("yyyy-MM-dd HH:mm:ss"),
                                      "non modificabile senza riscriverlo");
                    continue;
                }

                // Lo scarto di un'ora delle coppie di aprile 2006 e l'ora legale
                // registrata in modo diverso nei due timestamp, non un ritardo.
                TimeSpan d = bm - s7.Value;
                string esito;
                if (d < TimeSpan.Zero) esito = "SORGENTE PIU RECENTE: modificato senza ricompilare";
                else if (d < TimeSpan.FromMinutes(5)) esito = "compilato subito dopo";
                else if (d < TimeSpan.FromMinutes(65)) esito = "compilato subito dopo (1 h di ora legale)";
                else esito = "ricompilato dopo, " + ((int)d.TotalDays) + " giorni";

                Console.WriteLine("{0,-26} {1,-19}   {2,-8} {3,-19}  {4}",
                                  s7.Key, s7.Value.ToString("yyyy-MM-dd HH:mm:ss"),
                                  nb, bm.ToString("yyyy-MM-dd HH:mm:ss"), esito);
            }
            return 0;
        }

        Uso();
        return 1;
    }

    static dynamic TrovaProgetto(string nome)
    {
        dynamic esatto = null, parziale = null; int nparz = 0;
        foreach (dynamic p in Enum(simatic.Projects))
        {
            string n = p.Name;
            if (string.Equals(n, nome, StringComparison.OrdinalIgnoreCase)) esatto = p;
            else if (n.IndexOf(nome, StringComparison.OrdinalIgnoreCase) >= 0) { parziale = p; nparz++; }
        }
        if (esatto != null) return esatto;
        if (nparz == 1) return parziale;
        if (nparz > 1) Console.Error.WriteLine("Il nome \"" + nome + "\" corrisponde a " + nparz + " progetti. Sii piu preciso.");
        else Console.Error.WriteLine("Nessun progetto chiamato \"" + nome + "\". Prova: S7Tool progetti");
        return null;
    }

    // Il programma buono e quello che ha davvero dei blocchi: in un progetto con
    // CP e stazioni ci sono decine di "programmi" che sono contenitori vuoti.
    static dynamic TrovaProgramma(dynamic prj)
    {
        dynamic migliore = null; int max = 0;
        foreach (dynamic pg in Enum(prj.Programs))
        {
            int n = 0;
            try
            {
                foreach (dynamic c in Enum(pg.Next))
                {
                    string cn = c.Name.ToLowerInvariant();
                    if (cn != "blocks" && cn != "blocchi") continue;
                    foreach (dynamic b in Enum(c.Next)) n++;
                }
            }
            catch { }
            if (n > max) { max = n; migliore = pg; }
        }
        return migliore;
    }

    static dynamic Contenitore(dynamic prog, string quale)
    {
        bool blocchi = (quale == "blocchi");
        foreach (dynamic c in Enum(prog.Next))
        {
            string n = c.Name.ToLowerInvariant();
            if (blocchi && (n == "blocks" || n == "blocchi")) return c;
            if (!blocchi && (n == "sources" || n == "sorgenti" || n == "fonti")) return c;
        }
        throw new Exception("contenitore \"" + quale + "\" non trovato nel programma");
    }

    // I nomi dei blocchi dichiarati in un sorgente. Possono essere simbolici e
    // fra virgolette (FUNCTION_BLOCK "Motore_1") oppure assoluti (FB10).
    static List<string> Dichiarati(string file)
    {
        var res = new List<string>();
        string[] chiavi = { "FUNCTION_BLOCK", "ORGANIZATION_BLOCK", "DATA_BLOCK", "FUNCTION" };
        foreach (string riga in File.ReadAllLines(file))
        {
            string r = riga.Trim();
            foreach (string k in chiavi)
            {
                if (!r.StartsWith(k, StringComparison.OrdinalIgnoreCase)) continue;
                if (r.Length <= k.Length || (r[k.Length] != ' ' && r[k.Length] != '\t')) continue;
                string n = r.Substring(k.Length).Trim();
                // fermarsi al primo separatore: "FUNCTION Conv_WR : VOID"
                int taglia = n.IndexOfAny(new char[] { ':', ' ', '\t' });
                if (n.StartsWith("\""))
                {
                    int fine = n.IndexOf('"', 1);
                    n = fine > 0 ? n.Substring(1, fine - 1) : n.Substring(1);
                }
                else if (taglia > 0) n = n.Substring(0, taglia);
                n = n.Trim();
                if (n.Length > 0) res.Add(n);
                break;
            }
        }
        return res;
    }

    static IEnumerable Enum(dynamic collezione)
    {
        return (IEnumerable)collezione;
    }

    // Le proprieta dei blocchi non ci sono tutte su tutti i tipi: una DB non ha
    // Language, una FC non ha ReadOnlyDB. Meglio una cella vuota di un'eccezione.
    static string Str(Func<string> f)
    {
        try { string s = f(); return s == null ? "" : s.Replace('\t', ' ').Trim(); }
        catch { return ""; }
    }
}
