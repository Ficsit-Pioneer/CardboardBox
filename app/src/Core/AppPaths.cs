using System;
using System.IO;
using System.Reflection;

namespace CardboardBox.Core
{
    /// <summary>
    /// Where everything lives on disk. By default: %USERPROFILE%\CardboardBox
    /// (set CARDBOARDBOX_HOME to use a different folder).
    /// </summary>
    public static class AppPaths
    {
        public static readonly string Root = ResolveRoot();

        public static string Settings => Path.Combine(Root, "settings.json");
        public static string State => Path.Combine(Root, "state.json");
        public static string Logs => Path.Combine(Root, "logs");
        public static string Runtime => Path.Combine(Root, "runtime");
        public static string Downloads => Path.Combine(Root, "runtime", "downloads");
        public static string Cache => Path.Combine(Root, "cache");
        public static string RepoSnapshot => Path.Combine(Root, "cache", "repo");
        public static string Server => Path.Combine(Root, "server");
        public static string Plugins => Path.Combine(Root, "server", "plugins");
        public static string Backups => Path.Combine(Root, "backups");
        public static string Overrides => Path.Combine(Root, "overrides");
        public static string OverrideIcon => Path.Combine(Root, "overrides", "server-icon.png");
        public static string ServerJar => Path.Combine(Root, "server", "server.jar");

        public static string ExePath => Assembly.GetExecutingAssembly().Location;

        public static Version AppVersion => Assembly.GetExecutingAssembly().GetName().Version;

        public static string AppVersionText
        {
            get
            {
                var v = AppVersion;
                return v.Major + "." + v.Minor + "." + Math.Max(0, v.Build);
            }
        }

        public static void EnsureFolders()
        {
            foreach (var dir in new[] { Root, Logs, Runtime, Downloads, Cache, Server, Plugins, Backups, Overrides })
                Directory.CreateDirectory(dir);
        }

        private static string ResolveRoot()
        {
            var env = Environment.GetEnvironmentVariable("CARDBOARDBOX_HOME");
            if (!string.IsNullOrWhiteSpace(env))
                return Path.GetFullPath(env);
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "CardboardBox");
        }
    }
}
