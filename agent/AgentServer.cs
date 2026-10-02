// AgentServer.cs - endpoint HTTP dell'agente. Tutte le richieste: "Authorization: Bearer <token>".
//
//   GET    /api/health                               macchina, versioni TIA disponibili, istanze aperte, sessioni
//   POST   /api/sessions            {"version":"V21"}  apre una sessione (un processo TiaMcpServer) -> {"session","work_dir"}
//   POST   /api/sessions/{id}/rpc   <messaggio JSON-RPC> -> la risposta del server TIA (204 per una notifica)
//   POST   /api/sessions/{id}/ping                    il client e vivo (con "keepalive":true all'apertura,
//                                                     senza ping per 3 minuti la sessione si chiude)
//   DELETE /api/sessions/{id}                         chiude la sessione: il server rilascia Openness
//   PUT    /api/sessions/{id}/upload?name=<rel>       carica un file nella cartella della sessione -> {"path"}
//   GET    /api/sessions/{id}/list?dir=<path>         file sotto una cartella della sessione (percorsi relativi)
//   GET    /api/sessions/{id}/file?path=<path>        scarica un file della sessione
//
// I file sono confinati nella cartella di lavoro della sessione: niente al di fuori.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TiaMcp;

namespace TiaAgent
{
    public sealed class AgentServer : IDisposable
    {
        const long MaxUploadBytes = 50L * 1024 * 1024;
        const long MaxRpcBytes = 8L * 1024 * 1024;
        static readonly TimeSpan CallTimeout = TimeSpan.FromMinutes(30);

        readonly AgentConfig config;
        readonly HttpListener listener = new HttpListener();
        readonly byte[] token;
        readonly SessionManager sessions;
        volatile bool running;

        public AgentServer(AgentConfig config)
        {
            this.config = config;
            token = Encoding.UTF8.GetBytes(config.Token);
            sessions = new SessionManager(config);
        }

        public string Prefix { get; private set; }
        public SessionManager Sessions { get { return sessions; } }

        public void Start()
        {
            Prefix = "http://" + (string.IsNullOrEmpty(config.ListenHost) ? "+" : config.ListenHost) + ":" + config.Port + "/";
            listener.Prefixes.Add(Prefix);
            try { listener.Start(); }
            catch (HttpListenerException ex)
            {
                if (ex.ErrorCode == 5)
                    throw new InvalidOperationException("Accesso negato sulla porta " + config.Port + ": manca la prenotazione URL. Eseguire setup.cmd come amministratore.", ex);
                if (ex.ErrorCode == 32 || ex.ErrorCode == 183)
                    throw new InvalidOperationException("La porta " + config.Port + " e gia usata da un altro programma.", ex);
                throw;
            }
            running = true;
            var t = new Thread(AcceptLoop) { IsBackground = true, Name = "http accept" };
            t.Start();
            AgentLog.Write("listening on " + Prefix + " - access mode " + config.AccessMode + ", servers: " +
                           string.Join(", ", AgentConfig.Servers().Keys));
        }

        void AcceptLoop()
        {
            while (running)
            {
                HttpListenerContext ctx;
                try { ctx = listener.GetContext(); }
                catch { if (!running) return; continue; }
                ThreadPool.QueueUserWorkItem(delegate { Handle(ctx); });
            }
        }

        void Handle(HttpListenerContext ctx)
        {
            var sw = Stopwatch.StartNew();
            IPAddress remote = ctx.Request.RemoteEndPoint == null ? null : ctx.Request.RemoteEndPoint.Address;
            string path = ctx.Request.Url.AbsolutePath;
            string method = ctx.Request.HttpMethod;
            string what = path;
            string outcome = "?";
            bool quiet = false;
            try
            {
                if (config.AllowedClients.Count > 0 && (remote == null || !config.AllowedClients.Contains(Normalize(remote))))
                {
                    outcome = "forbidden";
                    Reply(ctx, 403, Fail("Client non ammesso."));
                    return;
                }
                if (!Authorized(ctx.Request))
                {
                    outcome = "unauthorized";
                    Thread.Sleep(500); // rallenta i tentativi
                    Reply(ctx, 401, Fail("Token mancante o errato."));
                    return;
                }

                string[] parts = path.Trim('/').Split('/');
                // parts: api, sessions, {id}, {action}
                if (method == "GET" && path == "/api/health")
                {
                    Reply(ctx, 200, Ok(Health()));
                    outcome = "ok";
                }
                else if (method == "POST" && path == "/api/sessions")
                {
                    JObj body = ReadJson(ctx.Request, 64 * 1024);
                    Session s = sessions.Create(body.Str("version"), Normalize(remote), body.Bool("keepalive", false));
                    what += " " + s.Version + " -> " + s.Id;
                    Reply(ctx, 200, Ok(new JObj().Set("session", s.Id).Set("version", s.Version).Set("work_dir", s.WorkDir)
                                                  .Set("access_mode", config.AccessMode)));
                    outcome = "ok";
                }
                else if (parts.Length >= 3 && parts[0] == "api" && parts[1] == "sessions")
                {
                    Session s = sessions.Get(parts[2]);
                    string action = parts.Length > 3 ? parts[3] : "";
                    if (method == "POST" && action == "ping")
                    {
                        s.Touch();
                        Reply(ctx, 200, Ok(new JObj().Set("session", s.Id)));
                        outcome = "ok";
                        quiet = true; // il ping arriva ogni minuto: non va nel log
                        return;
                    }
                    else if (method == "DELETE" && action == "")
                    {
                        sessions.Close(s.Id);
                        Reply(ctx, 200, Ok(new JObj().Set("closed", s.Id)));
                    }
                    else if (method == "POST" && action == "rpc")
                    {
                        string message = ReadText(ctx.Request, MaxRpcBytes);
                        JObj m = Json.Parse(message) as JObj;
                        what += " " + (m == null ? "?" : m.Str("method", "?"));
                        if (m != null && m.Str("method") == "tools/call" && m.Obj("params") != null) what += " " + m.Obj("params").Str("name", "");
                        string reply = s.Call(message, CallTimeout);
                        if (reply == null) Reply(ctx, 204, null);
                        else ReplyRaw(ctx, 200, reply);
                    }
                    else if (method == "PUT" && action == "upload")
                    {
                        string target = Confine(s.WorkDir, Path.Combine("in", ctx.Request.QueryString["name"] ?? "upload.dat"));
                        Directory.CreateDirectory(Path.GetDirectoryName(target));
                        if (ctx.Request.ContentLength64 > MaxUploadBytes) throw new InvalidOperationException("File troppo grande.");
                        using (var fs = File.Create(target)) Copy(ctx.Request.InputStream, fs, MaxUploadBytes);
                        what += " " + target;
                        Reply(ctx, 200, Ok(new JObj().Set("path", target)));
                    }
                    else if (method == "GET" && action == "list")
                    {
                        string dir = Confine(s.WorkDir, ctx.Request.QueryString["dir"] ?? "");
                        var files = new List<object>();
                        if (Directory.Exists(dir))
                            foreach (string f in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
                                files.Add(new JObj().Set("relative", f.Substring(dir.TrimEnd('\\').Length + 1)).Set("bytes", new FileInfo(f).Length));
                        Reply(ctx, 200, Ok(new JObj().Set("dir", dir).Set("files", files)));
                    }
                    else if (method == "GET" && action == "file")
                    {
                        string file = Confine(s.WorkDir, ctx.Request.QueryString["path"] ?? "");
                        what += " " + file;
                        if (!File.Exists(file)) { Reply(ctx, 404, Fail("File non trovato.")); outcome = "not found"; return; }
                        ctx.Response.ContentType = "application/octet-stream";
                        using (var fs = File.OpenRead(file))
                        {
                            ctx.Response.ContentLength64 = fs.Length;
                            fs.CopyTo(ctx.Response.OutputStream);
                        }
                        ctx.Response.Close();
                    }
                    else { Reply(ctx, 404, Fail("Endpoint sconosciuto.")); outcome = "not found"; return; }
                    outcome = "ok";
                }
                else { Reply(ctx, 404, Fail("Endpoint sconosciuto.")); outcome = "not found"; }
            }
            catch (KeyNotFoundException ex) { outcome = ex.Message; TryReply(ctx, 410, Fail(ex.Message)); }
            catch (UnauthorizedAccessException ex) { outcome = ex.Message; TryReply(ctx, 403, Fail(ex.Message)); }
            catch (Exception ex) { outcome = "failed: " + ex.Message; TryReply(ctx, 400, Fail(ex.Message)); }
            finally
            {
                if (!quiet) AgentLog.Write(Normalize(remote) + " " + method + " " + what + " -> " + outcome + " (" + sw.ElapsedMilliseconds + " ms)");
            }
        }

        JObj Health()
        {
            var tia = new List<object>();
            foreach (Process p in Process.GetProcessesByName("Siemens.Automation.Portal"))
            {
                string version = "?";
                try { version = p.MainModule.FileVersionInfo.FileMajorPart.ToString(); } catch { }
                tia.Add(new JObj().Set("pid", p.Id).Set("version", "V" + version).Set("window", SafeTitle(p)));
            }
            var open = new List<object>();
            foreach (Session s in sessions.All)
                open.Add(new JObj().Set("session", s.Id).Set("version", s.Version).Set("client", s.Client)
                                   .Set("started", s.Started.ToString("s")).Set("last_used", s.LastUsed.ToString("s")));
            return new JObj()
                .Set("component", "TiaAgent")
                .Set("version", typeof(AgentServer).Assembly.GetName().Version.ToString())
                .Set("machine", Environment.MachineName)
                .Set("user", Environment.UserDomainName + "\\" + Environment.UserName)
                .Set("os", Environment.OSVersion.VersionString)
                .Set("access_mode", config.AccessMode)
                .Set("servers", AgentConfig.Servers().Keys.ToList())
                .Set("tia_instances", tia)
                .Set("sessions", open)
                .Set("allowed_clients", config.AllowedClients)
                .Set("time", DateTime.Now.ToString("s"));
        }

        static string SafeTitle(Process p) { try { return p.MainWindowTitle; } catch { return ""; } }

        /// <summary>Un percorso relativo (o assoluto) che deve restare dentro la cartella della sessione.</summary>
        static string Confine(string root, string path)
        {
            string r = Path.GetFullPath(root).TrimEnd('\\') + "\\";
            string full = Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(r, path));
            if (!full.StartsWith(r, StringComparison.OrdinalIgnoreCase) && !string.Equals(full + "\\", r, StringComparison.OrdinalIgnoreCase))
                throw new UnauthorizedAccessException("Percorso fuori dalla cartella della sessione.");
            return full;
        }

        static string Normalize(IPAddress a)
        {
            if (a == null) return "?";
            return (a.IsIPv4MappedToIPv6 ? a.MapToIPv4() : a).ToString();
        }

        bool Authorized(HttpListenerRequest request)
        {
            string header = request.Headers["Authorization"] ?? "";
            if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return false;
            byte[] given = Encoding.UTF8.GetBytes(header.Substring(7).Trim());
            int diff = given.Length ^ token.Length; // confronto a tempo costante
            for (int i = 0; i < Math.Min(given.Length, token.Length); i++) diff |= given[i] ^ token[i];
            return diff == 0;
        }

        static string ReadText(HttpListenerRequest request, long max)
        {
            if (request.ContentLength64 > max) throw new InvalidOperationException("Richiesta troppo grande.");
            using (var ms = new MemoryStream())
            {
                Copy(request.InputStream, ms, max);
                return Encoding.UTF8.GetString(ms.ToArray());
            }
        }

        static JObj ReadJson(HttpListenerRequest request, long max)
        {
            string text = ReadText(request, max);
            if (text.Trim().Length == 0) return new JObj();
            return Json.Parse(text) as JObj ?? new JObj();
        }

        static void Copy(Stream from, Stream to, long max)
        {
            var buffer = new byte[81920];
            long total = 0;
            int n;
            while ((n = from.Read(buffer, 0, buffer.Length)) > 0)
            {
                total += n;
                if (total > max) throw new InvalidOperationException("Richiesta troppo grande.");
                to.Write(buffer, 0, n);
            }
        }

        static JObj Ok(JObj result) { return new JObj().Set("success", true).Set("result", result); }
        static JObj Fail(string error) { return new JObj().Set("success", false).Set("error", error); }

        static void Reply(HttpListenerContext ctx, int status, JObj body)
        {
            ReplyRaw(ctx, status, body == null ? null : Json.Write(body));
        }

        static void ReplyRaw(HttpListenerContext ctx, int status, string text)
        {
            ctx.Response.StatusCode = status;
            if (text != null)
            {
                byte[] bytes = Encoding.UTF8.GetBytes(text);
                ctx.Response.ContentType = "application/json; charset=utf-8";
                ctx.Response.ContentLength64 = bytes.Length;
                ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
            }
            ctx.Response.Close();
        }

        static void TryReply(HttpListenerContext ctx, int status, JObj body)
        {
            try { Reply(ctx, status, body); } catch { }
        }

        public void Dispose()
        {
            running = false;
            try { listener.Stop(); listener.Close(); } catch { }
            sessions.Dispose();
        }
    }
}
