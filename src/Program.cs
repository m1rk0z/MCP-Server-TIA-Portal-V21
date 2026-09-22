// Program.cs - il trasporto: JSON-RPC 2.0 su stdio, una riga per messaggio.
//
// Openness non e thread-safe e pretende un thread STA. Qui non c'e nessuna
// concorrenza: si legge una richiesta, la si esegue, si risponde. Per un motore
// di engineering e la semantica giusta, non un limite - due import in parallelo
// sullo stesso progetto non vogliono dire niente.
//
// Su stdout va SOLO protocollo. Qualunque diagnostica va su stderr: una riga di
// troppo su stdout e un client MCP che si disconnette senza spiegazioni.

using System;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;

namespace TiaMcp
{
    /// <summary>Errore atteso, da riferire al chiamante cosi com'e: non e un guasto del server.</summary>
    public class McpError : Exception
    {
        public McpError(string message) : base(message) { }
    }

    public static class Program
    {
        public const string ServerName = "tia-portal-openness";
        public const string ServerVersion = "1.0.0";

        // Versioni di protocollo MCP che sappiamo parlare, dalla piu recente.
        static readonly string[] Supported = { "2025-06-18", "2025-03-26", "2024-11-05" };

        static TiaSession session;
        static Tools tools;

        [STAThread]
        static int Main(string[] args)
        {
            bool readOnly = false;
            foreach (string a in args)
            {
                if (a == "--read-only" || a == "-r") readOnly = true;
                else if (a == "--version" || a == "-v") { Console.WriteLine(ServerName + " " + ServerVersion); return 0; }
                else if (a == "--help" || a == "-h") { Help(); return 0; }
                else { Console.Error.WriteLine("Unknown option: " + a); Help(); return 2; }
            }
            if (Environment.GetEnvironmentVariable("TIA_MCP_READONLY") == "1") readOnly = true;

            // Deve stare prima di qualunque riferimento a Siemens.Engineering: il JIT
            // risolve l'assembly quando compila il metodo che la nomina, non quando
            // la riga viene eseguita. Per questo Run() e a parte e non inlinabile.
            Openness.Install();

            return Run(readOnly);
        }

        static void Help()
        {
            Console.Error.WriteLine(ServerName + " " + ServerVersion + " - MCP server for TIA Portal Openness");
            Console.Error.WriteLine();
            Console.Error.WriteLine("  --read-only   refuse every tool that would modify the project");
            Console.Error.WriteLine("  --version     print the version");
            Console.Error.WriteLine();
            Console.Error.WriteLine("Speaks MCP over stdin/stdout: start it from an MCP client, not by hand.");
            Console.Error.WriteLine("Environment variables:");
            Console.Error.WriteLine("  TIA_OPENNESS_PATH   folders holding the Openness assemblies, ';'-separated");
            Console.Error.WriteLine("  TIA_MCP_READONLY=1  same as --read-only");
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static int Run(bool readOnly)
        {
            var utf8 = new UTF8Encoding(false);
            Console.OutputEncoding = utf8;
            var input = new StreamReader(Console.OpenStandardInput(), utf8);
            var output = new StreamWriter(Console.OpenStandardOutput(), utf8);
            output.AutoFlush = false;

            session = new TiaSession();
            try
            {
                tools = new Tools(session, readOnly);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("Cannot load the Openness assemblies: " + ex.Message);
                Console.Error.WriteLine("Looked in:");
                foreach (string d in Openness.SearchPath) Console.Error.WriteLine("  " + d);
                Console.Error.WriteLine("Set TIA_OPENNESS_PATH if TIA Portal is installed elsewhere.");
                return 1;
            }

            Log(ServerName + " " + ServerVersion + (readOnly ? " (read-only)" : "") + " listening on stdio");

            while (true)
            {
                string line;
                try { line = input.ReadLine(); }
                catch (IOException) { break; }
                if (line == null) break;                     // stdin chiuso: il client se n'e andato
                if (line.Trim().Length == 0) continue;

                string reply = Handle(line);
                if (reply == null) continue;                 // era una notifica: non si risponde

                output.Write(reply);
                output.Write('\n');
                output.Flush();
            }

            Log("stdin closed, releasing the Openness session");
            session.Dispose();

            // Openness lascia dietro di se thread in primo piano, e il CLR non
            // termina finche ne vive uno: dopo un aggancio fallito il processo
            // resta appeso per sempre con stdin gia chiuso. Per un server stdio
            // "il client se n'e andato" vuol dire uscire, e basta.
            Console.Out.Flush();
            Environment.Exit(0);
            return 0;
        }

        static void Log(string s)
        {
            Console.Error.WriteLine("[" + DateTime.Now.ToString("HH:mm:ss") + "] " + s);
        }

        // -------------------------------------------------------------- dispatch

        static string Handle(string line)
        {
            JObj request;
            try { request = Json.Parse(line) as JObj; }
            catch (Exception ex) { return Error(null, -32700, "Malformed JSON: " + ex.Message); }
            if (request == null) return Error(null, -32600, "Invalid request: a JSON object was expected.");

            object id = request.Get("id");
            string method = request.Str("method");
            JObj p = request.Obj("params");
            if (p == null) p = new JObj();

            // Una notifica non ha id e non vuole risposta. notifications/initialized
            // arriva sempre: rispondere sarebbe un errore di protocollo.
            bool isNotification = !request.Has("id") || id == null;

            try
            {
                object result;
                switch (method)
                {
                    case "initialize": result = Initialize(p); break;
                    case "ping": result = new JObj(); break;
                    case "tools/list": result = ListTools(); break;
                    case "tools/call": result = CallTool(p); break;

                    case "notifications/initialized":
                    case "notifications/cancelled":
                    case "notifications/roots/list_changed":
                        return null;

                    case "shutdown":
                        session.Dispose();
                        result = new JObj();
                        break;

                    default:
                        if (isNotification) return null;
                        return Error(id, -32601, "Method not handled: " + method);
                }

                if (isNotification) return null;
                return Ok(id, result);
            }
            catch (McpError ex)
            {
                if (isNotification) return null;
                return Error(id, -32602, ex.Message);
            }
            catch (Exception ex)
            {
                Log("ERROR on " + method + ": " + ex.GetType().Name + ": " + ex.Message);
                if (isNotification) return null;
                return Error(id, -32603, ex.GetType().Name + ": " + Flat(ex));
            }
        }

        static object Initialize(JObj p)
        {
            string asked = p.Str("protocolVersion", Supported[0]);
            string agreed = Supported[0];
            foreach (string v in Supported) if (v == asked) agreed = asked;

            return new JObj()
                .Set("protocolVersion", agreed)
                .Set("capabilities", new JObj().Set("tools", new JObj().Set("listChanged", false)))
                .Set("serverInfo", new JObj().Set("name", ServerName).Set("version", ServerVersion))
                .Set("instructions",
                     "MCP server for TIA Portal Openness. It works with both HMI families - WinCC " +
                     "classic (Comfort, Advanced, Professional) and WinCC Unified - which have quite " +
                     "different object models, so start with hmi_panels to learn which one you are " +
                     "dealing with.\n\n" +
                     "Nothing works without a session: call tia_instances to see what is running, then " +
                     "tia_attach. Attaching costs ONE manual confirmation from whoever is sitting at " +
                     "TIA Portal, and then holds for the whole conversation - do not detach and " +
                     "re-attach between operations, each round trip costs that person another click.\n\n" +
                     "When importing screens, list the hand-built ones in keep_safe. Openness cannot " +
                     "regenerate alarm views or trend views, and an Override import deletes them.\n\n" +
                     "Writes are never implicit: changes live in memory until you call tia_save.");
        }

        static object ListTools()
        {
            return new JObj().Set("tools", tools.Descriptors());
        }

        static object CallTool(JObj p)
        {
            string name = p.Str("name");
            if (string.IsNullOrEmpty(name)) throw new McpError("Missing tool name.");
            JObj args = p.Obj("arguments");

            object payload;
            try
            {
                payload = tools.Call(name, args);
            }
            catch (McpError ex)
            {
                // Un errore d'uso e un risultato, non un guasto del protocollo: il
                // modello lo legge e corregge il tiro da solo.
                return Content(ex.Message, true);
            }
            catch (Exception ex)
            {
                Log("ERROR in " + name + ": " + ex.GetType().Name + ": " + ex.Message);
                return Content(ex.GetType().Name + ": " + Flat(ex), true);
            }

            return Content(Json.Write(payload), false);
        }

        static JObj Content(string text, bool isError)
        {
            var one = new JObj().Set("type", "text").Set("text", text);
            return new JObj()
                .Set("content", new object[] { one })
                .Set("isError", isError);
        }

        // -------------------------------------------------------------- risposte

        static string Ok(object id, object result)
        {
            return Json.Write(new JObj().Set("jsonrpc", "2.0").Set("id", id).Set("result", result));
        }

        static string Error(object id, int code, string message)
        {
            return Json.Write(new JObj()
                .Set("jsonrpc", "2.0")
                .Set("id", id)
                .Set("error", new JObj().Set("code", code).Set("message", message)));
        }

        static string Flat(Exception ex)
        {
            var sb = new StringBuilder();
            while (ex != null)
            {
                sb.Append(ex.Message.Replace("\r\n", " | ").Replace("\n", " | "));
                ex = ex.InnerException;
                if (ex != null) sb.Append("  <<  ");
            }
            string s = sb.ToString();
            return s.Length > 1500 ? s.Substring(0, 1500) + " [...]" : s;
        }
    }
}
