using System;
using System.Collections.Generic;

namespace CardboardBox.Core
{
    /// <summary>
    /// What CardboardBox has installed / synced so far. Saved to state.json.
    /// Not meant to be edited by hand.
    /// </summary>
    public class AppState
    {
        // GitHub sync
        public string SnapshotSha;        // commit currently downloaded to cache\repo
        public string AppliedSha;         // commit whose files were copied into server\
        public string AppliedMessage;
        public string AppliedDate;
        public DateTime LastCheckUtc;
        public string LastCheckResult;
        /// <summary>server-relative path -> sha256 of the repo copy we last applied.</summary>
        public Dictionary<string, string> SyncedFiles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // Installed server software
        public string InstalledSoftware;      // paper, purpur, fabric, ...
        public string InstalledMcVersion;     // e.g. 26.2
        public string InstalledLoader;        // Paper/Purpur build, Fabric loader, NeoForge/Forge version
        public string InstalledKey;           // identifies the exact download that's installed
        public List<string> LaunchArgs = new List<string>();
        /// <summary>Newest Minecraft version that has opened the current world (worlds can't go back).</summary>
        public string WorldVersion;

        // From CardboardBox 1.0/1.1 (Paper only) - migrated on load
        public string PaperVersion;
        public int PaperBuild;
        public string PaperSha256;
        public int JavaMinimum;
        public List<string> JavaFlags = new List<string>();

        // playit.gg agent
        public string PlayitVersion;
        public DateTime PlayitCheckedUtc;

        /// <summary>plugin key from the manifest -> jar file name we downloaded for it.</summary>
        public Dictionary<string, string> ManagedPlugins = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public static AppState Load()
        {
            var s = new AppState();
            try
            {
                var d = Json.ReadFile(AppPaths.State);
                if (d == null) return s;
                s.SnapshotSha = d.Str("snapshotSha");
                s.AppliedSha = d.Str("appliedSha");
                s.AppliedMessage = d.Str("appliedMessage");
                s.AppliedDate = d.Str("appliedDate");
                if (DateTime.TryParse(d.Str("lastCheckUtc"), null, System.Globalization.DateTimeStyles.RoundtripKind, out var t))
                    s.LastCheckUtc = t.ToUniversalTime();
                s.LastCheckResult = d.Str("lastCheckResult");
                s.SyncedFiles = d.StrMap("syncedFiles");
                s.PaperVersion = d.Str("paperVersion");
                s.PaperBuild = d.Int("paperBuild");
                s.PaperSha256 = d.Str("paperSha256");
                s.JavaMinimum = d.Int("javaMinimum");
                s.JavaFlags = new List<string>();
                foreach (var f in d.Arr("javaFlags")) if (f != null) s.JavaFlags.Add(f.ToString());
                s.ManagedPlugins = d.StrMap("managedPlugins");
                s.InstalledSoftware = d.Str("installedSoftware");
                s.InstalledMcVersion = d.Str("installedMcVersion");
                s.InstalledLoader = d.Str("installedLoader");
                s.InstalledKey = d.Str("installedKey");
                s.LaunchArgs = new List<string>();
                foreach (var a in d.Arr("launchArgs")) if (a != null) s.LaunchArgs.Add(a.ToString());
                s.WorldVersion = d.Str("worldVersion");
                if (s.InstalledSoftware == null && s.PaperVersion != null)
                {
                    // CardboardBox 1.0/1.1 only ran Paper
                    s.InstalledSoftware = "paper";
                    s.InstalledMcVersion = s.PaperVersion;
                    s.InstalledLoader = s.PaperBuild.ToString();
                    s.InstalledKey = "paper|" + s.PaperVersion + "|" + s.PaperBuild;
                    s.LaunchArgs = new List<string> { "-jar", "server.jar", "--nogui" };
                    if (s.WorldVersion == null) s.WorldVersion = s.PaperVersion;
                }
                s.PlayitVersion = d.Str("playitVersion");
                if (DateTime.TryParse(d.Str("playitCheckedUtc"), null, System.Globalization.DateTimeStyles.RoundtripKind, out var pc))
                    s.PlayitCheckedUtc = pc.ToUniversalTime();
            }
            catch (Exception ex)
            {
                Log.Error("state.json was unreadable, starting fresh", ex);
            }
            return s;
        }

        public void Save()
        {
            var d = new Dictionary<string, object>
            {
                ["snapshotSha"] = SnapshotSha,
                ["appliedSha"] = AppliedSha,
                ["appliedMessage"] = AppliedMessage,
                ["appliedDate"] = AppliedDate,
                ["lastCheckUtc"] = LastCheckUtc == default(DateTime) ? null : (object)LastCheckUtc,
                ["lastCheckResult"] = LastCheckResult,
                ["syncedFiles"] = SyncedFiles,
                ["paperVersion"] = PaperVersion,
                ["paperBuild"] = PaperBuild,
                ["paperSha256"] = PaperSha256,
                ["javaMinimum"] = JavaMinimum,
                ["javaFlags"] = JavaFlags,
                ["managedPlugins"] = ManagedPlugins,
                ["installedSoftware"] = InstalledSoftware,
                ["installedMcVersion"] = InstalledMcVersion,
                ["installedLoader"] = InstalledLoader,
                ["installedKey"] = InstalledKey,
                ["launchArgs"] = LaunchArgs,
                ["worldVersion"] = WorldVersion,
                ["playitVersion"] = PlayitVersion,
                ["playitCheckedUtc"] = PlayitCheckedUtc == default(DateTime) ? null : (object)PlayitCheckedUtc,
            };
            try { Json.WriteFile(AppPaths.State, d); }
            catch (Exception ex) { Log.Error("Couldn't save state", ex); }
        }
    }
}
