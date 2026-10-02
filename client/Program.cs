// TiaMcpClient - server MCP (stdio) sul PC di Claude Code che inoltra tutto a TiaAgent,
// sulla macchina dove gira TIA Portal (tipicamente una VM).
//
//   TiaMcpClient.exe --agent http://<vm>:8766 --version V21 [--agent-token <t>] [--workdir <dir>] [--timeout <min>]
//   token anche da TIA_MCP_AGENT_TOKEN (meglio: nella sezione "env" di .claude.json)
//
// Una sessione MCP di Claude Code = una sessione sull'agente = un processo TiaMcpServer
// sulla VM = un solo Attach() a TIA Portal, confermato una volta sul desktop della VM.
//
// I tool che leggono o scrivono file lavorano sulla VM. Il client li rende trasparenti:
//   files / dir   un percorso che esiste su questo PC viene caricato sulla VM e sostituito
//   out_dir       l'export avviene nella cartella della sessione sulla VM, poi i file sono
//                 scaricati nella cartella indicata su questo PC e i percorsi riscritti
// Un percorso che non esiste su questo PC e lasciato com'e: e un percorso della VM.
//
// Su stdout va SOLO protocollo; la diagnostica va su stderr.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using TiaMcp;

namespace TiaMcpClient
{
    static class Program
    {
        static string agentUrl, token, version, localRoot;
        static TimeSpan timeout = TimeSpan.FromMinutes(30);
        static volatile string sessionId;
        static string sessionWorkDir;
        static int counter;
        // TIA_MCP_PING_SECONDS solo per le prove: l'agente chiude una sessione dopo 3 minuti senza ping
        static readonly TimeSpan PingInterval = TimeSpan.FromSeconds(PingSeconds());
        static System.Threading.Timer pinger;

        static int Main(string[] args)
        {
            try { ParseArgs(args); }
            catch (ArgumentException ex)
            {
                Console.Error.WriteLine(ex.Message);
                Console.Error.WriteLine("Uso: TiaMcpClient --agent http://<host>:8766 --version V19|V21 [--agent-token <t>] [--workdir <dir>] [--timeout <min>]");
                return 2;
            }
            ServicePointManager.Expect100Continue = false;
            // una chiamata lunga (compilazione) e il ping viaggiano insieme
            ServicePointManager.DefaultConnectionLimit = 8;

            var utf8 = new UTF8Encoding(false);
            var input = new StreamReader(Console.OpenStandardInput(), utf8);
            var output = new StreamWriter(Console.OpenStandardOutput(), utf8) { AutoFlush = false };
            Log("tia-mcp client -> " + agentUrl + " " + version + ", file locali in " + localRoot);

            while (true)
            {
                string line;
                try { line = input.ReadLine(); } catch (IOException) { break; }
                if (line == null) break;
                if (line.Trim().Length == 0) continue;

                string reply = Handle(line);
                if (reply == null) continue;
                output.Write(reply);
                output.Write('\n');
                output.Flush();
            }

            CloseSession();
            return 0;
        }

        static void ParseArgs(string[] args)
        {
            token = Environment.GetEnvironmentVariable("TIA_MCP_AGENT_TOKEN");
            agentUrl = Environment.GetEnvironmentVariable("TIA_MCP_AGENT_URL");
            version = Environment.GetEnvironmentVariable("TIA_MCP_VERSION");
            string workdir = null;
            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i].ToLowerInvariant();
                Func<string> next = delegate
                {
                    if (i + 1 >= args.Length) throw new ArgumentException("Manca il valore di " + args[i]);
                    return args[++i];
                };
                if (a == "--agent") agentUrl = next();
                else if (a == "--agent-token") token = next();
                else if (a == "--version") version = next();
                else if (a == "--workdir") workdir = next();
                else if (a == "--timeout") timeout = TimeSpan.FromMinutes(int.Parse(next()));
                else throw new ArgumentException("Opzione sconosciuta: " + args[i]);
            }
            if (string.IsNullOrEmpty(agentUrl)) throw new ArgumentException("--agent e obbligatorio (es. http://192.168.56.10:8766).");
            if (!agentUrl.Contains("://")) agentUrl = "http://" + agentUrl;
            Uri uri;
            if (!Uri.TryCreate(agentUrl, UriKind.Absolute, out uri) || uri.Scheme != "http")
                throw new ArgumentException("URL dell'agente non valido: " + agentUrl + " (atteso http://host:porta)");
            if (uri.IsDefaultPort && !agentUrl.TrimEnd('/').EndsWith(":80")) agentUrl = "http://" + uri.Host + ":8766";
            agentUrl = agentUrl.TrimEnd('/');
            if (string.IsNullOrEmpty(token)) throw new ArgumentException("Token mancante: --agent-token oppure TIA_MCP_AGENT_TOKEN.");
            if (string.IsNullOrEmpty(version)) throw new ArgumentException("--version e obbligatorio (V19, V21, ...).");
            version = version.Trim().ToUpperInvariant();
            if (!version.StartsWith("V")) version = "V" + version;
            uri = new Uri(agentUrl);
            localRoot = workdir ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                                                "tia-mcp", "remote", uri.Host + "_" + uri.Port, version);
        }

        // ------------------------------------------------------------------ messaggi

        static string Handle(string line)
        {
            JObj msg;
            try { msg = Json.Parse(line) as JObj; }
            catch (Exception ex) { return Error(null, -32700, "JSON non valido: " + ex.Message); }
            if (msg == null) return Error(null, -32600, "Atteso un oggetto JSON.");
            object id = msg.Get("id");
            bool notification = !msg.Has("id") || id == null;

            try
            {
                EnsureSession();
                var mappings = new List<KeyValuePair<string, string>>();   // percorso VM -> percorso locale
                var downloads = new List<KeyValuePair<string, string>>();  // cartella VM da scaricare -> cartella locale

                if (msg.Str("method") == "tools/call")
                {
                    JObj p = msg.Obj("params");
                    JObj arguments = p == null ? null : p.Obj("arguments");
                    if (arguments != null) PrepareFiles(arguments, mappings, downloads);
                }

                string reply = Rpc(Json.Write(msg));
                if (reply == null) return null;

                foreach (KeyValuePair<string, string> d in downloads) Download(d.Key, d.Value);
                return mappings.Count == 0 ? reply : Rewrite(reply, mappings);
            }
            catch (Exception ex)
            {
                Log("errore: " + ex.Message);
                if (notification) return null;
                // Su tools/call un errore e un risultato da mostrare al modello, non un guasto del protocollo.
                if (msg.Str("method") == "tools/call")
                    return Json.Write(new JObj().Set("jsonrpc", "2.0").Set("id", id).Set("result",
                        new JObj().Set("content", new object[] { new JObj().Set("type", "text").Set("text", ex.Message) }).Set("isError", true)));
                return Error(id, -32000, ex.Message);
            }
        }

        static void PrepareFiles(JObj a, List<KeyValuePair<string, string>> mappings, List<KeyValuePair<string, string>> downloads)
        {
            if (a.Has("files"))
            {
                var vm = new List<object>();
                foreach (string f in a.Strings("files")) vm.Add(UploadPath(f, a.Str("pattern", "*"), mappings));
                a.Set("files", vm);
            }
            if (a.Has("dir"))
            {
                string d = a.Str("dir");
                if (!string.IsNullOrEmpty(d)) a.Set("dir", UploadPath(d, a.Str("pattern", "*.xml"), mappings));
            }
            if (a.Has("out_dir"))
            {
                string local = a.Str("out_dir");
                if (!string.IsNullOrEmpty(local))
                {
                    local = Path.GetFullPath(Path.IsPathRooted(local) ? local : Path.Combine(localRoot, local));
                    Directory.CreateDirectory(local);
                    string vmOut = sessionWorkDir.TrimEnd('\\') + "\\out\\o" + (++counter);
                    a.Set("out_dir", vmOut);
                    downloads.Add(new KeyValuePair<string, string>(vmOut, local));
                    mappings.Add(new KeyValuePair<string, string>(vmOut, local));
                }
            }
        }

        /// <summary>Carica sulla VM un file o una cartella di questo PC; un percorso che qui non esiste resta com'e.</summary>
        static string UploadPath(string path, string pattern, List<KeyValuePair<string, string>> mappings)
        {
            string prefix = "u" + (++counter);
            if (File.Exists(path))
            {
                string vm = Upload(path, prefix + "/" + Path.GetFileName(path));
                mappings.Add(new KeyValuePair<string, string>(vm, Path.GetFullPath(path)));
                return vm;
            }
            if (Directory.Exists(path))
            {
                string root = Path.GetFullPath(path).TrimEnd('\\');
                string vmDir = null;
                foreach (string f in Directory.GetFiles(root, string.IsNullOrEmpty(pattern) ? "*" : pattern, SearchOption.TopDirectoryOnly))
                {
                    string vm = Upload(f, prefix + "/" + Path.GetFileName(f));
                    vmDir = Path.GetDirectoryName(vm);
                }
                if (vmDir == null) throw new InvalidOperationException("Nessun file '" + pattern + "' in " + root);
                mappings.Add(new KeyValuePair<string, string>(vmDir, root));
                return vmDir;
            }
            return path;
        }

        /// <summary>Riporta nei testi della risposta i percorsi della VM a quelli di questo PC.</summary>
        static string Rewrite(string reply, List<KeyValuePair<string, string>> mappings)
        {
            JObj r = Json.Parse(reply) as JObj;
            JObj result = r == null ? null : r.Obj("result");
            var content = result == null ? null : result.Get("content") as List<object>;
            if (content == null) return reply;
            foreach (object c in content)
            {
                JObj block = c as JObj;
                if (block == null || block.Str("type") != "text") continue;
                string text = block.Str("text") ?? "";
                foreach (KeyValuePair<string, string> m in mappings.OrderByDescending(x => x.Key.Length))
                {
                    // nel testo JSON del risultato le barre sono raddoppiate
                    text = Replace(text, Escape(m.Key), Escape(m.Value));
                    text = Replace(text, m.Key, m.Value);
                }
                block.Set("text", text);
            }
            return Json.Write(r);
        }

        static string Escape(string s) { return s.Replace("\\", "\\\\"); }

        static string Replace(string text, string from, string to)
        {
            int at = 0;
            var sb = new StringBuilder();
            while (true)
            {
                int i = text.IndexOf(from, at, StringComparison.OrdinalIgnoreCase);
                if (i < 0) { sb.Append(text, at, text.Length - at); return sb.ToString(); }
                sb.Append(text, at, i - at).Append(to);
                at = i + from.Length;
            }
        }

        // ------------------------------------------------------------------ agente

        static void EnsureSession()
        {
            if (sessionId != null) return;
            // keepalive: se questo processo viene chiuso a forza (senza EOF su stdin), i ping si
            // fermano e l'agente chiude la sessione da solo invece di tenerla aperta per ore.
            JObj r = Envelope(Send("POST", "/api/sessions", Encoding.UTF8.GetBytes(Json.Write(
                new JObj().Set("version", version).Set("keepalive", true))), "application/json"));
            sessionWorkDir = r.Str("work_dir");
            sessionId = r.Str("session");
            Log("sessione " + sessionId + " (" + r.Str("version") + ", " + r.Str("access_mode") + ") aperta su " + agentUrl);
            if (pinger == null) pinger = new System.Threading.Timer(delegate { Ping(); }, null, PingInterval, PingInterval);
        }

        static int PingSeconds()
        {
            int s;
            return int.TryParse(Environment.GetEnvironmentVariable("TIA_MCP_PING_SECONDS"), out s) && s > 0 ? s : 60;
        }

        static void Ping()
        {
            string id = sessionId;
            if (id == null) return;
            try { Send("POST", "/api/sessions/" + id + "/ping", new byte[0], "application/json", TimeSpan.FromSeconds(20)); }
            catch (AgentException ex) { if (ex.Status == 410 && sessionId == id) sessionId = null; Log("ping: " + ex.Message); }
            catch (Exception ex) { Log("ping: " + ex.Message); }
        }

        static string Rpc(string message)
        {
            try
            {
                return SendText("POST", "/api/sessions/" + sessionId + "/rpc", message);
            }
            catch (AgentException ex)
            {
                // Sessione scaduta o agente riavviato: se ne apre una nuova al messaggio successivo.
                if (ex.Status == 410) sessionId = null;
                throw;
            }
        }

        static void CloseSession()
        {
            if (pinger != null) pinger.Dispose();
            if (sessionId == null) return;
            try { Send("DELETE", "/api/sessions/" + sessionId, null, null); Log("sessione " + sessionId + " chiusa"); }
            catch (Exception ex) { Log("chiusura sessione: " + ex.Message); }
        }

        static string Upload(string localFile, string name)
        {
            byte[] data = File.ReadAllBytes(localFile);
            JObj r = Envelope(Send("PUT", "/api/sessions/" + sessionId + "/upload?name=" + Uri.EscapeDataString(name.Replace('\\', '/')), data, "application/octet-stream"));
            return r.Str("path");
        }

        static void Download(string vmDir, string localDir)
        {
            JObj r = Envelope(Send("GET", "/api/sessions/" + sessionId + "/list?dir=" + Uri.EscapeDataString(vmDir), null, null));
            var files = r.Get("files") as List<object> ?? new List<object>();
            string root = Path.GetFullPath(localDir).TrimEnd('\\') + "\\";
            foreach (object o in files)
            {
                string rel = ((JObj)o).Str("relative");
                string target = Path.GetFullPath(Path.Combine(root, rel));
                if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase)) continue; // mai fuori dalla cartella indicata
                Directory.CreateDirectory(Path.GetDirectoryName(target));
                File.WriteAllBytes(target, Send("GET", "/api/sessions/" + sessionId + "/file?path=" + Uri.EscapeDataString(vmDir.TrimEnd('\\') + "\\" + rel), null, null));
            }
            Log(files.Count + " file scaricati in " + localDir);
        }

        sealed class AgentException : Exception
        {
            public int Status { get; private set; }
            public AgentException(int status, string message) : base(message) { Status = status; }
        }

        static JObj Envelope(byte[] body)
        {
            JObj e = Json.Parse(Encoding.UTF8.GetString(body)) as JObj;
            if (e == null || !e.Bool("success", false)) throw new InvalidOperationException(e == null ? "Risposta non valida dall'agente." : e.Str("error", "errore dell'agente"));
            return e.Obj("result") ?? new JObj();
        }

        static string SendText(string method, string path, string text)
        {
            byte[] body = Send(method, path, Encoding.UTF8.GetBytes(text), "application/json");
            return body.Length == 0 ? null : Encoding.UTF8.GetString(body);
        }

        static byte[] Send(string method, string path, byte[] body, string contentType)
        {
            return Send(method, path, body, contentType, timeout + TimeSpan.FromMinutes(1));
        }

        static byte[] Send(string method, string path, byte[] body, string contentType, TimeSpan wait)
        {
            var req = (HttpWebRequest)WebRequest.Create(agentUrl + path);
            req.Method = method;
            req.Timeout = req.ReadWriteTimeout = (int)Math.Min(int.MaxValue, wait.TotalMilliseconds);
            req.Headers["Authorization"] = "Bearer " + token;
            req.Proxy = null;
            if (body != null)
            {
                req.ContentType = contentType;
                req.ContentLength = body.Length;
                using (Stream s = req.GetRequestStream()) s.Write(body, 0, body.Length);
            }
            try
            {
                using (var resp = (HttpWebResponse)req.GetResponse())
                    return ReadAll(resp);
            }
            catch (WebException ex)
            {
                var resp = ex.Response as HttpWebResponse;
                if (resp == null)
                    throw new InvalidOperationException("Agente TIA non raggiungibile su " + agentUrl + " (" + ex.Message + "). " +
                        "Verificare che la VM sia accesa, che l'utente abbia fatto l'accesso (l'agente parte con la sessione) e che la porta sia aperta.");
                using (resp)
                {
                    int status = (int)resp.StatusCode;
                    string text = Encoding.UTF8.GetString(ReadAll(resp));
                    string message = text;
                    try { JObj e = Json.Parse(text) as JObj; if (e != null && e.Has("error")) message = e.Str("error"); } catch { }
                    if (status == 401) message = "L'agente TIA ha rifiutato il token: controllare TIA_MCP_AGENT_TOKEN.";
                    if (status == 403 && message.Length == 0) message = "L'agente TIA non accetta richieste da questo PC.";
                    throw new AgentException(status, message);
                }
            }
        }

        static byte[] ReadAll(HttpWebResponse resp)
        {
            using (Stream s = resp.GetResponseStream())
            using (var ms = new MemoryStream())
            {
                if (s != null) s.CopyTo(ms);
                return ms.ToArray();
            }
        }

        static string Error(object id, int code, string message)
        {
            return Json.Write(new JObj().Set("jsonrpc", "2.0").Set("id", id)
                .Set("error", new JObj().Set("code", code).Set("message", message)));
        }

        static void Log(string s)
        {
            Console.Error.WriteLine("[" + DateTime.Now.ToString("HH:mm:ss") + "] " + s);
        }
    }
}
