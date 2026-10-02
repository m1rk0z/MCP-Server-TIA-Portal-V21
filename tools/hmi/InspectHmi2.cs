using System;
using System.IO;
using System.Reflection;
using Siemens.Engineering;
using Siemens.Engineering.Hmi;
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
            InspectHmi();
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

        static void InspectHmi()
        {
            var processes = TiaPortal.GetProcesses();
            if (processes.Count == 0) return;
            using (var tia = processes[0].Attach())
            {
                var proj = tia.Projects[0];
                Console.WriteLine("Project: " + proj.Name);

                foreach (Device dev in proj.Devices)
                {
                    Console.WriteLine("\nDevice: " + dev.Name);
                    FindHmiSoftware(dev.DeviceItems);
                }
            }
        }

        static void FindHmiSoftware(DeviceItemComposition items)
        {
            foreach (DeviceItem item in items)
            {
                var container = item.GetService<SoftwareContainer>();
                if (container != null && container.Software is HmiTarget)
                {
                    var target = (HmiTarget)container.Software;
                    Console.WriteLine("  Found HmiTarget on item: " + item.Name);
                    Console.WriteLine("  Target Name: " + target.Name);

                    // Inspect Tag Tables
                    Console.WriteLine("  --- Tag Tables ---");
                    foreach (var tbl in target.TagFolder.TagTables)
                    {
                        Console.WriteLine("    Table: " + tbl.Name + " (Tags: " + tbl.Tags.Count + ")");
                    }
                    foreach (var f in target.TagFolder.Folders)
                    {
                        Console.WriteLine("    Folder: " + f.Name);
                        foreach (var tbl in f.TagTables)
                        {
                            Console.WriteLine("      Table: " + tbl.Name + " (Tags: " + tbl.Tags.Count + ")");
                        }
                    }

                    // Inspect Screens
                    Console.WriteLine("  --- Screens ---");
                    foreach (var scr in target.ScreenFolder.Screens)
                    {
                        Console.WriteLine("    Screen: " + scr.Name);
                    }
                    foreach (var f in target.ScreenFolder.Folders)
                    {
                        Console.WriteLine("    Screen Folder: " + f.Name);
                        foreach (var scr in f.Screens)
                        {
                            Console.WriteLine("      Screen: " + scr.Name);
                        }
                    }

                    // Inspect Text Lists
                    Console.WriteLine("  --- Text Lists ---");
                    foreach (var tl in target.TextLists)
                    {
                        Console.WriteLine("    TextList: " + tl.Name);
                    }

                    // Inspect Templates
                    Console.WriteLine("  --- Screen Templates ---");
                    foreach (var tmpl in target.ScreenTemplateFolder.ScreenTemplates)
                    {
                        Console.WriteLine("    Template: " + tmpl.Name);
                    }
                }
                FindHmiSoftware(item.DeviceItems);
            }
        }
    }
}
