using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace CardboardBox.Core
{
    public class SyncResult
    {
        public List<string> Added = new List<string>();
        public List<string> Updated = new List<string>();
        public List<string> Removed = new List<string>();
        public bool Changed => Added.Count + Updated.Count + Removed.Count > 0;

        public string Summary =>
            !Changed ? "no file changes" :
            string.Join(", ", new[]
            {
                Added.Count > 0 ? Added.Count + " added" : null,
                Updated.Count > 0 ? Updated.Count + " updated" : null,
                Removed.Count > 0 ? Removed.Count + " removed" : null,
            }.Where(s => s != null));
    }

    /// <summary>
    /// Copies the repo's "server" folder into the real server folder.
    ///
    /// Rules (so the server's own changes are respected, like git pull):
    ///  * a file the repo has but the server doesn't  -> copied in
    ///  * a file the repo CHANGED since the last sync  -> overwritten
    ///  * a file the repo didn't change               -> left alone (plugins may edit their configs)
    ///  * a file the repo DELETED                      -> deleted
    ///  * server.properties is merged key-by-key (see ServerProperties)
    ///  * eula.txt and dot-files are never copied; worlds are never touched
    /// </summary>
    public static class RepoSync
    {
        public static string RepoServerFolder => Path.Combine(AppPaths.RepoSnapshot, "server");

        public static SyncResult Apply(AppState state)
        {
            var result = new SyncResult();
            var src = RepoServerFolder;
            var newMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            if (Directory.Exists(src))
            {
                foreach (var file in Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories))
                {
                    var rel = FileUtil.Relative(src, file);
                    if (ShouldSkip(rel)) continue;

                    var hash = Hash.FileSha256(file);
                    newMap[rel] = hash;
                    state.SyncedFiles.TryGetValue(rel, out var previous);
                    if (rel.Equals("server.properties", StringComparison.OrdinalIgnoreCase))
                    {
                        // merged key-by-key later (ServerProperties.BuildLive)
                        if (!string.Equals(previous, hash, StringComparison.OrdinalIgnoreCase)) result.Updated.Add(rel);
                        continue;
                    }

                    var dest = Path.Combine(AppPaths.Server, rel);

                    if (!File.Exists(dest))
                    {
                        Copy(file, dest, rel);
                        result.Added.Add(rel);
                    }
                    else if (!string.Equals(previous, hash, StringComparison.OrdinalIgnoreCase)
                             && !string.Equals(Hash.FileSha256(dest), hash, StringComparison.OrdinalIgnoreCase))
                    {
                        Copy(file, dest, rel);
                        result.Updated.Add(rel);
                    }
                }
            }
            else
            {
                Log.Warn("The GitHub repo has no \"server\" folder - nothing to copy.");
            }

            foreach (var rel in state.SyncedFiles.Keys.ToList())
            {
                if (newMap.ContainsKey(rel) || rel.Equals("server.properties", StringComparison.OrdinalIgnoreCase)) continue;
                var dest = Path.Combine(AppPaths.Server, rel);
                foreach (var candidate in new[] { dest, dest + ".disabled" })
                {
                    if (!File.Exists(candidate)) continue;
                    try { File.Delete(candidate); }
                    catch (Exception ex) { throw new FriendlyException("Couldn't remove " + rel + " (" + ex.Message + ").", "Stop the server and try again."); }
                }
                result.Removed.Add(rel);
            }

            state.SyncedFiles = newMap;
            return result;
        }

        /// <summary>Re-applies the "disabled" switch for plugins turned off in the app.</summary>
        public static void ApplyDisabledPlugins(Settings settings)
        {
            foreach (var name in settings.DisabledPlugins)
            foreach (var folder in new[] { AppPaths.Plugins, AppPaths.Mods })
            {
                var jar = Path.Combine(folder, name);
                if (!File.Exists(jar)) continue;
                var off = jar + ".disabled";
                try
                {
                    if (File.Exists(off)) File.Delete(off);
                    File.Move(jar, off);
                }
                catch (Exception ex) { Log.Warn("Couldn't disable plugin " + name + ": " + ex.Message); }
            }
        }

        private static bool ShouldSkip(string rel)
        {
            if (rel.Equals("eula.txt", StringComparison.OrdinalIgnoreCase)) return true;
            // .gitkeep, .DS_Store, .git folders ...
            return rel.Split('\\', '/').Any(part => part.StartsWith("."));
        }

        private static void Copy(string src, string dest, string rel)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(dest));
            try
            {
                var tmp = dest + ".cbx-tmp";
                File.Copy(src, tmp, true);
                if (File.Exists(dest)) File.Delete(dest);
                File.Move(tmp, dest);
            }
            catch (IOException ex)
            {
                throw new FriendlyException("Couldn't update " + rel + " - it's being used by another program.",
                    "Stop the server (or close the program using it) and try again. (" + ex.Message + ")", ex);
            }
        }
    }
}
