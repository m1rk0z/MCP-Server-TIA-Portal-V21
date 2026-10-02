using System;
using System.IO;
using System.Reflection;
using Siemens.Engineering;
using Siemens.Engineering.Hmi;
using Siemens.Engineering.Hmi.Screen;
using Siemens.Engineering.HW;
using Siemens.Engineering.HW.Features;

namespace TiaBridge
{
    class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            AppDomain.CurrentDomain.AssemblyResolve += OnAssemblyResolve;
            ExportHomeScreen();
        }

        static Assembly OnAssemblyResolve(object sender, ResolveEventArgs args)
        {
            string name = new AssemblyName(args.Name).Name;
            string p1 = Path.Combine(@"C:\Program Files\Siemens\Automation\Portal V21\PublicAPI\V21\net48", name + ".dll");
            if (File.Exists(p1)) return Assembly.LoadFrom(p1);
            string p2 = Path.Combine(@"C:\Program Files\Siemens\Automation\Portal V21\Bin", name + ".dll");
            if (File.Exists(p2)) return Assembly.LoadFrom(p2);
            return null;
        }

        static void ExportHomeScreen()
        {
            var processes = TiaPortal.GetProcesses();
            if (processes.Count == 0) return;
            using (var tia = processes[0].Attach())
            {
                var proj = tia.Projects[0];
                foreach (Device dev in proj.Devices)
                {
                    foreach (DeviceItem item in dev.DeviceItems)
                    {
                        var container = item.GetService<SoftwareContainer>();
                        if (container != null && container.Software is HmiTarget)
                        {
                            var target = (HmiTarget)container.Software;
                            string nome = Environment.GetCommandLineArgs().Length > 1 ? Environment.GetCommandLineArgs()[1] : "HOME";
                            Screen home = null;
                            foreach (var f in target.ScreenFolder.Folders)
                                if (home == null) home = f.Screens.Find(nome);
                            if (home == null) home = target.ScreenFolder.Screens.Find(nome);
                            if (home != null)
                            {
                                string outPath = Path.Combine(Environment.CurrentDirectory, nome + "_exported.xml");
                                Console.WriteLine("Esporto " + nome + " -> " + outPath);
                                home.Export(new FileInfo(outPath), ExportOptions.WithDefaults);
                                Console.WriteLine("Export completed successfully!");
                            }
                        }
                    }
                }
            }
        }
    }
}
