using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using CardboardBox.Core;
using CardboardBox.UI;

namespace CardboardBox
{
    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            // Only one CardboardBox per PC user (two would fight over the same world).
            bool justUpdated = args.Contains("--updated");
            using (var mutex = new Mutex(false, "Local\\CardboardBox-" + AppPaths.Root.ToLowerInvariant().GetHashCode()))
            {
                bool owned;
                try { owned = mutex.WaitOne(justUpdated ? 20000 : 0); }
                catch (AbandonedMutexException) { owned = true; }
                if (!owned)
                {
                    MessageBox.Show("CardboardBox is already running.\n\nLook for it in the taskbar.", "CardboardBox",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
                Application.ThreadException += (s, e) => ShowCrash(e.Exception, canContinue: true);
                AppDomain.CurrentDomain.UnhandledException += (s, e) => ShowCrash(e.ExceptionObject as Exception, canContinue: false);
                TaskScheduler.UnobservedTaskException += (s, e) =>
                {
                    Log.WriteFileOnly("ERROR unobserved task exception: " + e.Exception);
                    e.SetObserved();
                };

                try { AppPaths.EnsureFolders(); }
                catch (Exception ex)
                {
                    MessageBox.Show("CardboardBox couldn't create its folder:\n" + AppPaths.Root + "\n\n" + Errors.Friendly(ex),
                        "CardboardBox", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }

                SelfUpdate.CleanupOldExe();
                Log.WriteFileOnly("==== CardboardBox " + AppPaths.AppVersionText + " starting ====");
                Application.Run(new MainForm());
                try { mutex.ReleaseMutex(); } catch { }
            }
        }

        private static void ShowCrash(Exception ex, bool canContinue)
        {
            Log.WriteFileOnly("ERROR unhandled: " + ex);
            try
            {
                var text = "Something went wrong inside CardboardBox:\n\n" + Errors.Friendly(ex) +
                           "\n\nThe details were saved to:\n" + Log.FilePath +
                           (canContinue ? "\n\nCardboardBox will keep running." : "");
                MessageBox.Show(text, "CardboardBox - unexpected error", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            catch { /* nothing more we can do */ }
        }
    }
}
