using System;
using System.IO;
using System.Reflection;
using Siemens.Engineering;
using Siemens.Engineering.Hmi;
using Siemens.Engineering.Hmi.TextGraphicList;
using Siemens.Engineering.HW;
using Siemens.Engineering.HW.Features;

namespace TiaBridge
{
    class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            if (args.Length == 0) { Console.WriteLine("uso: ImportTl <file xml della lista testi>"); return; }
            AppDomain.CurrentDomain.AssemblyResolve += OnAssemblyResolve;
            ImportTextList(args[0]);
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

        static void ImportTextList(string xmlPath)
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
                            Console.WriteLine("Importing TextList from: " + xmlPath);
                            var imported = target.TextLists.Import(new FileInfo(xmlPath), ImportOptions.Override);
                            Console.WriteLine("Imported count: " + imported.Count);
                            foreach (var tl in imported)
                            {
                                Console.WriteLine("Successfully created TextList: " + tl.Name);
                            }
                        }
                    }
                }
            }
        }
    }
}
