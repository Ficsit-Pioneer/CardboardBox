using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;

namespace CardboardBox.Core
{
    /// <summary>A plugin / mod file we resolved and can download.</summary>
    public class AddonFile
    {
        public string Name;
        public string Url;
        public string FileName;
        public string Sha256, Sha512, Sha1;
        /// <summary>Modrinth project ids this file needs (required dependencies).</summary>
        public List<string> RequiredProjects = new List<string>();
    }

    /// <summary>Downloads and installs Java and add-ons (plugins / mods, including Geyser for crossplay).</summary>
    public static class Installer
    {
        private const string Adoptium = "https://api.adoptium.net/v3/assets/latest/";
        private const string Modrinth = "https://api.modrinth.com/v2/";
        private const string GeyserApi = "https://download.geysermc.org/v2/projects/";

        // ------------------------------------------------------------------ Java

        /// <summary>Picks the Java LTS release that satisfies a minimum.</summary>
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

        // ------------------------------------------------------------------ add-ons (plugins / mods)

        /// <summary>
        /// Makes the add-on folder match the list: downloads what's missing or updated (plus required
        /// dependencies) and removes files CardboardBox installed earlier that are no longer wanted.
        /// state.ManagedPlugins maps a key ("modrinth:slug", "geyser:geyser"...) to "folder/file.jar".
        /// </summary>
        public static async Task EnsureAddonsAsync(ServerSoftware sw, string mcVersion, List<PluginSpec> specs, AppState state, ProgressHandler progress)
        {
            var wanted = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var old = state.ManagedPlugins.ToDictionary(kv => kv.Key, kv => NormalizePath(kv.Value), StringComparer.OrdinalIgnoreCase);

            if (sw.AddonFolder == null)
            {
                if (specs.Count > 0) Log.Warn(sw.Name + " servers can't use plugins or mods, so the " + specs.Count + " listed in cardboardbox.json were skipped.");
            }
            else
            {
                var queue = new Queue<PluginSpec>(specs);
                var seenProjects = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                while (queue.Count > 0)
                {
                    var spec = queue.Dequeue();
                    // Keyed per server type: a Fabric mod must never be kept for NeoForge just because
                    // NeoForge has no version of it (they share the "mods" folder).
                    var key = sw.Id + "|" + spec.Key;
                    if (wanted.ContainsKey(key)) continue;
                    old.TryGetValue(key, out var oldPath);
                    try
                    {
                        var file = await ResolveAsync(spec, sw, mcVersion).ConfigureAwait(false);
                        var rel = sw.AddonFolder + "/" + SafeFileName(file.FileName);
                        // Same file already wanted under another name (e.g. a dependency listed twice)
                        if (wanted.Values.Contains(rel, StringComparer.OrdinalIgnoreCase)) continue;

                        var dest = Path.Combine(AppPaths.Server, rel.Replace('/', '\\'));
                        if (!(oldPath == rel && (File.Exists(dest) || File.Exists(dest + ".disabled"))))
                        {
                            await Net.DownloadFileAsync(file.Url, dest, spec.DisplayName, progress, file.Sha256, file.Sha512, file.Sha1).ConfigureAwait(false);
                            if (oldPath != null && oldPath != rel) DeleteAddon(oldPath);
                            Log.Success("Installed " + (spec.IsDependency ? "required " : "") + file.FileName + ".");
                        }
                        wanted[key] = rel;

                        foreach (var dep in file.RequiredProjects)
                            if (seenProjects.Add(dep) && !specs.Any(s => string.Equals(s.Modrinth, dep, StringComparison.OrdinalIgnoreCase)))
                                queue.Enqueue(new PluginSpec { Modrinth = dep, IsDependency = true });
                    }
                    catch (Exception ex)
                    {
                        // One broken add-on must never stop the whole server.
                        Log.Warn("\"" + spec.DisplayName + "\" couldn't be installed: " + Errors.Friendly(ex));
                        if (oldPath != null && oldPath.StartsWith(sw.AddonFolder + "/")) wanted[key] = oldPath;
                    }
                }
            }

            foreach (var kv in old)
            {
                if (wanted.TryGetValue(kv.Key, out var keep) && keep == kv.Value) continue;
                if (wanted.Values.Contains(kv.Value, StringComparer.OrdinalIgnoreCase)) continue;
                DeleteAddon(kv.Value);
                Log.Info("Removed " + Path.GetFileName(kv.Value) + " (no longer needed).");
            }
            state.ManagedPlugins = wanted;
        }

        private static Task<AddonFile> ResolveAsync(PluginSpec spec, ServerSoftware sw, string mcVersion)
        {
            if (!string.IsNullOrWhiteSpace(spec.Geyser)) return ResolveGeyserAsync(spec.Geyser, spec.Geyser == "floodgate" ? sw.FloodgatePlatform : sw.GeyserPlatform);
            if (!string.IsNullOrWhiteSpace(spec.Modrinth)) return ResolveModrinthAsync(spec.Modrinth.Trim(), sw, mcVersion);

            var url = spec.Url.Trim();
            var name = spec.File;
            if (string.IsNullOrWhiteSpace(name)) name = Uri.UnescapeDataString(new Uri(url).Segments.Last());
            if (!name.EndsWith(".jar", StringComparison.OrdinalIgnoreCase)) name += ".jar";
            return Task.FromResult(new AddonFile { Name = name, Url = url, FileName = name, Sha256 = spec.Sha256 });
        }

        /// <summary>Picks the newest release of a Modrinth project that works with this server type and Minecraft version.</summary>
        public static async Task<AddonFile> ResolveModrinthAsync(string slugOrId, ServerSoftware sw, string mcVersion)
        {
            var loaders = Uri.EscapeDataString("[" + string.Join(",", sw.ModrinthLoaders.Select(l => "\"" + l + "\"")) + "]");
            var games = Uri.EscapeDataString("[\"" + mcVersion + "\"]");
            var url = Modrinth + "project/" + Uri.EscapeDataString(slugOrId) + "/version?loaders=" + loaders + "&game_versions=" + games;
            var r = await Net.SendAsync(HttpMethod.Get, url).ConfigureAwait(false);
            if (r.Status == HttpStatusCode.NotFound) throw new FriendlyException("\"" + slugOrId + "\" wasn't found on Modrinth.");
            if (!r.Ok) throw new FriendlyException("Modrinth answered with error " + (int)r.Status + ".");

            var versions = Json.ParseArray(r.Body).OfType<Dictionary<string, object>>().ToList();
            var v = versions.FirstOrDefault(x => x.Str("version_type") == "release") ?? versions.FirstOrDefault();
            if (v == null)
                throw new FriendlyException("\"" + slugOrId + "\" has no " + sw.Name + " version for Minecraft " + mcVersion + " yet.");

            var files = v.Arr("files").OfType<Dictionary<string, object>>().ToList();
            var file = files.FirstOrDefault(f => f.Bool("primary")) ?? files.FirstOrDefault();
            if (file == null) throw new FriendlyException("\"" + slugOrId + "\" has no downloadable file.");
            var hashes = file.Obj("hashes");
            var result = new AddonFile
            {
                Name = v.Str("name") ?? slugOrId,
                Url = file.Str("url"),
                FileName = file.Str("filename"),
                Sha512 = hashes.Str("sha512"),
                Sha1 = hashes.Str("sha1"),
            };
            foreach (var dep in v.Arr("dependencies").OfType<Dictionary<string, object>>())
                if (dep.Str("dependency_type") == "required" && !string.IsNullOrEmpty(dep.Str("project_id")))
                    result.RequiredProjects.Add(dep.Str("project_id"));
            return result;
        }

        /// <summary>Geyser / Floodgate (crossplay) from GeyserMC's download service.</summary>
        private static async Task<AddonFile> ResolveGeyserAsync(string project, string platform)
        {
            if (platform == null) throw new FriendlyException("Crossplay isn't available for this server type.");
            var buildUrl = GeyserApi + project + "/versions/latest/builds/latest";
            var b = Json.ParseObject(await Net.GetStringAsync(buildUrl).ConfigureAwait(false));
            var dl = b.Obj("downloads").Obj(platform);
            if (dl == null) throw new FriendlyException(project + " has no download for " + platform + ".");
            return new AddonFile
            {
                Name = project,
                Url = buildUrl + "/downloads/" + platform,
                FileName = dl.Str("name") ?? (project + "-" + platform + ".jar"),
                Sha256 = dl.Str("sha256"),
            };
        }

        private static string NormalizePath(string stored) =>
            stored.Contains("/") ? stored : "plugins/" + stored; // CardboardBox 1.0/1.1 stored plain plugin names

        private static void DeleteAddon(string rel)
        {
            var path = Path.Combine(AppPaths.Server, rel.Replace('/', '\\'));
            foreach (var p in new[] { path, path + ".disabled" })
            {
                try { if (File.Exists(p)) File.Delete(p); }
                catch (Exception ex) { Log.Warn("Couldn't delete old " + Path.GetFileName(rel) + ": " + ex.Message); }
            }
        }

        private static string SafeFileName(string name)
        {
            name = Path.GetFileName(name ?? "addon.jar");
            foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
            return name.Length == 0 ? "addon.jar" : name;
        }
    }
}
