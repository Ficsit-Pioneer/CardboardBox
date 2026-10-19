using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;

namespace CardboardBox.Core
{
    /// <summary>Replaces CardboardBox.exe with the newest version from the repo's GitHub Releases.</summary>
    public static class SelfUpdate
    {
        public static void CleanupOldExe()
        {
            try
            {
                var old = AppPaths.ExePath + ".old";
                if (File.Exists(old)) File.Delete(old);
            }
            catch { /* still in use right after an update - fine, next time */ }
        }

        public static bool IsNewer(ReleaseInfo rel) =>
            rel != null && rel.Version != null && rel.ExeUrl != null && rel.Version > AppPaths.AppVersion;

        /// <summary>Downloads the new exe, swaps it in and starts it. The caller must then exit.</summary>
        public static async Task InstallAndRestartAsync(ReleaseInfo rel, ProgressHandler progress, bool startServer = false)
        {
            var exe = AppPaths.ExePath;
            var fresh = exe + ".new";
            var old = exe + ".old";

            await Net.DownloadFileAsync(rel.ExeUrl, fresh, "CardboardBox " + rel.Tag, progress).ConfigureAwait(false);

            var bytes = new byte[2];
            using (var fs = File.OpenRead(fresh)) fs.Read(bytes, 0, 2);
            if (new FileInfo(fresh).Length < 50 * 1024 || bytes[0] != 'M' || bytes[1] != 'Z')
            {
                File.Delete(fresh);
                throw new FriendlyException("The downloaded update doesn't look like a valid program.", "Download it by hand from " + rel.PageUrl);
            }

            try
            {
                if (File.Exists(old)) File.Delete(old);
                File.Move(exe, old);      // Windows allows renaming a running exe
                File.Move(fresh, exe);
            }
            catch (Exception ex)
            {
                if (!File.Exists(exe) && File.Exists(old)) File.Move(old, exe);
                throw new FriendlyException("Couldn't replace CardboardBox.exe (" + ex.Message + ").",
                    "Move CardboardBox.exe to a normal folder like Desktop or Documents, or download the update from " + rel.PageUrl, ex);
            }

            Log.Success("Updated to CardboardBox " + rel.Tag + " - restarting.");
            Process.Start(new ProcessStartInfo(exe, "--updated" + (startServer ? " --start-server" : "")) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exe) });
        }
    }
}
