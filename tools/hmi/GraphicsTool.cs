// GraphicsTool - esporta/importa le grafiche di progetto (Project.Graphics) di TIA Portal V19 via Openness.
//
//   GraphicsTool list   <pid>
//   GraphicsTool export <pid> <nome grafica> <cartella out>
//   GraphicsTool import <pid> <file xml>          (Override: sostituisce la grafica con lo stesso nome)
//   GraphicsTool archive <pid> <cartella> <nome.zap19>   (archivio compresso del progetto aperto)
//
// Si aggancia all'istanza TIA con il pid indicato (quella con il progetto aperto).
// Il server MCP non espone le grafiche: questo strumento serve solo a quello.

using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;

static class GraphicsTool
{
    const string Api = @"C:\Program Files\Siemens\Automation\Portal V19\PublicAPI\V19";

    [STAThread]
    static int Main(string[] args)
    {
        AppDomain.CurrentDomain.AssemblyResolve += (s, e) =>
        {
            string f = Path.Combine(Api, new AssemblyName(e.Name).Name + ".dll");
            return File.Exists(f) ? Assembly.LoadFrom(f) : null;
        };
        try { return Run(args); }
        catch (Exception ex) { Console.Error.WriteLine("ERRORE: " + ex); return 1; }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static int Run(string[] args)
    {
        if (args.Length < 2) { Console.Error.WriteLine("uso: list|export|import <pid> ..."); return 2; }
        int pid = int.Parse(args[1]);
        var proc = Siemens.Engineering.TiaPortal.GetProcesses().FirstOrDefault(p => p.Id == pid);
        if (proc == null) { Console.Error.WriteLine("istanza TIA non trovata: " + pid); return 2; }
        using (var portal = proc.Attach())
        {
            var project = portal.Projects.First();
            var graphics = project.Graphics;
            switch (args[0])
            {
                case "list":
                    foreach (var g in graphics) Console.WriteLine(g.Name);
                    return 0;
                case "export":
                {
                    var g = graphics.Find(args[2]);
                    if (g == null) { Console.Error.WriteLine("grafica non trovata: " + args[2]); return 3; }
                    Directory.CreateDirectory(args[3]);
                    var xml = new FileInfo(Path.Combine(args[3], g.Name + ".xml"));
                    if (xml.Exists) xml.Delete();
                    g.Export(xml, Siemens.Engineering.ExportOptions.WithDefaults);
                    Console.WriteLine("esportata " + g.Name + " -> " + xml.FullName);
                    return 0;
                }
                case "archive":
                {
                    // archivio compresso (.zap19) del progetto aperto, come "Archivia" di TIA
                    Directory.CreateDirectory(args[2]);
                    project.Archive(new DirectoryInfo(args[2]), args[3], Siemens.Engineering.ProjectArchivationMode.Compressed);
                    Console.WriteLine("archiviato " + project.Name + " -> " + Path.Combine(args[2], args[3]));
                    return 0;
                }
                case "import":
                {
                    var res = graphics.Import(new FileInfo(args[2]), Siemens.Engineering.ImportOptions.Override);
                    foreach (var g in res) Console.WriteLine("importata " + g.Name);
                    return 0;
                }
            }
            Console.Error.WriteLine("comando sconosciuto: " + args[0]);
            return 2;
        }
    }
}
