// Installer.cs - installa / rimuove l'agente con strumenti presenti su ogni Windows
// (netsh http, netsh advfirewall, icacls, chiave Run). Richiede i diritti di amministratore.

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Principal;
using System.Text;
using System.Threading;
using Microsoft.Win32;

namespace TiaAgent
{
    public sealed class InstallSettings
    {
        public int Port;
        public string AccessMode = "read-only";
        public List<string> AllowedClients = new List<string>();
        public bool NewToken;
    }

    public static class Installer
    {
        public const string AppName = "TIA MCP Agent";
        const string FirewallRule = "TIA MCP Agent";
        const string RunKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
        const string RunValue = "TiaAgent";
        const string UninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\TiaAgent";
        public const int MinPort = 1024, MaxPort = 65535;

        /// <summary>Contrassegna la copia elevata avviata da RelaunchElevated; seguito dal PID del processo che la aspetta.</summary>
        public const string ElevatedChildFlag = "--elevated-child";

        /// <summary>Program Files (64 bit, come TIA Portal).</summary>
        public static string InstallDir { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "TiaAgent"); } }
        public static string InstalledExe { get { return Path.Combine(InstallDir, "TiaAgent.exe"); } }

        public static bool IsAdministrator()
        {
            using (var id = WindowsIdentity.GetCurrent())
                return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
        }

        /// <summary>Riavvia questo eseguibile con i diritti di amministratore (UAC), lo aspetta e ne restituisce il codice (1223 = annullato).</summary>
        public static int RelaunchElevated(string[] args)
        {
            try
            {
                var all = args.Concat(new[] { ElevatedChildFlag, Process.GetCurrentProcess().Id.ToString() }).Select(QuoteArg);
                var psi = new ProcessStartInfo(Process.GetCurrentProcess().MainModule.FileName, string.Join(" ", all))
                { UseShellExecute = true, Verb = "runas" };
                using (Process p = Process.Start(psi))
                {
                    p.WaitForExit();
                    return p.ExitCode;
                }
            }
            catch (Win32Exception) { return 1223; }
        }

        /// <summary>Avvia l'agente installato da un processo non elevato: gira come questo utente, senza elevazione.</summary>
        public static void StartInstalledAgent()
        {
            try { if (File.Exists(InstalledExe)) Process.Start(new ProcessStartInfo(InstalledExe) { UseShellExecute = false }); }
            catch (Exception ex) { AgentLog.Write("cannot start agent after install: " + ex.Message); }
        }

        public static AgentConfig Install(InstallSettings settings, Action<string> progress, bool startAgent, int parentPid)
        {
            if (settings.Port < MinPort || settings.Port > MaxPort)
                throw new ArgumentException("Porta non valida: " + settings.Port + " (ammessa " + MinPort + "-" + MaxPort + ").");
            string sourceDir = AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\');
            AgentConfig cfg = AgentConfig.LoadOrDefault();
            int oldPort = cfg.Port;

            progress("Arresto dell'agente in esecuzione...");
            StopRunningAgents(parentPid);

            progress("Copia dei file in " + InstallDir + "...");
            if (!string.Equals(Path.GetFullPath(sourceDir), Path.GetFullPath(InstallDir), StringComparison.OrdinalIgnoreCase))
                CopyDirectory(sourceDir, InstallDir);

            progress("Configurazione...");
            cfg.Port = settings.Port;
            cfg.AccessMode = settings.AccessMode;
            cfg.AllowedClients = settings.AllowedClients;
            if (settings.NewToken || string.IsNullOrEmpty(cfg.Token)) cfg.Token = AgentConfig.NewToken();
            Directory.CreateDirectory(AgentConfig.DataDir);
            Directory.CreateDirectory(cfg.WorkDir);
            Directory.CreateDirectory(AgentConfig.LogDir);
            cfg.Save();
            SecureDataFolder(cfg);

            progress("Prenotazione URL HTTP sulla porta " + cfg.Port + "...");
            if (oldPort != cfg.Port) Run("netsh", "http delete urlacl url=http://+:" + oldPort + "/", false);
            Run("netsh", "http delete urlacl url=http://+:" + cfg.Port + "/", false);
            // WD = Everyone (indipendente dalla lingua); l'accesso e protetto dal token.
            Run("netsh", "http add urlacl url=http://+:" + cfg.Port + "/ sddl=D:(A;;GX;;;WD)", true);

            progress("Regola firewall...");
            Run("netsh", "advfirewall firewall delete rule name=\"" + FirewallRule + "\"", false);
            string remote = cfg.AllowedClients.Count > 0 ? " remoteip=" + string.Join(",", cfg.AllowedClients) : "";
            Run("netsh", "advfirewall firewall add rule name=\"" + FirewallRule + "\" dir=in action=allow protocol=TCP localport=" + cfg.Port + " profile=any" + remote, false);

            progress("Avvio automatico all'accesso...");
            using (RegistryKey run = Registry.LocalMachine.CreateSubKey(RunKey))
                run.SetValue(RunValue, "\"" + InstalledExe + "\"");

            using (RegistryKey un = Registry.LocalMachine.CreateSubKey(UninstallKey))
            {
                un.SetValue("DisplayName", AppName);
                un.SetValue("DisplayVersion", typeof(Installer).Assembly.GetName().Version.ToString(3));
                un.SetValue("Publisher", "tia-mcp");
                un.SetValue("InstallLocation", InstallDir);
                un.SetValue("DisplayIcon", InstalledExe);
                un.SetValue("UninstallString", "\"" + InstalledExe + "\" uninstall");
                un.SetValue("NoModify", 1, RegistryValueKind.DWord);
                un.SetValue("NoRepair", 1, RegistryValueKind.DWord);
            }

            File.WriteAllText(Path.Combine(AgentConfig.DataDir, "client-config.txt"), ClientInstructions(cfg), new UTF8Encoding(true));

            if (startAgent)
            {
                progress("Avvio dell'agente...");
                StartAgentAsDesktopUser();
            }
            AgentLog.Write("installed version " + typeof(Installer).Assembly.GetName().Version + " in " + InstallDir +
                           ", port " + cfg.Port + ", mode " + cfg.AccessMode + ", servers " + string.Join(",", AgentConfig.Servers().Keys));
            return cfg;
        }

        public static void Uninstall(bool purge, Action<string> progress, int parentPid)
        {
            AgentConfig cfg = AgentConfig.LoadOrDefault();
            progress("Arresto dell'agente...");
            StopRunningAgents(parentPid);
            progress("Rimozione di avvio automatico, firewall e prenotazione URL...");
            try { using (RegistryKey run = Registry.LocalMachine.OpenSubKey(RunKey, true)) if (run != null) run.DeleteValue(RunValue, false); } catch { }
            try { Registry.LocalMachine.DeleteSubKeyTree(UninstallKey, false); } catch { }
            Run("netsh", "advfirewall firewall delete rule name=\"" + FirewallRule + "\"", false);
            Run("netsh", "http delete urlacl url=http://+:" + cfg.Port + "/", false);
            progress("Eliminazione dei file del programma...");
            string self = AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\');
            if (string.Equals(Path.GetFullPath(self), Path.GetFullPath(InstallDir), StringComparison.OrdinalIgnoreCase))
            {
                // L'eseguibile in uso non puo cancellare la propria cartella: un cmd separato riprova per 2 minuti.
                string dir = InstallDir;
                Process.Start(new ProcessStartInfo("cmd.exe",
                    "/c for /l %i in (1,1,60) do (ping 127.0.0.1 -n 3 >nul & rmdir /s /q \"" + dir + "\" 2>nul & if not exist \"" + dir + "\" exit)")
                { CreateNoWindow = true, UseShellExecute = false, WindowStyle = ProcessWindowStyle.Hidden });
            }
            else if (Directory.Exists(InstallDir))
            {
                try { Directory.Delete(InstallDir, true); } catch { }
            }
            if (purge)
            {
                // Ultimo passo, senza log dopo (il log ricreerebbe la cartella).
                progress("Eliminazione di configurazione, log e file di lavoro...");
                try { Directory.Delete(AgentConfig.DataDir, true); } catch { }
            }
        }

        /// <summary>
        /// agent.json contiene token e modalita: solo gli amministratori lo modificano, gli utenti (l'agente) lo leggono.
        /// Gli utenti scrivono solo in work e logs. SID indipendenti dalla lingua:
        /// S-1-5-32-544 Administrators, S-1-5-18 SYSTEM, S-1-5-32-545 Users.
        /// </summary>
        static void SecureDataFolder(AgentConfig cfg)
        {
            // Solo la cartella (non /T: sui file i flag (OI)(CI) non valgono e lascerebbero un ACL vuoto),
            // poi tutto il contenuto torna a ereditare da lei.
            Run("icacls", "\"" + AgentConfig.DataDir + "\" /inheritance:r /grant:r *S-1-5-32-544:(OI)(CI)F *S-1-5-18:(OI)(CI)F *S-1-5-32-545:(OI)(CI)RX /C /Q", true);
            Run("icacls", "\"" + Path.Combine(AgentConfig.DataDir, "*") + "\" /reset /T /C /Q", true);
            foreach (string dir in new[] { cfg.WorkDir, AgentConfig.LogDir })
                Run("icacls", "\"" + dir + "\" /grant *S-1-5-32-545:(OI)(CI)M /T /C /Q", true);
        }

        public static string ClientInstructions(AgentConfig cfg)
        {
            List<string> ips = AgentConfig.LocalAddresses();
            var sb = new StringBuilder();
            sb.AppendLine("TIA MCP Agent - configurazione per Claude Code sul PC client");
            sb.AppendLine("============================================================");
            sb.AppendLine();
            sb.AppendLine("Macchina:          " + Environment.MachineName);
            sb.AppendLine("Indirizzi IP:      " + (ips.Count > 0 ? string.Join(", ", ips) : "(nessuno rilevato)"));
            sb.AppendLine("Porta:             " + cfg.Port);
            sb.AppendLine("Modalita':         " + cfg.AccessMode);
            sb.AppendLine("TIA Portal:        " + string.Join(", ", AgentConfig.Servers().Keys));
            sb.AppendLine("Client consentiti: " + (cfg.AllowedClients.Count > 0 ? string.Join(", ", cfg.AllowedClients) : "tutti (serve comunque il token)"));
            sb.AppendLine("Token:             " + cfg.Token);
            sb.AppendLine();
            sb.AppendLine("Sul PC con Claude Code, in %USERPROFILE%\\.claude.json dentro \"mcpServers\":");
            sb.AppendLine();
            sb.AppendLine(cfg.ClientSnippet(ips.FirstOrDefault()));
            sb.AppendLine();
            sb.AppendLine("Sostituire C:\\percorso\\tia-mcp con la cartella del repository sul PC.");
            sb.AppendLine("In read-only i server TIA partono con --read-only: ogni scrittura viene rifiutata.");
            return sb.ToString();
        }

        /// <summary>
        /// Ferma l'agente e i server TIA avviati dalla cartella di installazione (bloccano i file).
        /// I TiaMcpServer di altre cartelle (installazioni locali, sviluppo) non si toccano.
        /// </summary>
        public static void StopRunningAgents(int parentPid)
        {
            int me = Process.GetCurrentProcess().Id;
            string install = Path.GetFullPath(InstallDir).TrimEnd('\\') + "\\";
            foreach (Process p in Process.GetProcessesByName("TiaAgent").Concat(Process.GetProcessesByName("TiaMcpServer")))
            {
                if (p.Id == me || p.Id == parentPid) continue;
                if (p.ProcessName == "TiaMcpServer")
                {
                    string path = null;
                    try { path = p.MainModule.FileName; } catch { }
                    if (path == null || !path.StartsWith(install, StringComparison.OrdinalIgnoreCase)) continue;
                }
                try { p.Kill(); p.WaitForExit(5000); } catch { }
            }
        }

        /// <summary>Avvia l'agente non elevato nella sessione del desktop (explorer.exe usa il token normale dell'utente).</summary>
        static void StartAgentAsDesktopUser()
        {
            try { Process.Start(new ProcessStartInfo("explorer.exe", "\"" + InstalledExe + "\"") { UseShellExecute = false }); }
            catch (Exception ex) { AgentLog.Write("cannot start agent after install: " + ex.Message); }
        }

        static void CopyDirectory(string source, string target)
        {
            Directory.CreateDirectory(target);
            foreach (string dir in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
                Directory.CreateDirectory(target + dir.Substring(source.Length));
            foreach (string file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
            {
                string dest = target + file.Substring(source.Length);
                for (int attempt = 0; ; attempt++)
                {
                    try { File.Copy(file, dest, true); break; }
                    catch (IOException) { if (attempt >= 10) throw; Thread.Sleep(500); }
                }
            }
        }

        static void Run(string exe, string args, bool mustSucceed)
        {
            var psi = new ProcessStartInfo(exe, args)
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
            };
            using (Process p = Process.Start(psi))
            {
                string output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
                p.WaitForExit();
                AgentLog.Write("setup: " + exe + " " + args + " -> exit " + p.ExitCode);
                if (mustSucceed && p.ExitCode != 0)
                    throw new InvalidOperationException(exe + " " + args + " fallito (exit " + p.ExitCode + "): " + output.Trim());
            }
        }

        public static List<string> ParseClients(string text)
        {
            var list = new List<string>();
            foreach (string part in text.Split(new[] { ',', ';', ' ', '\n', '\r', '\t' }, StringSplitOptions.RemoveEmptyEntries))
            {
                IPAddress ip;
                if (!IPAddress.TryParse(part.Trim(), out ip)) throw new ArgumentException("Indirizzo IP non valido: " + part);
                list.Add(ip.ToString());
            }
            return list.Distinct().ToList();
        }

        /// <summary>Quoting compatibile con CommandLineToArgvW.</summary>
        public static string QuoteArg(string arg)
        {
            if (arg.Length > 0 && arg.IndexOfAny(new[] { ' ', '\t', '"' }) < 0) return arg;
            var sb = new StringBuilder("\"");
            int backslashes = 0;
            foreach (char c in arg)
            {
                if (c == '\\') { backslashes++; continue; }
                if (c == '"') { sb.Append('\\', backslashes * 2 + 1); sb.Append('"'); }
                else { sb.Append('\\', backslashes); sb.Append(c); }
                backslashes = 0;
            }
            sb.Append('\\', backslashes * 2);
            return sb.Append('"').ToString();
        }
    }
}
