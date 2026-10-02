// Sessions.cs - un processo TiaMcpServer per sessione del client.
//
// Il server TIA tiene l'aggancio a TIA Portal per tutta la vita del processo: una
// sessione MCP sul PC = un processo qui = una sola conferma di Openness sul desktop
// di questa macchina. Il processo parte nella sessione dell'utente (quella
// dell'agente), che e anche quella dove gira TIA Portal e dove compare la conferma.
//
// Il protocollo del server e una riga JSON per richiesta e una per risposta, senza
// messaggi spontanei: basta scrivere una riga e leggerne una.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using TiaMcp;

namespace TiaAgent
{
    public sealed class Session : IDisposable
    {
        readonly Process process;
        readonly BlockingCollection<string> lines = new BlockingCollection<string>();
        readonly object callLock = new object();
        readonly StreamWriter log;

        public string Id { get; private set; }
        public string Version { get; private set; }
        public string Client { get; private set; }
        public string WorkDir { get; private set; }
        public DateTime LastUsed { get; private set; }
        public DateTime Started { get; private set; }
        /// <summary>Il client manda un ping periodico: se smette (processo chiuso a forza), la sessione si chiude.</summary>
        public bool KeepAlive { get; private set; }
        DateTime lastSeen;
        public DateTime LastSeen { get { lock (seenLock) return lastSeen; } }
        readonly object seenLock = new object();
        public void Touch() { lock (seenLock) lastSeen = DateTime.Now; }
        public bool Exited { get { try { return process.HasExited; } catch { return true; } } }

        public Session(string id, string version, string exe, bool readOnly, string workRoot, string client, bool keepAlive)
        {
            Id = id;
            KeepAlive = keepAlive;
            Version = version;
            Client = client;
            WorkDir = Path.Combine(workRoot, id);
            Directory.CreateDirectory(WorkDir);
            Started = LastUsed = lastSeen = DateTime.Now;

            Directory.CreateDirectory(AgentConfig.LogDir);
            log = new StreamWriter(Path.Combine(AgentConfig.LogDir, "server-" + id + ".log"), true, new UTF8Encoding(false));
            log.AutoFlush = true;

            var psi = new ProcessStartInfo(exe, readOnly ? "--read-only" : "")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = new UTF8Encoding(false),
                StandardErrorEncoding = new UTF8Encoding(false),
                WorkingDirectory = WorkDir,
            };
            process = Process.Start(psi);
            process.StandardInput.AutoFlush = true;

            var reader = new Thread(ReadOutput) { IsBackground = true, Name = "stdout " + id };
            reader.Start();
            process.ErrorDataReceived += delegate(object s, DataReceivedEventArgs e) { if (e.Data != null) Log(e.Data); };
            process.BeginErrorReadLine();
            Log("started " + exe + (readOnly ? " --read-only" : "") + " for " + client + (keepAlive ? ", keepalive" : ""));
        }

        void ReadOutput()
        {
            try
            {
                string line;
                while ((line = process.StandardOutput.ReadLine()) != null) lines.Add(line);
            }
            catch { }
            lines.CompleteAdding();
        }

        void Log(string s)
        {
            try { lock (log) log.WriteLine(DateTime.Now.ToString("HH:mm:ss.fff") + " " + s); } catch { }
        }

        /// <summary>
        /// Inoltra un messaggio JSON-RPC. Restituisce la risposta, oppure null per una notifica.
        /// Le chiamate sono in serie: Openness non e thread-safe e il server risponde in ordine.
        /// </summary>
        public string Call(string message, TimeSpan timeout)
        {
            JObj m = Json.Parse(message) as JObj;
            if (m == null) throw new ArgumentException("Messaggio JSON-RPC non valido.");
            bool notification = !m.Has("id") || m.Get("id") == null;
            string method = m.Str("method", "?");

            Touch();
            lock (callLock)
            {
                LastUsed = DateTime.Now;
                if (Exited) throw new InvalidOperationException("Il server TIA di questa sessione e terminato (exit " + SafeExitCode() + "). Vedi il log server-" + Id + ".log.");
                // Una riga per messaggio: il JSON serializzato non contiene a capo.
                process.StandardInput.WriteLine(Json.Write(m));
                if (notification) return null;

                string reply;
                if (!lines.TryTake(out reply, timeout))
                {
                    if (lines.IsCompleted) throw new InvalidOperationException("Il server TIA e terminato durante '" + method + "'. Vedi il log server-" + Id + ".log.");
                    throw new TimeoutException("Nessuna risposta dal server TIA a '" + method + "' entro " + (int)timeout.TotalMinutes + " minuti.");
                }
                LastUsed = DateTime.Now;
                Touch();
                return reply;
            }
        }

        string SafeExitCode() { try { return process.ExitCode.ToString(); } catch { return "?"; } }

        public void Dispose()
        {
            // stdin chiuso = il server rilascia Openness ed esce; se non lo fa, lo si termina.
            try { process.StandardInput.Close(); } catch { }
            try { if (!process.WaitForExit(10000)) process.Kill(); } catch { }
            Log("stopped");
            try { log.Dispose(); } catch { }
            // I file scambiati sono gia stati scaricati dal client: la cartella della sessione non serve piu.
            try { Directory.Delete(WorkDir, true); } catch { }
        }
    }

    public sealed class SessionManager : IDisposable
    {
        readonly ConcurrentDictionary<string, Session> sessions = new ConcurrentDictionary<string, Session>();
        readonly AgentConfig config;
        readonly Timer reaper;

        public SessionManager(AgentConfig config)
        {
            this.config = config;
            Directory.CreateDirectory(config.WorkDir);
            // controllo frequente: una sessione abbandonata va chiusa entro poco piu di KeepAliveSeconds
            var every = TimeSpan.FromSeconds(Math.Min(30, Math.Max(1, config.KeepAliveSeconds / 3)));
            reaper = new Timer(delegate { Reap(); }, null, every, every);
        }

        public IEnumerable<Session> All { get { return sessions.Values; } }

        public Session Create(string version, string client, bool keepAlive)
        {
            var servers = AgentConfig.Servers();
            string key = (version ?? "").Trim().ToUpperInvariant();
            if (key.Length > 0 && !key.StartsWith("V")) key = "V" + key;
            string exe;
            if (!servers.TryGetValue(key, out exe))
                throw new ArgumentException("TIA Portal " + version + " non disponibile su questa macchina. Disponibili: " +
                                            (servers.Count == 0 ? "nessuno" : string.Join(", ", servers.Keys)));
            string id = Guid.NewGuid().ToString("N").Substring(0, 12);
            var s = new Session(id, key, exe, !config.IsReadWrite, config.WorkDir, client, keepAlive);
            sessions[id] = s;
            AgentLog.Write("session " + id + " " + key + " opened by " + client + (keepAlive ? " (keepalive)" : ""));
            return s;
        }

        public Session Get(string id)
        {
            Session s;
            if (!sessions.TryGetValue(id ?? "", out s)) throw new KeyNotFoundException("Sessione " + id + " inesistente o scaduta.");
            return s;
        }

        public void Close(string id)
        {
            Session s;
            if (sessions.TryRemove(id ?? "", out s))
            {
                s.Dispose();
                AgentLog.Write("session " + id + " closed");
            }
        }

        void Reap()
        {
            foreach (Session s in sessions.Values.ToList())
            {
                string why = null;
                if (s.Exited) why = "server exited";
                else if (s.KeepAlive && DateTime.Now - s.LastSeen > TimeSpan.FromSeconds(config.KeepAliveSeconds)) why = "client gone (no ping)";
                else if (DateTime.Now - s.LastUsed > TimeSpan.FromMinutes(config.SessionIdleMinutes)) why = "idle";
                if (why != null)
                {
                    AgentLog.Write("session " + s.Id + " " + why + ": closing");
                    Close(s.Id);
                }
            }
        }

        public void Dispose()
        {
            reaper.Dispose();
            foreach (string id in sessions.Keys.ToList()) Close(id);
        }
    }
}
