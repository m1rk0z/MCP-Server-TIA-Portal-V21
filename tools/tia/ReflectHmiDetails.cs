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
            var textListCompType = asm.GetType("Siemens.Engineering.Hmi.TextGraphicList.TextListComposition");
            Console.WriteLine("=== TextListComposition Methods ===");
            foreach (var m in textListCompType.GetMethods())
            {
                Console.WriteLine("  " + m.ToString());
            }

            var textListType = asm.GetType("Siemens.Engineering.Hmi.TextGraphicList.TextList");
            Console.WriteLine("\n=== TextList Methods & Properties ===");
            foreach (var m in textListType.GetMembers())
            {
                Console.WriteLine("  [" + m.MemberType + "] " + m.ToString());
            }

            var screenCompType = asm.GetType("Siemens.Engineering.Hmi.Screen.ScreenComposition");
            Console.WriteLine("\n=== ScreenComposition Methods ===");
            foreach (var m in screenCompType.GetMethods())
            {
                Console.WriteLine("  " + m.ToString());
            }

            var screenType = asm.GetType("Siemens.Engineering.Hmi.Screen.Screen");
            Console.WriteLine("\n=== Screen Methods & Properties ===");
            foreach (var m in screenType.GetMembers())
            {
                Console.WriteLine("  [" + m.MemberType + "] " + m.ToString());
            }
        }
    }
}
