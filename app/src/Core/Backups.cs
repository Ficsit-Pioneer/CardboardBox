using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading.Tasks;

namespace CardboardBox.Core
{
    public class BackupInfo
    {
        public string Path;
        public string Name => System.IO.Path.GetFileNameWithoutExtension(Path);
        public DateTime Created;
        public long Size;
    }

    /// <summary>Zips up the worlds (plus ops/whitelist/bans) so nothing is ever lost.</summary>
    public static class Backups
    {
        private static readonly string[] ExtraFiles = { "ops.json", "whitelist.json", "banned-players.json", "banned-ips.json", "usercache.json" };

        public static List<BackupInfo> List()
        {
            if (!Directory.Exists(AppPaths.Backups)) return new List<BackupInfo>();
            return new DirectoryInfo(AppPaths.Backups).GetFiles("backup-*.zip")
                .Select(f => new BackupInfo { Path = f.FullName, Created = f.LastWriteTime, Size = f.Length })
                .OrderByDescending(b => b.Created).ToList();
        }

        /// <summary>Top-level folders in the server folder that contain a Minecraft world.</summary>
        public static List<string> WorldFolders()
        {
            if (!Directory.Exists(AppPaths.Server)) return new List<string>();
            return Directory.GetDirectories(AppPaths.Server)
                .Where(d => File.Exists(Path.Combine(d, "level.dat"))).ToList();
        }

        public static async Task<string> CreateAsync(MinecraftServer server, string reason, int keep)
        {
            var worlds = WorldFolders();
            if (worlds.Count == 0)
            {
                Log.Info("No world to back up yet.");
                return null;
            }

            bool live = server != null && server.IsAlive;
            if (live)
            {
                // Make the server write everything to disk and pause saving while we copy.
                var saved = new TaskCompletionSource<bool>();
                Action<string> watcher = line => { if (line.Contains("Saved the game") || line.Contains("Automatic saving is now disabled")) saved.TrySetResult(true); };
                server.OutputLine += watcher;
                server.SendCommand("save-off");
                server.SendCommand("save-all flush");
                await Task.WhenAny(saved.Task, Task.Delay(30000)).ConfigureAwait(false);
                await Task.Delay(1000).ConfigureAwait(false);
                server.OutputLine -= watcher;
            }

            try
            {
                Directory.CreateDirectory(AppPaths.Backups);
                var stamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
                var name = "backup-" + stamp + (string.IsNullOrEmpty(reason) ? "" : "-" + reason) + ".zip";
                var target = Path.Combine(AppPaths.Backups, name);
                var part = target + ".part";
                int skipped = 0;

                await Task.Run(() =>
                {
                    using (var fs = new FileStream(part, FileMode.Create, FileAccess.Write))
                    using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
                    {
                        foreach (var world in worlds)
                            foreach (var file in Directory.EnumerateFiles(world, "*", SearchOption.AllDirectories))
                            {
                                if (Path.GetFileName(file).Equals("session.lock", StringComparison.OrdinalIgnoreCase)) continue;
                                if (!AddFile(zip, file)) skipped++;
                            }
                        foreach (var extra in ExtraFiles)
                        {
                            var f = Path.Combine(AppPaths.Server, extra);
                            if (File.Exists(f)) AddFile(zip, f);
                        }
                    }
                    File.Move(part, target);
                }).ConfigureAwait(false);

                if (skipped > 0) Log.Warn("Backup skipped " + skipped + " file(s) that were in use.");
                Log.Success("Backup saved: " + name + " (" + FileUtil.FormatBytes(new FileInfo(target).Length) + ")");
                Prune(keep);
                return target;
            }
            finally
            {
                if (live) server.SendCommand("save-on");
            }
        }

        private static bool AddFile(ZipArchive zip, string file)
        {
            try
            {
                var rel = FileUtil.Relative(AppPaths.Server, file).Replace('\\', '/');
                using (var src = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var dst = zip.CreateEntry(rel, CompressionLevel.Fastest).Open())
                    src.CopyTo(dst);
                return true;
            }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
        }

        private static void Prune(int keep)
        {
            foreach (var old in List().Skip(Math.Max(1, keep)))
            {
                try { File.Delete(old.Path); Log.Info("Removed old backup " + old.Name); }
                catch (Exception ex) { Log.Warn("Couldn't remove old backup " + old.Name + ": " + ex.Message); }
            }
        }

        /// <summary>Puts a backup back. The current worlds are moved aside (never deleted) first.</summary>
        public static void Restore(BackupInfo backup)
        {
            var aside = Path.Combine(AppPaths.Backups, "replaced-" + DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss"));
            foreach (var world in WorldFolders())
            {
                Directory.CreateDirectory(aside);
                Directory.Move(world, Path.Combine(aside, Path.GetFileName(world)));
            }

            using (var zip = ZipFile.OpenRead(backup.Path))
            {
                var root = Path.GetFullPath(AppPaths.Server).TrimEnd('\\') + "\\";
                foreach (var entry in zip.Entries)
                {
                    if (string.IsNullOrEmpty(entry.Name)) continue;
                    var dest = Path.GetFullPath(Path.Combine(AppPaths.Server, entry.FullName));
                    if (!dest.StartsWith(root, StringComparison.OrdinalIgnoreCase)) continue; // never write outside the server folder
                    Directory.CreateDirectory(Path.GetDirectoryName(dest));
                    entry.ExtractToFile(dest, true);
                }
            }
            Log.Success("Restored backup " + backup.Name + (Directory.Exists(aside) ? ". The previous world was kept in backups\\" + Path.GetFileName(aside) : "."));
        }
    }
}
