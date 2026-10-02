// AgentLog.cs - log giornalieri in %ProgramData%\TiaAgent\logs, tenuti 30 giorni.

using System;
using System.IO;
using System.Linq;
using System.Text;

namespace TiaAgent
{
    public static class AgentLog
    {
        static readonly object Lock = new object();

        public static void Write(string message)
        {
            try
            {
                lock (Lock)
                {
                    Directory.CreateDirectory(AgentConfig.LogDir);
                    string file = Path.Combine(AgentConfig.LogDir, "agent-" + DateTime.Now.ToString("yyyyMMdd") + ".log");
                    File.AppendAllText(file, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + " " + message + Environment.NewLine, Encoding.UTF8);
                }
            }
            catch { }
        }

        public static void Cleanup()
        {
            try
            {
                foreach (FileInfo f in new DirectoryInfo(AgentConfig.LogDir).GetFiles("*.log").Where(f => f.LastWriteTime < DateTime.Now.AddDays(-30)))
                    f.Delete();
            }
            catch { }
        }
    }
}
