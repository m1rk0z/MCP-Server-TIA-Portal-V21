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
            var t = asm.GetType("Siemens.Engineering.Hmi.Screen.ScreenFolderComposition");
            Console.WriteLine("Methods on ScreenFolderComposition:");
            foreach (var m in t.GetMethods())
            {
                Console.WriteLine(" - " + m.Name + "(" + string.Join(", ", Array.ConvertAll(m.GetParameters(), p => p.ParameterType.Name + " " + p.Name)) + ")");
            }
        }
    }
}
