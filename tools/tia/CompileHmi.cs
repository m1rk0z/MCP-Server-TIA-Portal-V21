using System;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using Siemens.Engineering;
using Siemens.Engineering.Hmi;
using Siemens.Engineering.HW;
using Siemens.Engineering.HW.Features;
using Siemens.Engineering.Compiler;

namespace TiaBridge
{
    class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            AppDomain.CurrentDomain.AssemblyResolve += (s, e) => {
                string name = new AssemblyName(e.Name).Name;
                string p1 = Path.Combine(@"C:\Program Files\Siemens\Automation\Portal V21\PublicAPI\V21\net48", name + ".dll");
                if (File.Exists(p1)) return Assembly.LoadFrom(p1);
                string p2 = Path.Combine(@"C:\Program Files\Siemens\Automation\Portal V21\Bin", name + ".dll");
                if (File.Exists(p2)) return Assembly.LoadFrom(p2);
                return null;
            };

            Execute();
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static void Execute()
        {
            var processes = TiaPortal.GetProcesses();
            if (processes.Count == 0)
            {
                Console.WriteLine("No running TIA Portal found.");
                return;
            }

            using (var tia = processes[0].Attach())
            {
                var proj = tia.Projects[0];
                Console.WriteLine("Project: " + proj.Name);

                foreach (Device dev in proj.Devices)
                {
                    Console.WriteLine("Checking Device: " + dev.Name);
                    var comp = dev.GetService<ICompilable>();
                    if (comp != null)
                    {
                        Console.WriteLine("Compiling device " + dev.Name + "...");
                        var res = comp.Compile();
                        Console.WriteLine("Compile Result: State = " + res.State + ", Errors = " + res.ErrorCount + ", Warnings = " + res.WarningCount);
                    }

                    foreach (DeviceItem item in dev.DeviceItems)
                    {
                        var container = item.GetService<SoftwareContainer>();
                        if (container != null && container.Software is HmiTarget)
                        {
                            var target = (HmiTarget)container.Software;
                            var targetComp = target.GetService<ICompilable>();
                            if (targetComp != null)
                            {
                                Console.WriteLine("Compiling HMI Target " + target.Name + "...");
                                var res = targetComp.Compile();
                                Console.WriteLine("HMI Compile Result: State = " + res.State + ", Errors = " + res.ErrorCount + ", Warnings = " + res.WarningCount);
                            }
                        }
                    }
                }

                proj.Save();
                Console.WriteLine("Project saved successfully!");
            }
        }
    }
}
