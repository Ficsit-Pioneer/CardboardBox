using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading.Tasks;

namespace CardboardBox.Core
{
    /// <summary>An error with a message that is safe to show to a non-technical person.</summary>
    public class FriendlyException : Exception
    {
        public string Hint { get; }

        public FriendlyException(string message, string hint = null, Exception inner = null)
            : base(message, inner)
        {
            Hint = hint;
        }
    }

    public static class Errors
    {
        /// <summary>Turns any exception into one plain-English sentence (plus a hint when we have one).</summary>
        public static string Friendly(Exception ex)
        {
            if (ex == null) return "Unknown error";
            if (ex is AggregateException agg && agg.InnerExceptions.Count == 1) ex = agg.InnerException;

            if (ex is FriendlyException fe)
                return string.IsNullOrEmpty(fe.Hint) ? fe.Message : fe.Message + " " + fe.Hint;

            var root = ex;
            while (root.InnerException != null) root = root.InnerException;

            if (root is SocketException || root is WebException || ex is HttpRequestException)
                return "Couldn't reach the internet (" + root.Message.TrimEnd('.') + "). Check the PC's connection and try again.";
            if (ex is TaskCanceledException || ex is OperationCanceledException || ex is TimeoutException)
                return "The connection timed out. The internet may be slow or down - CardboardBox will try again later.";
            if (ex is UnauthorizedAccessException)
                return "Windows blocked access to a file (" + ex.Message.TrimEnd('.') + "). Make sure no other program is using the server folder.";
            if (ex is IOException io && (io.HResult & 0xFFFF) == 112)
                return "The disk is full. Free up some space and try again.";
            if (ex is IOException)
                return "A file problem happened: " + ex.Message;
            if (ex is OutOfMemoryException)
                return "This PC ran out of memory.";
            return ex.Message;
        }
    }
}
