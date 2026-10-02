// FakeServer.cs - finto TiaMcpServer per provare agente e client senza TIA Portal (test\remote.ps1).
// Stesso trasporto del server vero: una riga JSON-RPC per richiesta, una per risposta.
//
//   fake_info                     argomenti del processo, pid e cartella corrente
//   fake_export  {out_dir,names}  scrive un file per nome in out_dir e ne restituisce i percorsi
//   fake_import  {files|dir}      legge i file e ne restituisce percorso e contenuto
//   fake_value   {value}          restituisce il valore ricevuto, cosi com'e

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using TiaMcp;

static class FakeServer
{
    static int Main(string[] args)
    {
        var utf8 = new UTF8Encoding(false);
        var input = new StreamReader(Console.OpenStandardInput(), utf8);
        var output = new StreamWriter(Console.OpenStandardOutput(), utf8);
        bool readOnly = Array.IndexOf(args, "--read-only") >= 0;
        Console.Error.WriteLine("fake server started" + (readOnly ? " (read-only)" : ""));
        string line;
        while ((line = input.ReadLine()) != null)
        {
            JObj m = Json.Parse(line) as JObj;
            if (m == null || !m.Has("id")) continue;
            object result;
            string method = m.Str("method");
            JObj p = m.Obj("params") ?? new JObj();
            if (method == "initialize")
                result = new JObj().Set("protocolVersion", p.Str("protocolVersion", "2025-06-18"))
                                   .Set("capabilities", new JObj().Set("tools", new JObj()))
                                   .Set("serverInfo", new JObj().Set("name", "fake-tia").Set("version", "0"));
            else if (method == "tools/list")
                result = new JObj().Set("tools", new object[] {
                    Tool("fake_info"), Tool("fake_export"), Tool("fake_import"), Tool("fake_value") });
            else if (method == "tools/call")
                result = Call(p.Str("name"), p.Obj("arguments") ?? new JObj(), args, readOnly);
            else
                result = new JObj();
            output.Write(Json.Write(new JObj().Set("jsonrpc", "2.0").Set("id", m.Get("id")).Set("result", result)));
            output.Write('\n');
            output.Flush();
        }
        return 0;
    }

    static JObj Tool(string name)
    {
        return new JObj().Set("name", name).Set("description", "fake").Set("inputSchema", new JObj().Set("type", "object"));
    }

    static JObj Call(string name, JObj a, string[] args, bool readOnly)
    {
        object payload;
        if (name == "fake_info")
            payload = new JObj().Set("args", new List<object>(args)).Set("read_only", readOnly)
                                .Set("pid", System.Diagnostics.Process.GetCurrentProcess().Id)
                                .Set("cwd", Environment.CurrentDirectory);
        else if (name == "fake_export")
        {
            string dir = a.Str("out_dir");
            Directory.CreateDirectory(dir);
            var files = new List<object>();
            foreach (string n in a.Strings("names"))
            {
                string f = Path.Combine(dir, n + ".xml");
                File.WriteAllText(f, "<Screen Name=\"" + n + "\" />");
                files.Add(f);
            }
            payload = new JObj().Set("exported", files.Count).Set("out_dir", dir).Set("files", files);
        }
        else if (name == "fake_import")
        {
            var files = new List<string>(a.Strings("files"));
            string dir = a.Str("dir");
            if (!string.IsNullOrEmpty(dir)) files.AddRange(Directory.GetFiles(dir, a.Str("pattern", "*.xml")));
            var read = new List<object>();
            foreach (string f in files) read.Add(new JObj().Set("path", f).Set("content", File.ReadAllText(f)));
            payload = new JObj().Set("imported", read);
        }
        else if (name == "fake_value")
            payload = new JObj().Set("value", a.Get("value"));
        else
            return new JObj().Set("content", new object[] { new JObj().Set("type", "text").Set("text", "unknown tool " + name) }).Set("isError", true);

        return new JObj().Set("content", new object[] { new JObj().Set("type", "text").Set("text", Json.Write(payload)) }).Set("isError", false);
    }
}
