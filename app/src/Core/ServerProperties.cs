using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace CardboardBox.Core
{
    /// <summary>
    /// Reads / writes Minecraft's server.properties while keeping comments and key order.
    /// Values are stored with Java-style escaping (\uXXXX for non-ASCII such as the \u00a7 color sign).
    /// </summary>
    public class ServerProperties
    {
        private readonly List<string> _lines = new List<string>();
        private readonly Dictionary<string, int> _index = new Dictionary<string, int>(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _values = new Dictionary<string, string>(StringComparer.Ordinal);

        public static string LivePath => Path.Combine(AppPaths.Server, "server.properties");
        public static string RepoPath => Path.Combine(RepoSync.RepoServerFolder, "server.properties");

        public IEnumerable<string> Keys => _values.Keys;

        public static ServerProperties Load(string path)
        {
            var p = new ServerProperties();
            if (!File.Exists(path)) return p;
            foreach (var raw in File.ReadAllLines(path, Encoding.UTF8))
            {
                var line = raw.TrimStart();
                if (line.Length == 0 || line[0] == '#' || line[0] == '!')
                {
                    p._lines.Add(raw);
                    continue;
                }
                int sep = IndexOfSeparator(line);
                var key = Unescape(sep < 0 ? line : line.Substring(0, sep)).Trim();
                var value = sep < 0 ? "" : Unescape(line.Substring(sep + 1).TrimStart());
                if (p._index.ContainsKey(key)) { p._values[key] = value; continue; }
                p._index[key] = p._lines.Count;
                p._values[key] = value;
                p._lines.Add(raw);
            }
            return p;
        }

        public string Get(string key, string def = null) => _values.TryGetValue(key, out var v) ? v : def;

        public int GetInt(string key, int def) =>
            int.TryParse(Get(key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : def;

        public bool GetBool(string key, bool def) =>
            bool.TryParse(Get(key), out var v) ? v : def;

        public void Set(string key, string value)
        {
            value = value ?? "";
            _values[key] = value;
            var line = Escape(key, true) + "=" + Escape(value, false);
            if (_index.TryGetValue(key, out var i)) _lines[i] = line;
            else { _index[key] = _lines.Count; _lines.Add(line); }
        }

        public void Save(string path)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllLines(path, _lines, new UTF8Encoding(false));
        }

        /// <summary>
        /// Builds the live server.properties: the server's current file, then every key from the
        /// GitHub repo's file, then the changes made in the app (overrides) on top.
        /// </summary>
        public static void BuildLive(Settings settings)
        {
            var live = Load(LivePath);
            var repo = Load(RepoPath);
            foreach (var key in repo.Keys) live.Set(key, repo.Get(key));
            foreach (var kv in settings.PropertyOverrides) live.Set(kv.Key, kv.Value);
            live.Save(LivePath);
        }

        /// <summary>The value the server will use: app override, else repo, else live file.</summary>
        public static string Effective(Settings settings, string key, string def = null)
        {
            if (settings.PropertyOverrides.TryGetValue(key, out var o)) return o;
            var repo = Load(RepoPath).Get(key);
            if (repo != null) return repo;
            return Load(LivePath).Get(key, def);
        }

        private static int IndexOfSeparator(string line)
        {
            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];
                if (c == '\\') { i++; continue; }
                if (c == '=' || c == ':' || c == ' ' || c == '\t')
                {
                    // "key = value": skip whitespace around the separator
                    int j = i;
                    while (j < line.Length && (line[j] == ' ' || line[j] == '\t')) j++;
                    if (j < line.Length && (line[j] == '=' || line[j] == ':')) return j;
                    return i;
                }
            }
            return -1;
        }

        public static string Unescape(string s)
        {
            if (s.IndexOf('\\') < 0) return s;
            var sb = new StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c != '\\' || i + 1 >= s.Length) { sb.Append(c); continue; }
                char n = s[++i];
                switch (n)
                {
                    case 'n': sb.Append('\n'); break;
                    case 't': sb.Append('\t'); break;
                    case 'r': sb.Append('\r'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'u':
                        if (i + 4 < s.Length &&
                            int.TryParse(s.Substring(i + 1, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var code))
                        {
                            sb.Append((char)code);
                            i += 4;
                        }
                        else sb.Append('u');
                        break;
                    default: sb.Append(n); break;
                }
            }
            return sb.ToString();
        }

        public static string Escape(string s, bool isKey)
        {
            var sb = new StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                switch (c)
                {
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    case '=':
                    case ':':
                        if (isKey) sb.Append('\\');
                        sb.Append(c);
                        break;
                    case ' ':
                        if (isKey || i == 0) sb.Append('\\');
                        sb.Append(c);
                        break;
                    case '#':
                    case '!':
                        if (i == 0) sb.Append('\\');
                        sb.Append(c);
                        break;
                    default:
                        if (c < 0x20 || c > 0x7e) sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            return sb.ToString();
        }
    }
}
