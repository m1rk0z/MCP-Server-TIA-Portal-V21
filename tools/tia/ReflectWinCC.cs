using System;
using System.IO;
using System.Reflection;

namespace TiaBridge
{
    class Program
    {
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

            var asm = Assembly.LoadFrom(@"C:\Program Files\Siemens\Automation\Portal V21\PublicAPI\V21\net48\Siemens.Engineering.WinCC.dll");
            Console.WriteLine("Types in Siemens.Engineering.WinCC.dll:");
            foreach (var t in asm.GetExportedTypes())
            {
                Console.WriteLine("  " + t.FullName);
                if (t.Name.Contains("Hmi") || t.Name.Contains("Target") || t.Name.Contains("Software") || t.Name.Contains("Screen") || t.Name.Contains("Text"))
                {
                    foreach (var m in t.GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                    {
                        Console.WriteLine("    [" + m.MemberType + "] " + m.Name);
                    }
                }
            }
        }
    }
}
