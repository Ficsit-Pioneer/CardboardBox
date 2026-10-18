using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;

namespace CardboardBox.Core
{
    /// <summary>One exact download of a server program.</summary>
    public class SoftwareBuild
    {
        public string Software;
        public string McVersion;
        /// <summary>Paper/Purpur build number, Fabric loader version, or NeoForge/Forge version.</summary>
        public string Loader;
        public string Url;
        public string FileName;
        public string Sha256, Sha1, Md5;
        public int JavaMajor;
        public List<string> JvmFlags = new List<string>();
        public bool Experimental;

        public string Key => Software + "|" + McVersion + "|" + Loader;
    }

    /// <summary>
    /// A kind of Minecraft server CardboardBox can run. Each one knows which versions exist,
    /// how to download / install itself, and how it is started.
    /// </summary>
    public abstract class ServerSoftware
    {
        public abstract string Id { get; }
        public abstract string Name { get; }
        /// <summary>One line for the dropdown.</summary>
        public abstract string Blurb { get; }
        /// <summary>"plugins", "mods" or null (no add-ons).</summary>
        public abstract string AddonFolder { get; }
        /// <summary>Modrinth loader names whose add-ons work here.</summary>
        public virtual string[] ModrinthLoaders => new string[0];
        /// <summary>GeyserMC platform for crossplay, or null if crossplay isn't possible.</summary>
        public virtual string GeyserPlatform => null;
        public virtual string FloodgatePlatform => GeyserPlatform;
        public bool SupportsCrossplay => GeyserPlatform != null;
        public string AddonWord => AddonFolder == "mods" ? "mods" : "plugins";

        /// <summary>
        /// Worlds only move cleanly between servers of the same family: Paper/Purpur ("bukkit": Nether and End in
        /// separate folders, Paper data packs) and everything else ("vanilla"). Each family keeps its own world.
        /// </summary>
        public string WorldFamily => AddonFolder == "plugins" ? "bukkit" : "vanilla";
        public static string FamilyName(string family) => family == "bukkit" ? "Paper/Purpur" : "Fabric/NeoForge/Forge/Vanilla";

        /// <summary>Minecraft versions available for this software, newest first.</summary>
        public abstract Task<List<string>> GetVersionsAsync();

        public abstract Task<SoftwareBuild> ResolveAsync(string mcVersion, string loaderVersion);

        /// <summary>Downloads / installs the build into the server folder and returns the launch arguments.</summary>
        public virtual async Task<List<string>> InstallAsync(SoftwareBuild b, string javaExe, ProgressHandler progress)
        {
            var tmp = Path.Combine(AppPaths.Downloads, b.FileName ?? (Id + ".jar"));
            await Net.DownloadFileAsync(b.Url, tmp, Name + " " + b.McVersion, progress, sha256: b.Sha256, sha1: b.Sha1, md5: b.Md5).ConfigureAwait(false);
            File.Copy(tmp, AppPaths.ServerJar, true);
            File.Delete(tmp);
            return new List<string> { "-jar", "server.jar", "--nogui" };
        }

        /// <summary>"latest" becomes the newest version this software has.</summary>
        public async Task<string> ResolveVersionAsync(string requested)
        {
            requested = (requested ?? "").Trim();
            if (requested.Length > 0 && !requested.Equals("latest", StringComparison.OrdinalIgnoreCase)) return requested;
            var versions = await GetVersionsAsync().ConfigureAwait(false);
            if (versions.Count == 0) throw new FriendlyException(Name + " has no Minecraft versions available right now.");
            return versions[0];
        }

        protected FriendlyException NoSuchVersion(string mc) =>
            new FriendlyException(Name + " isn't available for Minecraft " + mc + ".",
                "Pick another version or server type in Server Settings.");

        // ------------------------------------------------------------------ registry

        public static readonly ServerSoftware[] All =
        {
            new PaperSoftware(), new PurpurSoftware(), new FabricSoftware(),
            new NeoForgeSoftware(), new ForgeSoftware(), new VanillaSoftware(),
        };

        public static ServerSoftware Get(string id)
        {
            var sw = All.FirstOrDefault(s => s.Id.Equals((id ?? "").Trim(), StringComparison.OrdinalIgnoreCase));
            if (sw == null)
                throw new FriendlyException("Unknown server software \"" + id + "\".",
                    "Use one of: " + string.Join(", ", All.Select(s => s.Id)) + ".");
            return sw;
        }

        /// <summary>Good general-purpose garbage collector settings (Aikar's flags) for servers that don't publish their own.</summary>
        public static readonly string[] DefaultJvmFlags =
        {
            "-XX:+UseG1GC", "-XX:+ParallelRefProcEnabled", "-XX:MaxGCPauseMillis=200", "-XX:+UnlockExperimentalVMOptions",
            "-XX:+DisableExplicitGC", "-XX:+AlwaysPreTouch", "-XX:G1NewSizePercent=30", "-XX:G1MaxNewSizePercent=40",
            "-XX:G1HeapRegionSize=8M", "-XX:G1ReservePercent=20", "-XX:G1HeapWastePercent=5", "-XX:G1MixedGCCountTarget=4",
            "-XX:InitiatingHeapOccupancyPercent=15", "-XX:G1MixedGCLiveThresholdPercent=90", "-XX:G1RSetUpdatingPauseTimePercent=5",
            "-XX:SurvivorRatio=32", "-XX:+PerfDisableSharedMem", "-XX:MaxTenuringThreshold=1",
        };
    }

    // ====================================================================== Paper

    public class PaperSoftware : ServerSoftware
    {
        private const string Fill = "https://fill.papermc.io/v3/projects/paper";

        public override string Id => "paper";
        public override string Name => "Paper";
        public override string Blurb => "Paper - plugins, fastest and most popular (recommended)";
        public override string AddonFolder => "plugins";
        public override string[] ModrinthLoaders => new[] { "paper", "spigot", "bukkit" };
        public override string GeyserPlatform => "spigot";

        public override async Task<List<string>> GetVersionsAsync()
        {
            var d = Json.ParseObject(await Net.GetStringAsync(Fill).ConfigureAwait(false));
            var all = new List<string>();
            var groups = d.Obj("versions");
            if (groups != null)
                foreach (var g in groups.Values) all.AddRange(Json.AsList(g).Select(x => x?.ToString()));
            return VersionOrder.NewestFirst(all);
        }

        public override async Task<SoftwareBuild> ResolveAsync(string mcVersion, string loaderVersion)
        {
            var version = await ResolveVersionAsync(mcVersion).ConfigureAwait(false);
            var vr = await Net.SendAsync(HttpMethod.Get, Fill + "/versions/" + Uri.EscapeDataString(version)).ConfigureAwait(false);
            if (vr.Status == HttpStatusCode.NotFound) throw NoSuchVersion(version);
            if (!vr.Ok) throw new FriendlyException("PaperMC answered with error " + (int)vr.Status + ".");

            var java = Json.ParseObject(vr.Body).Obj("version").Obj("java");
            var b = new SoftwareBuild { Software = Id, McVersion = version, JavaMajor = java.Obj("version").Int("minimum", 21) };
            foreach (var f in java.Obj("flags").Arr("recommended")) if (f != null) b.JvmFlags.Add(f.ToString());

            var builds = Json.ParseArray(await Net.GetStringAsync(Fill + "/versions/" + Uri.EscapeDataString(version) + "/builds").ConfigureAwait(false))
                .OfType<Dictionary<string, object>>().ToList();
            if (builds.Count == 0) throw new FriendlyException("Paper has no downloads for Minecraft " + version + " yet.");

            Dictionary<string, object> pick;
            int wanted = int.TryParse(loaderVersion, out var n) ? n : 0;
            if (wanted > 0)
            {
                pick = builds.FirstOrDefault(x => x.Int("id") == wanted);
                if (pick == null) throw new FriendlyException("Paper build " + wanted + " for " + version + " doesn't exist.");
            }
            else
            {
                pick = builds.FirstOrDefault(x => string.Equals(x.Str("channel"), "STABLE", StringComparison.OrdinalIgnoreCase));
                if (pick == null) { pick = builds[0]; b.Experimental = true; }
            }

            var dl = pick.Obj("downloads").Obj("server:default");
            b.Loader = pick.Int("id").ToString();
            b.Url = dl.Str("url");
            b.FileName = dl.Str("name");
            b.Sha256 = dl.Obj("checksums").Str("sha256");
            if (string.IsNullOrEmpty(b.Url)) throw new FriendlyException("Paper's download information was incomplete.");
            return b;
        }
    }

    // ====================================================================== Vanilla

    public class VanillaSoftware : ServerSoftware
    {
        public override string Id => "vanilla";
        public override string Name => "Vanilla";
        public override string Blurb => "Vanilla - plain Minecraft from Mojang, no plugins or mods";
        public override string AddonFolder => null;

        public override async Task<List<string>> GetVersionsAsync() => VersionOrder.NewestFirst(await Mojang.ReleasesAsync().ConfigureAwait(false));

        public override async Task<SoftwareBuild> ResolveAsync(string mcVersion, string loaderVersion)
        {
            var version = await ResolveVersionAsync(mcVersion).ConfigureAwait(false);
            var v = await Mojang.VersionAsync(version).ConfigureAwait(false);
            var server = v.Obj("downloads").Obj("server");
            if (server == null) throw NoSuchVersion(version);
            return new SoftwareBuild
            {
                Software = Id, McVersion = version, Loader = "mojang",
                Url = server.Str("url"), Sha1 = server.Str("sha1"), FileName = "minecraft_server." + version + ".jar",
                JavaMajor = v.Obj("javaVersion").Int("majorVersion", 21),
            };
        }
    }
}
