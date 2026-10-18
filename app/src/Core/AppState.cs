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

        // Installed software
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
                ["playitVersion"] = PlayitVersion,
                ["playitCheckedUtc"] = PlayitCheckedUtc == default(DateTime) ? null : (object)PlayitCheckedUtc,
            };
            try { Json.WriteFile(AppPaths.State, d); }
            catch (Exception ex) { Log.Error("Couldn't save state", ex); }
        }
    }
}
