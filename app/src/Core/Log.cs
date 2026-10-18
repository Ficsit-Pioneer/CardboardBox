using System;
using System.IO;
using System.Text;

namespace CardboardBox.Core
{
    public enum LogLevel { Info, Success, Warn, Error }

    /// <summary>
    /// Writes CardboardBox's own messages to logs\cardboardbox.log and to the console tab.
    /// </summary>
    public static class Log
    {
        private static readonly object Gate = new object();
        private const long MaxBytes = 5 * 1024 * 1024;

        /// <summary>Raised for every message (may be raised from any thread).</summary>
        public static event Action<LogLevel, string> Message;

        public static string FilePath => Path.Combine(AppPaths.Logs, "cardboardbox.log");

        public static void Info(string msg) => Write(LogLevel.Info, msg);
        public static void Success(string msg) => Write(LogLevel.Success, msg);
        public static void Warn(string msg) => Write(LogLevel.Warn, msg);
        public static void Error(string msg) => Write(LogLevel.Error, msg);

        public static void Error(string msg, Exception ex)
        {
            Write(LogLevel.Error, msg + ": " + Errors.Friendly(ex));
            WriteFileOnly("    details: " + ex);
        }

        public static void Write(LogLevel level, string msg)
        {
            WriteFileOnly(level.ToString().ToUpperInvariant() + " " + msg);
            try { Message?.Invoke(level, msg); } catch { /* never let logging crash the app */ }
        }

        public static void WriteFileOnly(string line)
        {
            lock (Gate)
            {
                try
                {
                    Directory.CreateDirectory(AppPaths.Logs);
                    var info = new FileInfo(FilePath);
                    if (info.Exists && info.Length > MaxBytes)
                    {
                        var old = FilePath + ".1";
                        if (File.Exists(old)) File.Delete(old);
                        File.Move(FilePath, old);
                    }
                    File.AppendAllText(FilePath, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " " + line + Environment.NewLine, Encoding.UTF8);
                }
                catch { /* disk full / locked - ignore */ }
            }
        }
    }
}
