using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;

namespace CardboardBox.Core
{
    /// <summary>
    /// Things the person running CardboardBox can change in the app. Saved to settings.json.
    /// Server settings changed in the app are stored as "overrides" on top of the GitHub repo's values.
    /// </summary>
    public class Settings
    {
        public const string DefaultOwner = "Ficsit-Pioneer";
        public const string DefaultRepo = "CardboardBox";
        public const string DefaultBranch = "main";

        public string RepoOwner = DefaultOwner;
        public string RepoName = DefaultRepo;
        public string Branch = DefaultBranch;
        public int CheckIntervalMinutes = 60;

        /// <summary>0 = use the amount the GitHub repo recommends.</summary>
        public int RamMB;

        public bool EulaAccepted;
        public bool AutoStartServer;
        public bool AutoRestartOnCrash = true;
        public bool AutoRestartForUpdates = true;
        public bool BackupBeforeUpdates = true;
        public bool KeepPcAwake = true;
        public int KeepBackups = 10;

        /// <summary>server.properties values changed in the app (key -> value).</summary>
        public Dictionary<string, string> PropertyOverrides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Plugin jar file names turned off in the app.</summary>
        public List<string> DisabledPlugins = new List<string>();

        private string _tokenProtected;

        public string RepoSlug => RepoOwner + "/" + RepoName;
        public string RepoUrl => "https://github.com/" + RepoSlug;

        /// <summary>Optional GitHub token (only needed to PUSH changes). Encrypted with Windows DPAPI for this user.</summary>
        public string GitHubToken
        {
            get
            {
                if (string.IsNullOrEmpty(_tokenProtected)) return null;
                try
                {
                    var bytes = ProtectedData.Unprotect(Convert.FromBase64String(_tokenProtected), null, DataProtectionScope.CurrentUser);
                    return Encoding.UTF8.GetString(bytes);
                }
                catch { return null; }
            }
            set
            {
                if (string.IsNullOrWhiteSpace(value)) { _tokenProtected = null; return; }
                var bytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(value.Trim()), null, DataProtectionScope.CurrentUser);
                _tokenProtected = Convert.ToBase64String(bytes);
            }
        }

        public bool HasToken => !string.IsNullOrEmpty(GitHubToken);

        public static Settings Load()
        {
            var s = new Settings();
            try
            {
                var d = Json.ReadFile(AppPaths.Settings);
                if (d == null) return s;
                s.RepoOwner = d.Str("repoOwner", DefaultOwner);
                s.RepoName = d.Str("repoName", DefaultRepo);
                s.Branch = d.Str("branch", DefaultBranch);
                s.CheckIntervalMinutes = Math.Max(5, d.Int("checkIntervalMinutes", 60));
                s.RamMB = d.Int("ramMB", 0);
                s.EulaAccepted = d.Bool("eulaAccepted");
                s.AutoStartServer = d.Bool("autoStartServer");
                s.AutoRestartOnCrash = d.Bool("autoRestartOnCrash", true);
                s.AutoRestartForUpdates = d.Bool("autoRestartForUpdates", true);
                s.BackupBeforeUpdates = d.Bool("backupBeforeUpdates", true);
                s.KeepPcAwake = d.Bool("keepPcAwake", true);
                s.KeepBackups = Math.Max(1, d.Int("keepBackups", 10));
                s.PropertyOverrides = d.StrMap("propertyOverrides");
                s.DisabledPlugins = new List<string>();
                foreach (var p in d.Arr("disabledPlugins")) if (p != null) s.DisabledPlugins.Add(p.ToString());
                s._tokenProtected = d.Str("githubTokenEncrypted");
            }
            catch (Exception ex)
            {
                Log.Error("settings.json was unreadable, using defaults", ex);
            }
            return s;
        }

        public void Save()
        {
            var d = new Dictionary<string, object>
            {
                ["repoOwner"] = RepoOwner,
                ["repoName"] = RepoName,
                ["branch"] = Branch,
                ["checkIntervalMinutes"] = CheckIntervalMinutes,
                ["ramMB"] = RamMB,
                ["eulaAccepted"] = EulaAccepted,
                ["autoStartServer"] = AutoStartServer,
                ["autoRestartOnCrash"] = AutoRestartOnCrash,
                ["autoRestartForUpdates"] = AutoRestartForUpdates,
                ["backupBeforeUpdates"] = BackupBeforeUpdates,
                ["keepPcAwake"] = KeepPcAwake,
                ["keepBackups"] = KeepBackups,
                ["propertyOverrides"] = PropertyOverrides,
                ["disabledPlugins"] = DisabledPlugins,
                ["githubTokenEncrypted"] = _tokenProtected,
            };
            try { Json.WriteFile(AppPaths.Settings, d); }
            catch (Exception ex) { Log.Error("Couldn't save settings", ex); }
        }
    }
}
