using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace CardboardBox.Core
{
    /// <summary>One plugin listed in cardboardbox.json.</summary>
    public class PluginSpec
    {
        /// <summary>Modrinth project slug or id, e.g. "chunky". CardboardBox picks the right version automatically.</summary>
        public string Modrinth;
        /// <summary>Direct download link to a .jar (used when Modrinth is not set).</summary>
        public string Url;
        /// <summary>File name to save a Url download as (optional).</summary>
        public string File;
        /// <summary>Optional sha256 of the Url download.</summary>
        public string Sha256;

        /// <summary>"geyser" or "floodgate" - added automatically when crossplay is on.</summary>
        public string Geyser;
        /// <summary>Added automatically because another add-on needs it.</summary>
        public bool IsDependency;

        public string Key =>
            !string.IsNullOrWhiteSpace(Geyser) ? "geyser:" + Geyser :
            !string.IsNullOrWhiteSpace(Modrinth) ? "modrinth:" + Modrinth.Trim().ToLowerInvariant() : "url:" + Url;

        public string DisplayName =>
            !string.IsNullOrWhiteSpace(Geyser) ? (Geyser == "floodgate" ? "Floodgate (crossplay)" : "Geyser (crossplay)") :
            !string.IsNullOrWhiteSpace(Modrinth) ? Modrinth : (File ?? Url);
    }

    /// <summary>
    /// cardboardbox.json in the root of the GitHub repo: tells every CardboardBox what to install.
    /// </summary>
    public class Manifest
    {
        public string ServerName = "CardboardBox";
        /// <summary>Server software: paper, purpur, fabric, neoforge, forge or vanilla.</summary>
        public string Software = "paper";
        /// <summary>Minecraft version, e.g. "26.2", or "latest" for the newest stable release.</summary>
        public string MinecraftVersion = "latest";
        /// <summary>Optional exact Fabric loader / NeoForge / Forge version. Empty = newest stable.</summary>
        public string LoaderVersion;
        /// <summary>Let Bedrock players (phones, consoles, Windows) join too, using Geyser + Floodgate.</summary>
        public bool Crossplay;
        /// <summary>Address of the owner's admin hub (hub/ in the repo), e.g. https://cardboardbox-hub.name.workers.dev</summary>
        public string AdminHub;
        /// <summary>0 = newest stable Paper build for that version.</summary>
        public int PaperBuild;
        /// <summary>0 = pick automatically from what Paper says it needs.</summary>
        public int JavaVersion;
        public int DefaultRamGB = 4;
        /// <summary>Message from the server owner, shown on the dashboard.</summary>
        public string Announcement;
        public List<string> ExtraJvmFlags = new List<string>();
        public List<PluginSpec> Plugins = new List<PluginSpec>();

        public static string PathInSnapshot => System.IO.Path.Combine(AppPaths.RepoSnapshot, "cardboardbox.json");

        public static Manifest LoadFromSnapshot()
        {
            var path = PathInSnapshot;
            if (!System.IO.File.Exists(path)) return new Manifest();
            try
            {
                return Parse(System.IO.File.ReadAllText(path, Encoding.UTF8));
            }
            catch (Exception ex)
            {
                throw new FriendlyException("cardboardbox.json in the GitHub repo has a mistake in it.",
                    "Ask the server owner to fix it (" + ex.Message + ").", ex);
            }
        }

        public static Manifest Parse(string json)
        {
            var d = Json.ParseObject(json);
            var m = new Manifest
            {
                ServerName = d.Str("serverName", "CardboardBox"),
                Software = (d.Str("software", "paper") ?? "paper").Trim().ToLowerInvariant(),
                MinecraftVersion = d.Str("minecraftVersion", "latest"),
                LoaderVersion = d.Str("loaderVersion"),
                Crossplay = d.Bool("crossplay"),
                AdminHub = d.Str("adminHub"),
                PaperBuild = d.Int("paperBuild", 0),
                JavaVersion = d.Int("javaVersion", 0),
                DefaultRamGB = Math.Max(1, d.Int("defaultRamGB", 4)),
                Announcement = d.Str("announcement"),
            };
            // CardboardBox 1.0/1.1 used "paperBuild" for the same thing
            if (string.IsNullOrWhiteSpace(m.LoaderVersion) && m.PaperBuild > 0) m.LoaderVersion = m.PaperBuild.ToString();
            if (string.IsNullOrWhiteSpace(m.LoaderVersion)) m.LoaderVersion = null;

            foreach (var f in d.Arr("extraJvmFlags"))
                if (f != null && !string.IsNullOrWhiteSpace(f.ToString())) m.ExtraJvmFlags.Add(f.ToString().Trim());

            // "mods" is accepted as another name for the same list
            foreach (var item in d.Arr("plugins").Concat(d.Arr("mods")))
            {
                if (item is string s && !string.IsNullOrWhiteSpace(s))
                {
                    // Shorthand: "chunky" means { "modrinth": "chunky" }
                    m.Plugins.Add(s.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                        ? new PluginSpec { Url = s.Trim() }
                        : new PluginSpec { Modrinth = s.Trim() });
                    continue;
                }
                var p = item as Dictionary<string, object>;
                if (p == null) continue;
                var spec = new PluginSpec
                {
                    Modrinth = p.Str("modrinth"),
                    Url = p.Str("url"),
                    File = p.Str("file"),
                    Sha256 = p.Str("sha256"),
                };
                if (string.IsNullOrWhiteSpace(spec.Modrinth) && string.IsNullOrWhiteSpace(spec.Url)) continue;
                m.Plugins.Add(spec);
            }
            return m;
        }
    }
}
