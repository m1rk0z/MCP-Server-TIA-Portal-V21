// Json.cs - lettore e scrittore JSON minimi, senza dipendenze esterne.
//
// Il server gira su .NET Framework 4.8 con il compilatore in-box (C# 5): non c'e
// NuGet e non c'e System.Text.Json. Duecento righe scritte a mano valgono meno
// rischio di una dipendenza da risolvere a mano su una macchina di cantiere.
//
// Rappresentazione: null, bool, double, string, JObj (oggetto, ordine
// conservato) e List<object> (array). L'ordine conta: uno schema di tool con le
// chiavi in ordine casuale e illeggibile nei log.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace TiaMcp
{
    /// <summary>Oggetto JSON che conserva l'ordine di inserimento.</summary>
    public class JObj : IEnumerable<KeyValuePair<string, object>>
    {
        readonly List<KeyValuePair<string, object>> items = new List<KeyValuePair<string, object>>();
        readonly Dictionary<string, int> index = new Dictionary<string, int>(StringComparer.Ordinal);

        public int Count { get { return items.Count; } }

        public JObj Set(string key, object value)
        {
            int at;
            if (index.TryGetValue(key, out at)) items[at] = new KeyValuePair<string, object>(key, value);
            else { index[key] = items.Count; items.Add(new KeyValuePair<string, object>(key, value)); }
            return this;
        }

        public bool Has(string key) { return index.ContainsKey(key); }

        public object Get(string key)
        {
            int at;
            return index.TryGetValue(key, out at) ? items[at].Value : null;
        }

        public JObj Obj(string key) { return Get(key) as JObj; }

        public IEnumerator<KeyValuePair<string, object>> GetEnumerator() { return items.GetEnumerator(); }
        IEnumerator IEnumerable.GetEnumerator() { return items.GetEnumerator(); }

        // --- letture tolleranti: un argomento assente non e un errore ---

        public string Str(string key, string fallback)
        {
            object v = Get(key);
            if (v == null) return fallback;
            string s = v as string;
            return s != null ? s : Convert.ToString(v, CultureInfo.InvariantCulture);
        }

        public string Str(string key) { return Str(key, null); }

        public int Int(string key, int fallback)
        {
            object v = Get(key);
            if (v == null) return fallback;
            if (v is double) return (int)(double)v;
            int n;
            return int.TryParse(Convert.ToString(v, CultureInfo.InvariantCulture),
                                NumberStyles.Integer, CultureInfo.InvariantCulture, out n) ? n : fallback;
        }

        public bool Bool(string key, bool fallback)
        {
            object v = Get(key);
            if (v == null) return fallback;
            if (v is bool) return (bool)v;
            string s = Convert.ToString(v, CultureInfo.InvariantCulture);
            return s == "true" || s == "1";
        }

        /// <summary>Accetta sia un array sia una stringa singola: i client MCP non sono coerenti.</summary>
        public List<string> Strings(string key)
        {
            var result = new List<string>();
            object v = Get(key);
            if (v == null) return result;
            var list = v as List<object>;
            if (list != null)
            {
                foreach (object o in list)
                    if (o != null) result.Add(Convert.ToString(o, CultureInfo.InvariantCulture));
                return result;
            }
            string s = Convert.ToString(v, CultureInfo.InvariantCulture);
            if (s.Length > 0) result.Add(s);
            return result;
        }
    }

    public static class Json
    {
        // ------------------------------------------------------------------ write

        public static string Write(object value)
        {
            var sb = new StringBuilder(256);
            WriteTo(sb, value);
            return sb.ToString();
        }

        static void WriteTo(StringBuilder sb, object v)
        {
            if (v == null) { sb.Append("null"); return; }

            if (v is string) { WriteString(sb, (string)v); return; }
            if (v is bool) { sb.Append(((bool)v) ? "true" : "false"); return; }

            if (v is int || v is long || v is short || v is byte ||
                v is uint || v is ulong || v is ushort || v is sbyte)
            { sb.Append(Convert.ToString(v, CultureInfo.InvariantCulture)); return; }

            if (v is double || v is float || v is decimal)
            {
                double d = Convert.ToDouble(v, CultureInfo.InvariantCulture);
                // JSON non ammette NaN ne infinito: meglio null che un documento illegale
                if (double.IsNaN(d) || double.IsInfinity(d)) { sb.Append("null"); return; }
                sb.Append(d.ToString("R", CultureInfo.InvariantCulture));
                return;
            }

            JObj obj = v as JObj;
            if (obj != null)
            {
                sb.Append('{');
                bool first = true;
                foreach (KeyValuePair<string, object> kv in obj)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    WriteString(sb, kv.Key);
                    sb.Append(':');
                    WriteTo(sb, kv.Value);
                }
                sb.Append('}');
                return;
            }

            IDictionary dict = v as IDictionary;
            if (dict != null)
            {
                sb.Append('{');
                bool first = true;
                foreach (DictionaryEntry e in dict)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    WriteString(sb, Convert.ToString(e.Key, CultureInfo.InvariantCulture));
                    sb.Append(':');
                    WriteTo(sb, e.Value);
                }
                sb.Append('}');
                return;
            }

            IEnumerable seq = v as IEnumerable;
            if (seq != null)
            {
                sb.Append('[');
                bool first = true;
                foreach (object o in seq)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    WriteTo(sb, o);
                }
                sb.Append(']');
                return;
            }

            WriteString(sb, Convert.ToString(v, CultureInfo.InvariantCulture));
        }

        static void WriteString(StringBuilder sb, string s)
        {
            sb.Append('"');
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        // i caratteri di controllo vanno sempre in \u; il resto passa
                        // com'e: il trasporto e UTF-8
                        if (c < ' ') sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }

        // ------------------------------------------------------------------- read

        public static object Parse(string text)
        {
            int pos = 0;
            object v = ParseValue(text, ref pos);
            SkipWs(text, ref pos);
            return v;
        }

        static void SkipWs(string s, ref int i)
        {
            while (i < s.Length && (s[i] == ' ' || s[i] == '\t' || s[i] == '\r' || s[i] == '\n')) i++;
        }

        static object ParseValue(string s, ref int i)
        {
            SkipWs(s, ref i);
            if (i >= s.Length) throw new FormatException("truncated JSON");

            char c = s[i];
            if (c == '{') return ParseObject(s, ref i);
            if (c == '[') return ParseArray(s, ref i);
            if (c == '"') return ParseString(s, ref i);

            if (Match(s, ref i, "true")) return true;
            if (Match(s, ref i, "false")) return false;
            if (Match(s, ref i, "null")) return null;

            return ParseNumber(s, ref i);
        }

        static bool Match(string s, ref int i, string word)
        {
            if (i + word.Length > s.Length) return false;
            if (string.CompareOrdinal(s, i, word, 0, word.Length) != 0) return false;
            i += word.Length;
            return true;
        }

        static JObj ParseObject(string s, ref int i)
        {
            var o = new JObj();
            i++;                                   // {
            SkipWs(s, ref i);
            if (i < s.Length && s[i] == '}') { i++; return o; }
            while (true)
            {
                SkipWs(s, ref i);
                if (i >= s.Length || s[i] != '"') throw new FormatException("expected a JSON key");
                string key = ParseString(s, ref i);
                SkipWs(s, ref i);
                if (i >= s.Length || s[i] != ':') throw new FormatException("expected ':'");
                i++;
                o.Set(key, ParseValue(s, ref i));
                SkipWs(s, ref i);
                if (i >= s.Length) throw new FormatException("unterminated JSON object");
                if (s[i] == ',') { i++; continue; }
                if (s[i] == '}') { i++; return o; }
                throw new FormatException("expected ',' or '}'");
            }
        }

        static List<object> ParseArray(string s, ref int i)
        {
            var a = new List<object>();
            i++;                                   // [
            SkipWs(s, ref i);
            if (i < s.Length && s[i] == ']') { i++; return a; }
            while (true)
            {
                a.Add(ParseValue(s, ref i));
                SkipWs(s, ref i);
                if (i >= s.Length) throw new FormatException("unterminated JSON array");
                if (s[i] == ',') { i++; continue; }
                if (s[i] == ']') { i++; return a; }
                throw new FormatException("expected ',' or ']'");
            }
        }

        static string ParseString(string s, ref int i)
        {
            var sb = new StringBuilder();
            i++;                                   // apertura
            while (i < s.Length)
            {
                char c = s[i++];
                if (c == '"') return sb.ToString();
                if (c != '\\') { sb.Append(c); continue; }
                if (i >= s.Length) break;
                char e = s[i++];
                switch (e)
                {
                    case '"': sb.Append('"'); break;
                    case '\\': sb.Append('\\'); break;
                    case '/': sb.Append('/'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 't': sb.Append('\t'); break;
                    case 'u':
                        if (i + 4 > s.Length) throw new FormatException("truncated \\u escape");
                        sb.Append((char)int.Parse(s.Substring(i, 4), NumberStyles.HexNumber,
                                                  CultureInfo.InvariantCulture));
                        i += 4;
                        break;
                    default: throw new FormatException("unknown escape sequence");
                }
            }
            throw new FormatException("unterminated JSON string");
        }

        static object ParseNumber(string s, ref int i)
        {
            int start = i;
            if (i < s.Length && (s[i] == '-' || s[i] == '+')) i++;
            while (i < s.Length && ((s[i] >= '0' && s[i] <= '9') || s[i] == '.' ||
                                    s[i] == 'e' || s[i] == 'E' || s[i] == '-' || s[i] == '+')) i++;
            string raw = s.Substring(start, i - start);
            double d;
            if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out d))
                throw new FormatException("not a valid JSON number: " + raw);
            return d;
        }
    }
}
