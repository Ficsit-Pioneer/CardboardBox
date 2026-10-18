using System;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace CardboardBox.Core
{
    /// <summary>
    /// Geyser (Bedrock crossplay) settings. Bedrock is switching from RakNet to NetherNet on 2026-10-27 (Bedrock 26.60),
    /// so CardboardBox runs Geyser in "both" mode: old clients use RakNet, new clients use NetherNet.
    /// </summary>
    public static class GeyserConfig
    {
        /// <summary>RakNet (UDP) and NetherNet signaling (TCP) - the port Bedrock players type in.</summary>
        public const int BedrockPort = 19132;
        /// <summary>NetherNet / WebRTC game traffic (UDP). Must differ from BedrockPort in "both" mode.</summary>
        public const int NetherNetPort = 19133;

        /// <summary>
        /// Startup settings Geyser reads before its config file, so they work from the very first start:
        /// accept old and new Bedrock clients, and use GeyserMC's signaling service as well as the built-in one
        /// so players outside the home can find the server.
        /// </summary>
        public static readonly string[] JvmFlags =
        {
            "-DgeyserTransport=both",
            "-DgeyserWebrtcPort=" + NetherNetPort,
            "-DgeyserSignalingMode=hybrid",
        };

        public static string PathFor(ServerSoftware sw)
        {
            switch (sw?.GeyserPlatform)
            {
                case "spigot": return Path.Combine(AppPaths.Plugins, "Geyser-Spigot", "config.yml");
                case "fabric": return Path.Combine(AppPaths.Server, "config", "Geyser-Fabric", "config.yml");
                case "neoforge": return Path.Combine(AppPaths.Server, "config", "Geyser-NeoForge", "config.yml");
                default: return null;
            }
        }

        /// <summary>
        /// Adjusts Geyser's config file (Geyser writes it on its first start):
        ///  * auth-type floodgate - Bedrock players don't need a Java account
        ///  * assisted-joins     - GeyserMC's signaling service helps players behind routers connect
        /// Returns true if the file changed (Geyser then needs a reload).
        /// </summary>
        public static bool Apply(ServerSoftware sw)
        {
            var path = PathFor(sw);
            if (path == null || !File.Exists(path)) return false;
            try
            {
                var text = File.ReadAllText(path, Encoding.UTF8);
                var updated = Regex.Replace(text, @"(?m)^(\s*auth-type:\s*)[""']?online[""']?\s*$", "$1floodgate");
                updated = Regex.Replace(updated, @"(?m)^(\s*assisted-joins:\s*)false\s*$", "$1true");
                if (updated == text) return false;
                File.WriteAllText(path, updated, new UTF8Encoding(false));
                Log.Info("Crossplay: Geyser now uses Floodgate (no Java account needed for Bedrock players) and assisted joins.");
                return true;
            }
            catch (Exception ex)
            {
                Log.Warn("Couldn't adjust Geyser's settings: " + ex.Message);
                return false;
            }
        }
    }
}
