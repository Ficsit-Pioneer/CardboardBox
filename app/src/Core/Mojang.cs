using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;

namespace CardboardBox.Core
{
    /// <summary>Mojang's own version list: release names, server downloads and the Java each version needs.</summary>
    public static class Mojang
    {
        private const string ManifestUrl = "https://piston-meta.mojang.com/mc/game/version_manifest_v2.json";
        private static Dictionary<string, object> _manifest;
        private static DateTime _manifestAt;
        private static readonly Dictionary<string, Dictionary<string, object>> VersionCache = new Dictionary<string, Dictionary<string, object>>();

        private static async Task<Dictionary<string, object>> ManifestAsync()
        {
            if (_manifest == null || (DateTime.UtcNow - _manifestAt).TotalMinutes > 30)
            {
                _manifest = Json.ParseObject(await Net.GetStringAsync(ManifestUrl).ConfigureAwait(false));
                _manifestAt = DateTime.UtcNow;
            }
            return _manifest;
        }

        /// <summary>Full releases (no snapshots), newest first.</summary>
        public static async Task<List<string>> ReleasesAsync()
        {
            var m = await ManifestAsync().ConfigureAwait(false);
            return m.Arr("versions").OfType<Dictionary<string, object>>()
                .Where(v => v.Str("type") == "release").Select(v => v.Str("id")).ToList();
        }

        public static async Task<Dictionary<string, object>> VersionAsync(string id)
        {
            lock (VersionCache) if (VersionCache.TryGetValue(id, out var cached)) return cached;
            var m = await ManifestAsync().ConfigureAwait(false);
            var entry = m.Arr("versions").OfType<Dictionary<string, object>>().FirstOrDefault(v => v.Str("id") == id);
            if (entry == null)
                throw new FriendlyException("Minecraft " + id + " doesn't exist.", "Pick a version from the list in Server Settings.");
            var json = Json.ParseObject(await Net.GetStringAsync(entry.Str("url")).ConfigureAwait(false));
            lock (VersionCache) VersionCache[id] = json;
            return json;
        }

        /// <summary>The Java major version Mojang says this Minecraft version needs (e.g. 25 for 26.x).</summary>
        public static async Task<int> JavaMajorAsync(string id)
        {
            try
            {
                var v = await VersionAsync(id).ConfigureAwait(false);
                var major = v.Obj("javaVersion").Int("majorVersion", 0);
                if (major > 0) return major;
            }
            catch (Exception ex) when (!(ex is FriendlyException))
            {
                Log.WriteFileOnly("WARN couldn't read Java version for " + id + ": " + ex.Message);
            }
            return VersionOrder.Compare(id, "26.1") >= 0 ? 25 : VersionOrder.Compare(id, "1.20.5") >= 0 ? 21 : 17;
        }
    }

    /// <summary>Compares Minecraft version names: 1.21.11 &lt; 26.1 &lt; 26.2 &lt; 26.10.</summary>
    public static class VersionOrder
    {
        public static int Compare(string a, string b)
        {
            var pa = Parts(a);
            var pb = Parts(b);
            for (int i = 0; i < Math.Max(pa.Count, pb.Count); i++)
            {
                int x = i < pa.Count ? pa[i] : 0, y = i < pb.Count ? pb[i] : 0;
                if (x != y) return x.CompareTo(y);
            }
            return 0;
        }

        /// <summary>A plain release name like 26.2 or 1.21.11 (no snapshots, pre-releases or release candidates).</summary>
        public static bool IsRelease(string v) =>
            !string.IsNullOrEmpty(v) && v.Split('.').All(p => p.Length > 0 && p.All(char.IsDigit));

        public static List<string> NewestFirst(IEnumerable<string> versions) =>
            versions.Where(IsRelease).Distinct().OrderByDescending(v => v, Comparer<string>.Create(Compare)).ToList();

        private static List<int> Parts(string v)
        {
            var list = new List<int>();
            foreach (var p in (v ?? "").Split('.', '-', ' '))
            {
                if (int.TryParse(p, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)) list.Add(n);
                else break;
            }
            return list;
        }
    }
}
