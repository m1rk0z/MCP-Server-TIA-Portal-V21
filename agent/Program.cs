// Program.cs - TiaAgent: espone i server MCP di TIA Portal di questa macchina al client sul PC.
//
//   TiaAgent.exe                avvia l'agente (icona nella tray + endpoint HTTP)
//   TiaAgent.exe install [--port N] [--access-mode read-only|read-write] [--allow ip,ip] [--new-token] [--silent]
//   TiaAgent.exe uninstall [--purge] [--silent]
//   TiaAgent.exe config         mostra la configurazione per il client (token, blocco .claude.json)

using System;
using System.Linq;
using System.Threading;
using System.Windows.Forms;

namespace TiaAgent
{
    static class Program
    {
        [STAThread]
        static int Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            string command = args.Length > 0 ? args[0].ToLowerInvariant() : "run";
            bool silent = args.Any(a => a.Equals("--silent", StringComparison.OrdinalIgnoreCase));
            try
            {
                switch (command)
                {
                    case "run": return RunAgent();
                    case "install": return Install(args, silent);
                    case "uninstall": return Uninstall(args, silent);
                    case "config": return ShowConfig();
                    default:
                        MessageBox.Show("Comando sconosciuto: " + command + "\n\nUso: TiaAgent.exe [install|uninstall|config]", Installer.AppName);
                        return 2;
                }
            }
            catch (Exception ex)
            {
                AgentLog.Write(command + " failed: " + ex);
                if (!silent) MessageBox.Show(ex.Message, Installer.AppName, MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 1;
            }
        }

        static int RunAgent()
        {
            bool first;
            using (var mutex = new Mutex(true, @"Global\TiaAgent", out first))
            {
                if (!first) return 0; // gia in esecuzione
                AgentLog.Cleanup();
                AgentConfig config = AgentConfig.Load();
                AgentLog.Write("agent " + typeof(Program).Assembly.GetName().Version + " starting as " + Environment.UserDomainName + "\\" + Environment.UserName);
                Application.Run(new TrayApp(config));
                GC.KeepAlive(mutex);
                return 0;
            }
        }

        static int Install(string[] args, bool silent)
        {
            if (!Installer.IsAdministrator())
            {
                // La copia elevata installa; questo processo non elevato avvia poi l'agente come utente giusto.
                int code = Installer.RelaunchElevated(args);
                if (code == 0) Installer.StartInstalledAgent();
                return code;
            }
            int parentPid;
            int.TryParse(Opt(args, Installer.ElevatedChildFlag), out parentPid);

            AgentConfig current = AgentConfig.LoadOrDefault();
            InstallSettings settings;
            if (silent)
            {
                int p;
                string mode = Opt(args, "--access-mode");
                string allow = Opt(args, "--allow");
                settings = new InstallSettings
                {
                    Port = int.TryParse(Opt(args, "--port"), out p) ? p : current.Port,
                    AccessMode = mode == null ? current.AccessMode : NormalizeMode(mode),
                    AllowedClients = allow == null ? current.AllowedClients : Installer.ParseClients(allow),
                    NewToken = args.Contains("--new-token", StringComparer.OrdinalIgnoreCase),
                };
            }
            else
            {
                using (var form = new InstallForm(current))
                {
                    if (form.ShowDialog() != DialogResult.OK || form.Settings == null) return 1;
                    settings = form.Settings;
                }
            }

            AgentConfig cfg = Installer.Install(settings, delegate(string m) { AgentLog.Write("setup: " + m); }, parentPid == 0, parentPid);
            if (!silent)
                using (var f = new ClientConfigForm(cfg, "Installazione completata: l'agente parte alla chiusura di questa finestra (icona TIA nella tray)."))
                    f.ShowDialog();
            return 0;
        }

        static string NormalizeMode(string mode)
        {
            string m = mode.Trim().ToLowerInvariant();
            if (m == "read-only" || m == "readonly") return "read-only";
            if (m == "read-write" || m == "readwrite") return "read-write";
            throw new ArgumentException("Modalita' non valida: " + mode + " (read-only o read-write)");
        }

        static int Uninstall(string[] args, bool silent)
        {
            if (!Installer.IsAdministrator()) return Installer.RelaunchElevated(args);
            bool purge = args.Contains("--purge", StringComparer.OrdinalIgnoreCase);
            if (!silent)
            {
                DialogResult answer = MessageBox.Show(
                    "Disinstallare " + Installer.AppName + "?\n\nSi' = rimuove il programma e mantiene configurazione, token e file in " + AgentConfig.DataDir +
                    "\nNo = rimuove anche configurazione, log e file di lavoro",
                    Installer.AppName, MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
                if (answer == DialogResult.Cancel) return 1;
                purge = answer == DialogResult.No;
            }
            int parentPid;
            int.TryParse(Opt(args, Installer.ElevatedChildFlag), out parentPid);
            Installer.Uninstall(purge, delegate(string m) { AgentLog.Write("uninstall: " + m); }, parentPid);
            if (!silent) MessageBox.Show(Installer.AppName + " disinstallato.", Installer.AppName, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return 0;
        }

        static int ShowConfig()
        {
            using (var f = new ClientConfigForm(AgentConfig.Load(), null)) f.ShowDialog();
            return 0;
        }

        static string Opt(string[] args, string name)
        {
            int i = Array.FindIndex(args, a => a.Equals(name, StringComparison.OrdinalIgnoreCase));
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        }
    }
}
