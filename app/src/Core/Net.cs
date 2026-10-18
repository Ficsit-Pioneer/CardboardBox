using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace CardboardBox.Core
{
    /// <summary>Progress report for long downloads.</summary>
    public delegate void ProgressHandler(string what, long done, long total);

    /// <summary>HTTP with retries, timeouts and checksum-verified downloads.</summary>
    public static class Net
    {
        private static readonly HttpClient Client = CreateClient();

        public static string UserAgent => "CardboardBox/" + AppPaths.AppVersionText + " (+https://github.com/" + Settings.DefaultOwner + "/" + Settings.DefaultRepo + ")";

        private static HttpClient CreateClient()
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12 | (SecurityProtocolType)12288 /* Tls13 */;
            ServicePointManager.DefaultConnectionLimit = 8;
            var handler = new HttpClientHandler
            {
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
                AllowAutoRedirect = true,
            };
            var c = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
            c.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
            return c;
        }

        public class Response
        {
            public HttpStatusCode Status;
            public string Body;
            public HttpResponseHeaders Headers;
            public bool Ok => (int)Status >= 200 && (int)Status < 300;
        }

        /// <summary>Sends a request and returns status + body. Retries network failures and 5xx errors.</summary>
        public static async Task<Response> SendAsync(HttpMethod method, string url, IDictionary<string, string> headers = null,
            string jsonBody = null, int timeoutSeconds = 30, int attempts = 3)
        {
            Exception last = null;
            for (int attempt = 1; attempt <= attempts; attempt++)
            {
                try
                {
                    using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds)))
                    using (var req = new HttpRequestMessage(method, url))
                    {
                        if (headers != null)
                            foreach (var h in headers) req.Headers.TryAddWithoutValidation(h.Key, h.Value);
                        if (jsonBody != null)
                            req.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
                        using (var resp = await Client.SendAsync(req, cts.Token).ConfigureAwait(false))
                        {
                            var body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                            var r = new Response { Status = resp.StatusCode, Body = body, Headers = resp.Headers };
                            if ((int)resp.StatusCode >= 500 && attempt < attempts)
                            {
                                last = new HttpRequestException("Server error " + (int)resp.StatusCode + " from " + new Uri(url).Host);
                                await Task.Delay(Backoff(attempt)).ConfigureAwait(false);
                                continue;
                            }
                            return r;
                        }
                    }
                }
                catch (Exception ex) when (ex is HttpRequestException || ex is TaskCanceledException || ex is IOException || ex is WebException)
                {
                    last = ex;
                    if (attempt < attempts) await Task.Delay(Backoff(attempt)).ConfigureAwait(false);
                }
            }
            throw new FriendlyException("Couldn't reach " + SafeHost(url) + ".", "Check the internet connection. (" + Errors.Friendly(last) + ")", last);
        }

        public static async Task<string> GetStringAsync(string url, IDictionary<string, string> headers = null, int timeoutSeconds = 30)
        {
            var r = await SendAsync(HttpMethod.Get, url, headers, null, timeoutSeconds).ConfigureAwait(false);
            if (!r.Ok)
                throw new FriendlyException(SafeHost(url) + " answered with error " + (int)r.Status + ".", null);
            return r.Body;
        }

        /// <summary>
        /// Downloads a file to <paramref name="destination"/>. Writes to a .part file first and only
        /// moves it into place once the checksum (sha256 / sha512 / sha1, if given) matches.
        /// </summary>
        public static async Task DownloadFileAsync(string url, string destination, string what,
            ProgressHandler progress = null, string sha256 = null, string sha512 = null, string sha1 = null,
            CancellationToken token = default(CancellationToken), string md5 = null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destination));
            var part = destination + ".part";
            Exception last = null;

            for (int attempt = 1; attempt <= 3; attempt++)
            {
                try
                {
                    using (var resp = await Client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false))
                    {
                        if (!resp.IsSuccessStatusCode)
                            throw new HttpRequestException("HTTP " + (int)resp.StatusCode + " downloading " + what);
                        long total = resp.Content.Headers.ContentLength ?? -1;
                        using (var src = await resp.Content.ReadAsStreamAsync().ConfigureAwait(false))
                        using (var dst = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true))
                        {
                            var buffer = new byte[81920];
                            long done = 0;
                            int read;
                            var lastReport = DateTime.MinValue;
                            // Abort if the connection stalls for 60 seconds.
                            while (true)
                            {
                                var readTask = src.ReadAsync(buffer, 0, buffer.Length, token);
                                if (await Task.WhenAny(readTask, Task.Delay(TimeSpan.FromSeconds(60), token)).ConfigureAwait(false) != readTask)
                                    throw new TimeoutException("Download of " + what + " stalled.");
                                read = await readTask.ConfigureAwait(false);
                                if (read <= 0) break;
                                await dst.WriteAsync(buffer, 0, read, token).ConfigureAwait(false);
                                done += read;
                                if (progress != null && (DateTime.UtcNow - lastReport).TotalMilliseconds > 200)
                                {
                                    lastReport = DateTime.UtcNow;
                                    progress(what, done, total);
                                }
                            }
                            progress?.Invoke(what, done, total);
                        }
                    }

                    VerifyChecksum(part, what, sha256, sha512, sha1, md5);
                    if (File.Exists(destination)) File.Delete(destination);
                    File.Move(part, destination);
                    return;
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    TryDelete(part);
                    throw;
                }
                catch (Exception ex) when (!(ex is FriendlyException))
                {
                    last = ex;
                    TryDelete(part);
                    if (attempt < 3)
                    {
                        Log.Warn("Download of " + what + " failed (attempt " + attempt + "/3), retrying: " + Errors.Friendly(ex));
                        await Task.Delay(Backoff(attempt), token).ConfigureAwait(false);
                    }
                }
            }
            throw new FriendlyException("Couldn't download " + what + ".", Errors.Friendly(last), last);
        }

        private static void VerifyChecksum(string file, string what, string sha256, string sha512, string sha1, string md5)
        {
            string expected = null, actual = null;
            if (!string.IsNullOrEmpty(sha256)) { expected = sha256; actual = Hash.FileSha256(file); }
            else if (!string.IsNullOrEmpty(sha512)) { expected = sha512; actual = Hash.OfFile(file, SHA512.Create()); }
            else if (!string.IsNullOrEmpty(sha1)) { expected = sha1; actual = Hash.OfFile(file, SHA1.Create()); }
            else if (!string.IsNullOrEmpty(md5)) { expected = md5; actual = Hash.OfFile(file, MD5.Create()); }
            if (expected == null) return;
            if (!string.Equals(expected.Trim(), actual, StringComparison.OrdinalIgnoreCase))
            {
                TryDelete(file);
                throw new FriendlyException("The download of " + what + " was corrupted (checksum mismatch).", "It will be retried next time.");
            }
        }

        public static string SafeHost(string url)
        {
            try { return new Uri(url).Host; } catch { return url; }
        }

        private static TimeSpan Backoff(int attempt) => TimeSpan.FromSeconds(Math.Min(30, 2 * Math.Pow(2, attempt - 1)));

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }
    }

    public static class Hash
    {
        public static string FileSha256(string path) => OfFile(path, SHA256.Create());

        public static string OfFile(string path, HashAlgorithm algo)
        {
            using (algo)
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                return ToHex(algo.ComputeHash(fs));
        }

        public static string Bytes(byte[] data)
        {
            using (var sha = SHA256.Create()) return ToHex(sha.ComputeHash(data));
        }

        private static string ToHex(byte[] bytes)
        {
            var sb = new StringBuilder(bytes.Length * 2);
            foreach (var b in bytes) sb.Append(b.ToString("x2"));
            return sb.ToString();
        }
    }
}
