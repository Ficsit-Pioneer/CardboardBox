using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;

namespace CardboardBox.Core
{
    /// <summary>
    /// Tiny JSON helper on top of the JSON parser built into Windows' .NET Framework.
    /// Objects come back as Dictionary&lt;string, object&gt;, arrays as object[] / ArrayList.
    /// </summary>
    public static class Json
    {
        public static object Parse(string text)
        {
            var s = new JavaScriptSerializer { MaxJsonLength = int.MaxValue, RecursionLimit = 256 };
            return s.DeserializeObject(text);
        }

        public static Dictionary<string, object> ParseObject(string text)
        {
            var o = Parse(text) as Dictionary<string, object>;
            if (o == null) throw new FormatException("Expected a JSON object.");
            return o;
        }

        public static List<object> ParseArray(string text) => AsList(Parse(text));

        public static Dictionary<string, object> ReadFile(string path)
        {
            if (!File.Exists(path)) return null;
            return ParseObject(File.ReadAllText(path, Encoding.UTF8));
        }

        /// <summary>Writes JSON atomically (temp file + replace) so a crash never leaves a half-written file.</summary>
        public static void WriteFile(string path, object value)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, Write(value, true), new UTF8Encoding(false));
            if (File.Exists(path)) File.Replace(tmp, path, null);
            else File.Move(tmp, path);
        }

        public static string Write(object value, bool indent = false)
        {
            var sb = new StringBuilder();
            WriteValue(sb, value, indent, 0);
            return sb.ToString();
        }

        // ---------- typed accessors (never throw on missing / wrong types) ----------

        public static string Str(this IDictionary<string, object> d, string key, string def = null)
        {
            if (d == null || !d.TryGetValue(key, out var v) || v == null) return def;
            return Convert.ToString(v, CultureInfo.InvariantCulture);
        }

        public static int Int(this IDictionary<string, object> d, string key, int def = 0)
        {
            if (d == null || !d.TryGetValue(key, out var v) || v == null) return def;
            try { return Convert.ToInt32(v, CultureInfo.InvariantCulture); } catch { return def; }
        }

        public static long Long(this IDictionary<string, object> d, string key, long def = 0)
        {
            if (d == null || !d.TryGetValue(key, out var v) || v == null) return def;
            try { return Convert.ToInt64(v, CultureInfo.InvariantCulture); } catch { return def; }
        }

        public static bool Bool(this IDictionary<string, object> d, string key, bool def = false)
        {
            if (d == null || !d.TryGetValue(key, out var v) || v == null) return def;
            if (v is bool b) return b;
            return bool.TryParse(Convert.ToString(v, CultureInfo.InvariantCulture), out var p) ? p : def;
        }

        public static Dictionary<string, object> Obj(this IDictionary<string, object> d, string key)
        {
            if (d == null || !d.TryGetValue(key, out var v)) return null;
            return v as Dictionary<string, object>;
        }

        public static List<object> Arr(this IDictionary<string, object> d, string key)
        {
            if (d == null || !d.TryGetValue(key, out var v)) return new List<object>();
            return AsList(v);
        }

        public static Dictionary<string, string> StrMap(this IDictionary<string, object> d, string key)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var o = d.Obj(key);
            if (o != null)
                foreach (var kv in o)
                    if (kv.Value != null) result[kv.Key] = Convert.ToString(kv.Value, CultureInfo.InvariantCulture);
            return result;
        }

        public static List<object> AsList(object v)
        {
            var list = new List<object>();
            if (v is IEnumerable e && !(v is string) && !(v is IDictionary))
                foreach (var item in e) list.Add(item);
            return list;
        }

        // ---------- writer ----------

        private static void WriteValue(StringBuilder sb, object v, bool indent, int depth)
        {
            switch (v)
            {
                case null: sb.Append("null"); return;
                case string s: WriteString(sb, s); return;
                case bool b: sb.Append(b ? "true" : "false"); return;
                case DateTime dt: WriteString(sb, dt.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture)); return;
                case float _:
                case double _:
                case decimal _:
                    sb.Append(Convert.ToString(v, CultureInfo.InvariantCulture)); return;
                case int _:
                case long _:
                case short _:
                case byte _:
                case uint _:
                case ulong _:
                    sb.Append(Convert.ToString(v, CultureInfo.InvariantCulture)); return;
                case IDictionary dict:
                    {
                        sb.Append('{');
                        bool first = true;
                        foreach (DictionaryEntry kv in dict)
                        {
                            if (!first) sb.Append(',');
                            first = false;
                            NewLine(sb, indent, depth + 1);
                            WriteString(sb, Convert.ToString(kv.Key, CultureInfo.InvariantCulture));
                            sb.Append(indent ? ": " : ":");
                            WriteValue(sb, kv.Value, indent, depth + 1);
                        }
                        if (!first) NewLine(sb, indent, depth);
                        sb.Append('}');
                        return;
                    }
                case IEnumerable list:
                    {
                        sb.Append('[');
                        bool first = true;
                        foreach (var item in list)
                        {
                            if (!first) sb.Append(',');
                            first = false;
                            NewLine(sb, indent, depth + 1);
                            WriteValue(sb, item, indent, depth + 1);
                        }
                        if (!first) NewLine(sb, indent, depth);
                        sb.Append(']');
                        return;
                    }
                default:
                    WriteString(sb, Convert.ToString(v, CultureInfo.InvariantCulture)); return;
            }
        }

        private static void NewLine(StringBuilder sb, bool indent, int depth)
        {
            if (!indent) return;
            sb.Append('\n');
            sb.Append(' ', depth * 2);
        }

        private static void WriteString(StringBuilder sb, string s)
        {
            sb.Append('"');
            foreach (var c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }
    }
}
