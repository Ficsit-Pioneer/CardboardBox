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

        /// <summary>Server type chosen in the app (null = use cardboardbox.json from GitHub).</summary>
        public string SoftwareOverride;
        public string VersionOverride;
        public bool? CrossplayOverride;

        public bool HasServerTypeOverride => SoftwareOverride != null || VersionOverride != null || CrossplayOverride != null;

        /// <summary>server.properties values changed in the app (key -> value).</summary>
        public Dictionary<string, string> PropertyOverrides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Plugin jar file names turned off in the app.</summary>
        public List<string> DisabledPlugins = new List<string>();

        /// <summary>Run a playit.gg tunnel while the server is running.</summary>
        public bool PlayitEnabled;
        /// <summary>The playit.gg address friends use (cached for display).</summary>
        public string PlayitAddress;
        public string PlayitAddressWithPort;
        /// <summary>Bedrock (crossplay) playit address as "host:port" (no longer created automatically).</summary>
        public string PlayitBedrockAddress;
        /// <summary>Address GeyserMC's signaling service (Warden) gave this server, e.g. name.wdn.gg (port 443).</summary>
        public string BedrockInternetAddress;

        private string _tokenProtected;
        private string _playitProtected;

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

        /// <summary>playit.gg agent secret key, encrypted with Windows DPAPI for this user.</summary>
        public string PlayitSecret
        {
            get { return Unprotect(_playitProtected); }
            set { _playitProtected = Protect(value); }
        }

        public bool HasPlayit => !string.IsNullOrEmpty(PlayitSecret);

        private static string Protect(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            var bytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(value.Trim()), null, DataProtectionScope.CurrentUser);
            return Convert.ToBase64String(bytes);
        }

        private static string Unprotect(string stored)
        {
            if (string.IsNullOrEmpty(stored)) return null;
            try { return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(stored), null, DataProtectionScope.CurrentUser)); }
            catch { return null; }
        }

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
                s.SoftwareOverride = d.Str("softwareOverride");
                s.VersionOverride = d.Str("versionOverride");
                if (d.ContainsKey("crossplayOverride") && d["crossplayOverride"] is bool cp) s.CrossplayOverride = cp;
                s.DisabledPlugins = new List<string>();
                foreach (var p in d.Arr("disabledPlugins")) if (p != null) s.DisabledPlugins.Add(p.ToString());
                s._tokenProtected = d.Str("githubTokenEncrypted");
                s.PlayitEnabled = d.Bool("playitEnabled");
                s.PlayitAddress = d.Str("playitAddress");
                s.PlayitAddressWithPort = d.Str("playitAddressWithPort");
                s.PlayitBedrockAddress = d.Str("playitBedrockAddress");
                s.BedrockInternetAddress = d.Str("bedrockInternetAddress");
                s._playitProtected = d.Str("playitSecretEncrypted");
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
                ["softwareOverride"] = SoftwareOverride,
                ["versionOverride"] = VersionOverride,
                ["crossplayOverride"] = CrossplayOverride,
                ["disabledPlugins"] = DisabledPlugins,
                ["githubTokenEncrypted"] = _tokenProtected,
                ["playitEnabled"] = PlayitEnabled,
                ["playitAddress"] = PlayitAddress,
                ["playitAddressWithPort"] = PlayitAddressWithPort,
                ["playitBedrockAddress"] = PlayitBedrockAddress,
                ["bedrockInternetAddress"] = BedrockInternetAddress,
                ["playitSecretEncrypted"] = _playitProtected,
            };
            try { Json.WriteFile(AppPaths.Settings, d); }
            catch (Exception ex) { Log.Error("Couldn't save settings", ex); }
        }
    }
}
