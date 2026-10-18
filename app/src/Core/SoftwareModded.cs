using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace CardboardBox.Core
{
    // ====================================================================== Purpur

    /// <summary>Purpur: a Paper fork with lots of extra gameplay options. Uses Paper/Spigot plugins.</summary>
    public class PurpurSoftware : ServerSoftware
    {
        private const string Api = "https://api.purpurmc.org/v2/purpur";

        public override string Id => "purpur";
        public override string Name => "Purpur";
        public override string Blurb => "Purpur - Paper with extra fun settings, uses the same plugins";
        public override string AddonFolder => "plugins";
        public override string[] ModrinthLoaders => new[] { "purpur", "paper", "spigot", "bukkit" };
        public override string GeyserPlatform => "spigot";

        public override async Task<List<string>> GetVersionsAsync()
        {
            var d = Json.ParseObject(await Net.GetStringAsync(Api).ConfigureAwait(false));
            return VersionOrder.NewestFirst(d.Arr("versions").Select(v => v?.ToString()));
        }

        public override async Task<SoftwareBuild> ResolveAsync(string mcVersion, string loaderVersion)
        {
            var version = await ResolveVersionAsync(mcVersion).ConfigureAwait(false);
            var which = string.IsNullOrWhiteSpace(loaderVersion) ? "latest" : loaderVersion.Trim();
            var r = await Net.SendAsync(HttpMethod.Get, Api + "/" + Uri.EscapeDataString(version) + "/" + Uri.EscapeDataString(which)).ConfigureAwait(false);
            if (r.Status == HttpStatusCode.NotFound) throw NoSuchVersion(version);
            if (!r.Ok) throw new FriendlyException("PurpurMC answered with error " + (int)r.Status + ".");
            var d = Json.ParseObject(r.Body);
            if (d.Str("result") != "SUCCESS" || d.Str("build") == null) throw NoSuchVersion(version);
            var build = d.Str("build");
            return new SoftwareBuild
            {
                Software = Id, McVersion = version, Loader = build,
                Url = Api + "/" + version + "/" + build + "/download",
                FileName = "purpur-" + version + "-" + build + ".jar",
                Md5 = d.Str("md5"),
                JavaMajor = await Mojang.JavaMajorAsync(version).ConfigureAwait(false),
            };
        }
    }

    // ====================================================================== Fabric

    /// <summary>Fabric: lightweight mod loader. Its server launcher downloads Minecraft itself on first start.</summary>
    public class FabricSoftware : ServerSoftware
    {
        private const string Meta = "https://meta.fabricmc.net/v2/versions";

        public override string Id => "fabric";
        public override string Name => "Fabric";
        public override string Blurb => "Fabric - lightweight mods (Fabric API is added for you)";
        public override string AddonFolder => "mods";
        public override string[] ModrinthLoaders => new[] { "fabric" };
        public override string GeyserPlatform => "fabric";

        public override async Task<List<string>> GetVersionsAsync()
        {
            var list = Json.ParseArray(await Net.GetStringAsync(Meta + "/game").ConfigureAwait(false)).OfType<Dictionary<string, object>>();
            return VersionOrder.NewestFirst(list.Where(v => v.Bool("stable")).Select(v => v.Str("version")));
        }

        public override async Task<SoftwareBuild> ResolveAsync(string mcVersion, string loaderVersion)
        {
            var version = await ResolveVersionAsync(mcVersion).ConfigureAwait(false);
            if (!(await GetVersionsAsync().ConfigureAwait(false)).Contains(version)) throw NoSuchVersion(version);

            var loader = loaderVersion;
            if (string.IsNullOrWhiteSpace(loader))
            {
                var loaders = Json.ParseArray(await Net.GetStringAsync(Meta + "/loader").ConfigureAwait(false)).OfType<Dictionary<string, object>>();
                loader = loaders.FirstOrDefault(l => l.Bool("stable"))?.Str("version");
            }
            var installers = Json.ParseArray(await Net.GetStringAsync(Meta + "/installer").ConfigureAwait(false)).OfType<Dictionary<string, object>>();
            var installer = installers.FirstOrDefault(i => i.Bool("stable"))?.Str("version");
            if (loader == null || installer == null) throw new FriendlyException("Fabric's download information was incomplete.");

            return new SoftwareBuild
            {
                Software = Id, McVersion = version, Loader = loader,
                // The installer segment is required (the URL without it is a 404).
                Url = Meta + "/loader/" + version + "/" + loader + "/" + installer + "/server/jar",
                FileName = "fabric-server-" + version + "-" + loader + ".jar",
                JavaMajor = await Mojang.JavaMajorAsync(version).ConfigureAwait(false),
            };
        }
    }

    // ====================================================================== Forge-style (shared by NeoForge and Forge)

    /// <summary>Forge and NeoForge ship an installer that lays out libraries and an argument file to start from.</summary>
    public abstract class InstallerSoftware : ServerSoftware
    {
        public override string AddonFolder => "mods";

        protected abstract string InstallerUrl(SoftwareBuild b);
        protected abstract string InstallFlag { get; }
        protected abstract string ArgsFile(SoftwareBuild b);

        public override async Task<List<string>> InstallAsync(SoftwareBuild b, string javaExe, ProgressHandler progress)
        {
            var url = InstallerUrl(b);
            var installer = Path.Combine(AppPaths.Downloads, Path.GetFileName(new Uri(url).AbsolutePath));
            string sha1 = null;
            try { sha1 = (await Net.GetStringAsync(url + ".sha1").ConfigureAwait(false)).Trim().Split(' ')[0]; } catch { /* no sidecar */ }
            await Net.DownloadFileAsync(url, installer, Name + " installer", progress, sha1: sha1).ConfigureAwait(false);

            Log.Info("Running the " + Name + " installer (this downloads Minecraft and libraries, usually 1-3 minutes)...");
            var psi = new ProcessStartInfo
            {
                FileName = javaExe,
                Arguments = "-jar \"" + installer + "\" " + InstallFlag + " \"" + AppPaths.Server + "\"",
                WorkingDirectory = AppPaths.Downloads, // its log file goes here, not into the server folder
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            var output = new StringBuilder();
            using (var p = new Process { StartInfo = psi })
            {
                p.OutputDataReceived += (s, e) => { if (e.Data != null) lock (output) output.AppendLine(e.Data); };
                p.ErrorDataReceived += (s, e) => { if (e.Data != null) lock (output) output.AppendLine(e.Data); };
                p.Start();
                Native.TieToThisProcess(p);
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();
                bool done = await Task.Run(() => p.WaitForExit(20 * 60 * 1000)).ConfigureAwait(false);
                if (!done) { try { p.Kill(); } catch { } }
                p.WaitForExit(5000);
                Log.WriteFileOnly(Name + " installer output:\r\n" + output);
                if (!done || p.ExitCode != 0)
                    throw new FriendlyException("The " + Name + " installer failed" + (done ? " (exit code " + p.ExitCode + ")" : " (it took too long)") + ".",
                        "Check the internet connection and try again. Details are in the log file.");
            }
            try { File.Delete(installer); } catch { }

            var args = ArgsFile(b);
            if (!File.Exists(Path.Combine(AppPaths.Server, args)))
                throw new FriendlyException("The " + Name + " installer finished but didn't create " + args + ".");
            // run.bat ends with "pause", so CardboardBox starts Java directly with the same argument file.
            return new List<string> { "@" + args, "nogui" };
        }
    }

    // ====================================================================== NeoForge

    public class NeoForgeSoftware : InstallerSoftware
    {
        private const string Maven = "https://maven.neoforged.net/releases/net/neoforged/neoforge/";
        private const string VersionsApi = "https://maven.neoforged.net/api/maven/versions/releases/net/neoforged/neoforge";
        private static List<string> _all;
        private static DateTime _allAt;

        public override string Id => "neoforge";
        public override string Name => "NeoForge";
        public override string Blurb => "NeoForge - big mods and modpacks (the modern Forge)";
        public override string[] ModrinthLoaders => new[] { "neoforge" };
        public override string GeyserPlatform => "neoforge";
        protected override string InstallFlag => "--install-server";

        private static async Task<List<string>> AllAsync()
        {
            if (_all == null || (DateTime.UtcNow - _allAt).TotalMinutes > 30)
            {
                var d = Json.ParseObject(await Net.GetStringAsync(VersionsApi).ConfigureAwait(false));
                _all = d.Arr("versions").Select(v => v?.ToString()).Where(v => v != null).ToList();
                _allAt = DateTime.UtcNow;
            }
            return _all;
        }

        /// <summary>NeoForge 21.1.x is for MC 1.21.1; 26.3.0.x is for MC 26.3; 26.1.2.x is for 26.1.2.</summary>
        public static string McFor(string neo)
        {
            var p = neo.Split('-')[0].Split('.');
            if (p.Length == 3 && int.TryParse(p[0], out var x) && x < 26)
                return "1." + p[0] + (p[1] == "0" ? "" : "." + p[1]);
            if (p.Length == 4)
                return p[0] + "." + p[1] + (p[2] == "0" ? "" : "." + p[2]);
            return null;
        }

        private static string PrefixFor(string mc)
        {
            var p = mc.Split('.');
            if (p[0] == "1") return p[1] + "." + (p.Length > 2 ? p[2] : "0") + ".";
            return p[0] + "." + p[1] + "." + (p.Length > 2 ? p[2] : "0") + ".";
        }

        public override async Task<List<string>> GetVersionsAsync()
        {
            var all = await AllAsync().ConfigureAwait(false);
            return VersionOrder.NewestFirst(all.Where(v => !v.Contains("-") && !v.Contains("+")).Select(McFor).Where(v => v != null));
        }

        public override async Task<SoftwareBuild> ResolveAsync(string mcVersion, string loaderVersion)
        {
            var version = await ResolveVersionAsync(mcVersion).ConfigureAwait(false);
            string neo = loaderVersion;
            bool experimental = false;
            if (string.IsNullOrWhiteSpace(neo))
            {
                var prefix = PrefixFor(version);
                var candidates = (await AllAsync().ConfigureAwait(false)).Where(v => v.StartsWith(prefix) && !v.Contains("+")).ToList();
                var stable = candidates.Where(v => !v.Contains("-")).ToList();
                var pool = stable.Count > 0 ? stable : candidates;
                experimental = stable.Count == 0;
                neo = pool.OrderByDescending(v => v, Comparer<string>.Create(VersionOrder.Compare)).FirstOrDefault();
                if (neo == null) throw NoSuchVersion(version);
            }
            return new SoftwareBuild
            {
                Software = Id, McVersion = version, Loader = neo.Trim(), Experimental = experimental,
                JavaMajor = await Mojang.JavaMajorAsync(version).ConfigureAwait(false),
            };
        }

        protected override string InstallerUrl(SoftwareBuild b) => Maven + b.Loader + "/neoforge-" + b.Loader + "-installer.jar";
        protected override string ArgsFile(SoftwareBuild b) => "libraries/net/neoforged/neoforge/" + b.Loader + "/win_args.txt";
    }

    // ====================================================================== Forge

    public class ForgeSoftware : InstallerSoftware
    {
        private const string Promotions = "https://files.minecraftforge.net/net/minecraftforge/forge/promotions_slim.json";
        private const string Maven = "https://maven.minecraftforge.net/net/minecraftforge/forge/";

        public override string Id => "forge";
        public override string Name => "Forge";
        public override string Blurb => "Forge - the classic mod loader (no crossplay)";
        public override string[] ModrinthLoaders => new[] { "forge" };
        protected override string InstallFlag => "--installServer";

        private static async Task<Dictionary<string, object>> PromosAsync() =>
            Json.ParseObject(await Net.GetStringAsync(Promotions).ConfigureAwait(false)).Obj("promos") ?? new Dictionary<string, object>();

        public override async Task<List<string>> GetVersionsAsync()
        {
            var promos = await PromosAsync().ConfigureAwait(false);
            var versions = promos.Keys.Select(k => Regex.Replace(k, "-(latest|recommended)$", ""));
            // Forge 1.17+ starts from an argument file; older Forge works differently and isn't supported here.
            return VersionOrder.NewestFirst(versions).Where(v => VersionOrder.Compare(v, "1.17") >= 0).ToList();
        }

        public override async Task<SoftwareBuild> ResolveAsync(string mcVersion, string loaderVersion)
        {
            var version = await ResolveVersionAsync(mcVersion).ConfigureAwait(false);
            var forge = loaderVersion;
            if (string.IsNullOrWhiteSpace(forge))
            {
                var promos = await PromosAsync().ConfigureAwait(false);
                forge = promos.Str(version + "-recommended") ?? promos.Str(version + "-latest");
                if (forge == null) throw NoSuchVersion(version);
            }
            return new SoftwareBuild
            {
                Software = Id, McVersion = version, Loader = forge.Trim(),
                JavaMajor = await Mojang.JavaMajorAsync(version).ConfigureAwait(false),
            };
        }

        private static string Full(SoftwareBuild b) => b.McVersion + "-" + b.Loader;
        protected override string InstallerUrl(SoftwareBuild b) => Maven + Full(b) + "/forge-" + Full(b) + "-installer.jar";
        protected override string ArgsFile(SoftwareBuild b) => "libraries/net/minecraftforge/forge/" + Full(b) + "/win_args.txt";
    }
}
