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
            Run();
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

        static void Run()
        {
            Console.WriteLine("Searching for running TIA Portal instances...");
            var processes = TiaPortal.GetProcesses();
            Console.WriteLine("Found " + processes.Count + " process(es).");
            if (processes.Count == 0) return;

            var p = processes[0];
            Console.WriteLine("Attaching to TIA Portal PID: " + p.Id + " (" + p.ProjectPath + ")...");
            using (var tia = p.Attach())
            {
                if (tia.Projects.Count == 0)
                {
                    Console.WriteLine("No project currently open in this TIA Portal instance.");
                    return;
                }

                var proj = tia.Projects[0];
                Console.WriteLine("Connected to Project: " + proj.Name);
                Console.WriteLine("Project Path: " + proj.Path);

                Console.WriteLine("\n=== DEVICES IN PROJECT ===");
                foreach (Device dev in proj.Devices)
                {
                    Console.WriteLine("Device: " + dev.Name + " (" + dev.TypeIdentifier + ")");
                    InspectDeviceItems(dev.DeviceItems, "  ");
                }
            }
        }

        static void InspectDeviceItems(DeviceItemComposition items, string indent)
        {
            foreach (DeviceItem item in items)
            {
                Console.WriteLine(indent + "* Item: " + item.Name + " [" + item.Classification + "]");
                InspectDeviceItems(item.DeviceItems, indent + "  ");
            }
        }
    }
}
