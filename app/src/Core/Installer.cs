using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;

namespace CardboardBox.Core
{
    public class PaperRelease
    {
        public string Version;
        public int Build;
        public string Url;
        public string Sha256;
        public string FileName;
        public bool Experimental;
        public int JavaMinimum;
        public List<string> JavaFlags = new List<string>();
    }

    /// <summary>Downloads and installs everything the server needs: Java, Paper and plugins.</summary>
    public static class Installer
    {
        private const string Fill = "https://fill.papermc.io/v3/projects/paper";
        private const string Adoptium = "https://api.adoptium.net/v3/assets/latest/";
        private const string Modrinth = "https://api.modrinth.com/v2/";

        // ------------------------------------------------------------------ Paper

        public static async Task<PaperRelease> ResolvePaperAsync(Manifest m)
        {
            var version = (m.MinecraftVersion ?? "").Trim();
            if (version.Length == 0 || version.Equals("latest", StringComparison.OrdinalIgnoreCase))
                version = await ResolveLatestVersionAsync().ConfigureAwait(false);

            var vr = await Net.SendAsync(HttpMethod.Get, Fill + "/versions/" + Uri.EscapeDataString(version)).ConfigureAwait(false);
            if (vr.Status == HttpStatusCode.NotFound)
                throw new FriendlyException("Paper doesn't have Minecraft version \"" + version + "\".",
                    "Ask the server owner to fix \"minecraftVersion\" in cardboardbox.json.");
            if (!vr.Ok) throw new FriendlyException("PaperMC answered with error " + (int)vr.Status + ".");

            var vinfo = Json.ParseObject(vr.Body).Obj("version");
            var java = vinfo.Obj("java");
            var rel = new PaperRelease
            {
                Version = version,
                JavaMinimum = java.Obj("version").Int("minimum", 21),
            };
            foreach (var f in java.Obj("flags").Arr("recommended")) if (f != null) rel.JavaFlags.Add(f.ToString());

            var builds = Json.ParseArray(await Net.GetStringAsync(Fill + "/versions/" + Uri.EscapeDataString(version) + "/builds").ConfigureAwait(false))
                .OfType<Dictionary<string, object>>().ToList();
            if (builds.Count == 0) throw new FriendlyException("Paper has no downloads for Minecraft " + version + " yet.");

            Dictionary<string, object> pick;
            if (m.PaperBuild > 0)
            {
                pick = builds.FirstOrDefault(b => b.Int("id") == m.PaperBuild);
                if (pick == null) throw new FriendlyException("Paper build " + m.PaperBuild + " for " + version + " doesn't exist.");
            }
            else
            {
                pick = builds.FirstOrDefault(b => string.Equals(b.Str("channel"), "STABLE", StringComparison.OrdinalIgnoreCase));
                if (pick == null) { pick = builds[0]; rel.Experimental = true; }
            }

            var dl = pick.Obj("downloads").Obj("server:default");
            rel.Build = pick.Int("id");
            rel.Url = dl.Str("url");
            rel.FileName = dl.Str("name");
            rel.Sha256 = dl.Obj("checksums").Str("sha256");
            if (string.IsNullOrEmpty(rel.Url)) throw new FriendlyException("Paper's download information was incomplete.");
            return rel;
        }

        private static async Task<string> ResolveLatestVersionAsync()
        {
            var d = Json.ParseObject(await Net.GetStringAsync(Fill).ConfigureAwait(false));
            var all = new List<string>();
            var groups = d.Obj("versions");
            if (groups != null)
                foreach (var g in groups.Values)
                    all.AddRange(Json.AsList(g).Select(x => x?.ToString()).Where(x => x != null && !x.Contains("-")));

            foreach (var v in all.OrderByDescending(VersionKey).Take(3))
            {
                var builds = Json.ParseArray(await Net.GetStringAsync(Fill + "/versions/" + v + "/builds").ConfigureAwait(false));
                if (builds.OfType<Dictionary<string, object>>().Any(b => b.Str("channel") == "STABLE")) return v;
            }
            throw new FriendlyException("Couldn't work out the newest Minecraft version from PaperMC.");
        }

        private static Version VersionKey(string v)
        {
            var parts = v.Split('.').Select(p => int.TryParse(p, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : 0).ToList();
            while (parts.Count < 3) parts.Add(0);
            return new Version(parts[0], parts[1], parts[2]);
        }

        public static async Task EnsurePaperAsync(PaperRelease rel, AppState state, ProgressHandler progress)
        {
            if (File.Exists(AppPaths.ServerJar) && state.PaperSha256 == rel.Sha256 &&
                string.Equals(Hash.FileSha256(AppPaths.ServerJar), rel.Sha256, StringComparison.OrdinalIgnoreCase))
                return;

            Log.Info("Downloading Paper " + rel.Version + " build " + rel.Build + (rel.Experimental ? " (experimental build)" : "") + "...");
            var tmp = Path.Combine(AppPaths.Downloads, rel.FileName ?? "paper.jar");
            await Net.DownloadFileAsync(rel.Url, tmp, "Paper " + rel.Version, progress, sha256: rel.Sha256).ConfigureAwait(false);
            File.Copy(tmp, AppPaths.ServerJar, true);
            File.Delete(tmp);

            state.PaperVersion = rel.Version;
            state.PaperBuild = rel.Build;
            state.PaperSha256 = rel.Sha256;
            Log.Success("Installed Paper " + rel.Version + " build " + rel.Build + ".");
        }

        // ------------------------------------------------------------------ Java

        /// <summary>Picks the Java LTS release that satisfies Paper's minimum.</summary>
        public static int PickJavaFeature(int minimum)
        {
            foreach (var lts in new[] { 8, 11, 17, 21, 25, 29, 33 })
                if (lts >= minimum) return lts;
            return minimum;
        }

        public static string JavaExe(int feature) => Path.Combine(AppPaths.Runtime, "java-" + feature, "bin", "java.exe");

        public static async Task<string> EnsureJavaAsync(int feature, ProgressHandler progress)
        {
            var exe = JavaExe(feature);
            if (File.Exists(exe)) return exe;
            if (!Environment.Is64BitOperatingSystem)
                throw new FriendlyException("Minecraft servers need 64-bit Windows.", "This PC is running 32-bit Windows.");

            Log.Info("Downloading Java " + feature + " (only needed once)...");
            Dictionary<string, object> pkg = null;
            foreach (var image in new[] { "jre", "jdk" })
            {
                var url = Adoptium + feature + "/hotspot?architecture=x64&image_type=" + image + "&os=windows&vendor=eclipse";
                var r = await Net.SendAsync(HttpMethod.Get, url).ConfigureAwait(false);
                if (!r.Ok) continue;
                var first = Json.ParseArray(r.Body).OfType<Dictionary<string, object>>().FirstOrDefault();
                pkg = first.Obj("binary").Obj("package");
                if (pkg != null) break;
            }
            if (pkg == null) throw new FriendlyException("Couldn't find a Java " + feature + " download for Windows.");

            var zip = Path.Combine(AppPaths.Downloads, pkg.Str("name") ?? ("java-" + feature + ".zip"));
            await Net.DownloadFileAsync(pkg.Str("link"), zip, "Java " + feature, progress, sha256: pkg.Str("checksum")).ConfigureAwait(false);

            var unpack = Path.Combine(AppPaths.Runtime, "java-" + feature + "-unpack");
            var target = Path.Combine(AppPaths.Runtime, "java-" + feature);
            try
            {
                FileUtil.DeleteDirectory(unpack);
                ZipFile.ExtractToDirectory(zip, unpack);
                var javaExe = Directory.GetFiles(unpack, "java.exe", SearchOption.AllDirectories)
                    .FirstOrDefault(f => string.Equals(Path.GetFileName(Path.GetDirectoryName(f)), "bin", StringComparison.OrdinalIgnoreCase));
                if (javaExe == null) throw new FriendlyException("The Java download didn't contain java.exe.");
                var home = Path.GetDirectoryName(Path.GetDirectoryName(javaExe));
                FileUtil.DeleteDirectory(target);
                Directory.Move(home, target);
            }
            finally
            {
                FileUtil.DeleteDirectory(unpack);
                try { File.Delete(zip); } catch { }
            }
            Log.Success("Installed Java " + feature + ".");
            return exe;
        }

        // ------------------------------------------------------------------ Plugins

        /// <summary>Downloads the plugins listed in cardboardbox.json and removes ones that were taken off the list.</summary>
        public static async Task EnsurePluginsAsync(Manifest m, string mcVersion, AppState state, ProgressHandler progress)
        {
            var wanted = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var spec in m.Plugins)
            {
                var key = spec.Key;
                state.ManagedPlugins.TryGetValue(key, out var oldFile);
                try
                {
                    string url, fileName, sha512 = null, sha1 = null, sha256 = spec.Sha256;
                    if (!string.IsNullOrWhiteSpace(spec.Modrinth))
                    {
                        var v = await ResolveModrinthAsync(spec.Modrinth.Trim(), mcVersion).ConfigureAwait(false);
                        url = v.Item1; fileName = v.Item2; sha512 = v.Item3; sha1 = v.Item4;
                    }
                    else
                    {
                        url = spec.Url.Trim();
                        fileName = spec.File;
                        if (string.IsNullOrWhiteSpace(fileName)) fileName = Uri.UnescapeDataString(new Uri(url).Segments.Last());
                        if (!fileName.EndsWith(".jar", StringComparison.OrdinalIgnoreCase)) fileName += ".jar";
                    }
                    fileName = SafeFileName(fileName);
                    var dest = Path.Combine(AppPaths.Plugins, fileName);

                    if (oldFile == fileName && (File.Exists(dest) || File.Exists(dest + ".disabled")))
                    {
                        wanted[key] = fileName;
                        continue;
                    }

                    await Net.DownloadFileAsync(url, dest, "plugin " + spec.DisplayName, progress, sha256, sha512, sha1).ConfigureAwait(false);
                    if (oldFile != null && oldFile != fileName) DeletePluginFile(oldFile);
                    wanted[key] = fileName;
                    Log.Success("Installed plugin " + fileName + ".");
                }
                catch (Exception ex)
                {
                    // One broken plugin must never stop the whole server.
                    Log.Warn("Plugin \"" + spec.DisplayName + "\" couldn't be installed: " + Errors.Friendly(ex));
                    if (oldFile != null) wanted[key] = oldFile;
                }
            }

            foreach (var kv in state.ManagedPlugins)
            {
                if (wanted.ContainsKey(kv.Key)) continue;
                DeletePluginFile(kv.Value);
                Log.Info("Removed plugin " + kv.Value + " (no longer listed in cardboardbox.json).");
            }
            state.ManagedPlugins = wanted;
        }

        private static async Task<Tuple<string, string, string, string>> ResolveModrinthAsync(string slug, string mcVersion)
        {
            var loaders = Uri.EscapeDataString("[\"paper\",\"spigot\",\"bukkit\"]");
            var games = Uri.EscapeDataString("[\"" + mcVersion + "\"]");
            var url = Modrinth + "project/" + Uri.EscapeDataString(slug) + "/version?loaders=" + loaders + "&game_versions=" + games;
            var r = await Net.SendAsync(HttpMethod.Get, url).ConfigureAwait(false);
            if (r.Status == HttpStatusCode.NotFound)
                throw new FriendlyException("\"" + slug + "\" wasn't found on Modrinth.");
            if (!r.Ok) throw new FriendlyException("Modrinth answered with error " + (int)r.Status + ".");

            var versions = Json.ParseArray(r.Body).OfType<Dictionary<string, object>>().ToList();
            var v = versions.FirstOrDefault(x => x.Str("version_type") == "release") ?? versions.FirstOrDefault();
            if (v == null)
                throw new FriendlyException("\"" + slug + "\" has no version for Minecraft " + mcVersion + " yet.");

            var files = v.Arr("files").OfType<Dictionary<string, object>>().ToList();
            var file = files.FirstOrDefault(f => f.Bool("primary")) ?? files.FirstOrDefault();
            if (file == null) throw new FriendlyException("\"" + slug + "\" has no downloadable file.");
            var hashes = file.Obj("hashes");
            return Tuple.Create(file.Str("url"), file.Str("filename"), hashes.Str("sha512"), hashes.Str("sha1"));
        }

        private static void DeletePluginFile(string fileName)
        {
            foreach (var p in new[] { Path.Combine(AppPaths.Plugins, fileName), Path.Combine(AppPaths.Plugins, fileName + ".disabled") })
            {
                try { if (File.Exists(p)) File.Delete(p); }
                catch (Exception ex) { Log.Warn("Couldn't delete old plugin " + fileName + ": " + ex.Message); }
            }
        }

        private static string SafeFileName(string name)
        {
            name = Path.GetFileName(name ?? "plugin.jar");
            foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
            return name.Length == 0 ? "plugin.jar" : name;
        }
    }
}
