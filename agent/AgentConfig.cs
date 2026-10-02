// AgentConfig.cs - impostazioni dell'agente, in %ProgramData%\TiaAgent\agent.json (scritte dall'installer).
//
// C# 5: e compilato dal csc di .NET Framework, come il server.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using TiaMcp;

namespace TiaAgent
{
    public sealed class AgentConfig
    {
        public const int DefaultPort = 8766;

        /// <summary>%ProgramData%\TiaAgent, oppure TIA_AGENT_DATA (uso portatile, test).</summary>
        public static readonly string DataDir = DataDirectory();

        public static string ConfigPath { get { return Path.Combine(DataDir, "agent.json"); } }
        public static string LogDir { get { return Path.Combine(DataDir, "logs"); } }

        public int Port = DefaultPort;

        /// <summary>"+" = tutte le interfacce (serve la prenotazione URL dell'installer); "localhost" = solo locale.</summary>
        public string ListenHost = "+";

        /// <summary>Segreto condiviso con il client ("Authorization: Bearer ...").</summary>
        public string Token = "";

        /// <summary>read-only: i server TIA partono con --read-only e rifiutano ogni scrittura, qualunque cosa chieda il client.</summary>
        public string AccessMode = "read-only";

        /// <summary>IPv4 dei client ammessi. Vuoto = tutti (il token resta obbligatorio).</summary>
        public List<string> AllowedClients = new List<string>();

        /// <summary>Cartella dei file scambiati con il client (export e import), una sottocartella per sessione.</summary>
        public string WorkDir = Path.Combine(DataDirectory(), "work");

        /// <summary>Una sessione senza richieste per questo tempo viene chiusa (il server TIA esce e rilascia Openness).</summary>
        public int SessionIdleMinutes = 720;

        public bool IsReadWrite { get { return string.Equals(AccessMode, "read-write", StringComparison.OrdinalIgnoreCase); } }

        static string DataDirectory()
        {
            string custom = Environment.GetEnvironmentVariable("TIA_AGENT_DATA");
            if (!string.IsNullOrEmpty(custom)) return custom;
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "TiaAgent");
        }

        public static bool Exists { get { return File.Exists(ConfigPath); } }

        public static AgentConfig Load()
        {
            if (!Exists) throw new FileNotFoundException("Agente non configurato: eseguire setup.cmd come amministratore.", ConfigPath);
            AgentConfig cfg = Parse(File.ReadAllText(ConfigPath, Encoding.UTF8));
            if (string.IsNullOrEmpty(cfg.Token)) throw new InvalidOperationException("Nessun token in " + ConfigPath + ": rieseguire setup.cmd.");
            return cfg;
        }

        public static AgentConfig LoadOrDefault()
        {
            try { return Exists ? Parse(File.ReadAllText(ConfigPath, Encoding.UTF8)) : new AgentConfig(); }
            catch { return new AgentConfig(); }
        }

        static AgentConfig Parse(string text)
        {
            var o = Json.Parse(text) as JObj ?? new JObj();
            var c = new AgentConfig();
            c.Port = o.Int("Port", DefaultPort);
            c.ListenHost = o.Str("ListenHost", "+");
            c.Token = o.Str("Token", "");
            c.AccessMode = o.Str("AccessMode", "read-only");
            c.AllowedClients = o.Strings("AllowedClients");
            c.WorkDir = o.Str("WorkDir", c.WorkDir);
            c.SessionIdleMinutes = o.Int("SessionIdleMinutes", 720);
            return c;
        }

        public void Save()
        {
            Directory.CreateDirectory(DataDir);
            var o = new JObj()
                .Set("Port", Port)
                .Set("ListenHost", ListenHost)
                .Set("Token", Token)
                .Set("AccessMode", AccessMode)
                .Set("AllowedClients", AllowedClients)
                .Set("WorkDir", WorkDir)
                .Set("SessionIdleMinutes", SessionIdleMinutes);
            File.WriteAllText(ConfigPath, Json.Write(o), new UTF8Encoding(false));
        }

        public static string NewToken()
        {
            var bytes = new byte[32];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(bytes);
            return string.Concat(bytes.Select(b => b.ToString("x2")));
        }

        /// <summary>IPv4 di questa macchina, da mostrare nella configurazione del client.</summary>
        public static List<string> LocalAddresses()
        {
            try
            {
                return NetworkInterface.GetAllNetworkInterfaces()
                    .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                    .SelectMany(n => n.GetIPProperties().UnicastAddresses)
                    .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a.Address))
                    .Select(a => a.Address.ToString())
                    .Where(a => !a.StartsWith("169.254."))
                    .Distinct()
                    .ToList();
            }
            catch { return new List<string>(); }
        }

        /// <summary>Server TIA disponibili: sottocartelle Vxx accanto all'agente con TiaMcpServer.exe.</summary>
        public static SortedDictionary<string, string> Servers()
        {
            var result = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            foreach (string dir in Directory.GetDirectories(baseDir, "V*"))
            {
                string exe = Path.Combine(dir, "TiaMcpServer.exe");
                if (File.Exists(exe)) result[Path.GetFileName(dir).ToUpperInvariant()] = exe;
            }
            return result;
        }

        /// <summary>Il blocco da incollare in %USERPROFILE%\.claude.json sul PC con Claude Code: un server per versione.</summary>
        public string ClientSnippet(string address)
        {
            string host = address ?? LocalAddresses().FirstOrDefault() ?? Environment.MachineName;
            var sb = new StringBuilder();
            bool first = true;
            foreach (string v in Servers().Keys)
            {
                if (!first) sb.Append(",\r\n");
                first = false;
                sb.Append("\"tia-" + v.ToLowerInvariant() + "\": {\r\n");
                sb.Append("  \"type\": \"stdio\",\r\n");
                sb.Append("  \"command\": \"C:\\\\percorso\\\\tia-mcp\\\\bin\\\\client\\\\TiaMcpClient.exe\",\r\n");
                sb.Append("  \"args\": [\"--agent\", \"http://" + host + ":" + Port + "\", \"--version\", \"" + v + "\"],\r\n");
                sb.Append("  \"env\": { \"TIA_MCP_AGENT_TOKEN\": \"" + Token + "\" }\r\n");
                sb.Append("}");
            }
            return sb.ToString();
        }
    }
}
