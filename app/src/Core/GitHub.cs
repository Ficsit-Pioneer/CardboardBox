using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace CardboardBox.Core
{
    public class CommitInfo
    {
        public string Sha;
        public string Message;
        public string Date;
        public string ShortSha => Sha != null && Sha.Length >= 7 ? Sha.Substring(0, 7) : Sha;
    }

    public class ReleaseInfo
    {
        public string Tag;
        public Version Version;
        public string ExeUrl;
        public string PageUrl;
        public string Notes;
    }

    /// <summary>Talks to the public GitHub repo: reads the latest commit, downloads it, and (with a token) pushes changes.</summary>
    public class GitHub
    {
        private readonly Settings _settings;

        public GitHub(Settings settings) { _settings = settings; }

        private string ApiBase => "https://api.github.com/repos/" + _settings.RepoOwner + "/" + _settings.RepoName;

        private Dictionary<string, string> Headers(bool requireToken = false)
        {
            var h = new Dictionary<string, string>
            {
                ["Accept"] = "application/vnd.github+json",
                ["X-GitHub-Api-Version"] = "2022-11-28",
            };
            var token = _settings.GitHubToken;
            if (!string.IsNullOrEmpty(token)) h["Authorization"] = "Bearer " + token;
            else if (requireToken)
                throw new FriendlyException("A GitHub token is needed to push changes.", "Add one on the GitHub page of CardboardBox.");
            return h;
        }

        /// <summary>
        /// Gets the newest commit on the branch. The commit id comes from git's own endpoint, which is
        /// always fresh and has no rate limit (the REST API caches anonymous answers for ~60 s).
        /// The REST API is then only asked for the message of a commit we haven't seen yet.
        /// </summary>
        public async Task<CommitInfo> GetLatestCommitAsync(CommitInfo known = null)
        {
            var sha = await LsRemoteAsync().ConfigureAwait(false);
            if (known != null && known.Sha == sha) return known;

            var info = new CommitInfo { Sha = sha, Message = "" };
            try
            {
                var r = await Net.SendAsync(HttpMethod.Get, ApiBase + "/commits/" + sha, Headers(), attempts: 1).ConfigureAwait(false);
                if (r.Ok)
                {
                    var commit = Json.ParseObject(r.Body).Obj("commit");
                    info.Message = (commit.Str("message") ?? "").Split('\n')[0].Trim();
                    info.Date = commit.Obj("committer").Str("date") ?? commit.Obj("author").Str("date");
                }
            }
            catch (Exception ex)
            {
                Log.WriteFileOnly("INFO commit details unavailable: " + ex.Message); // only cosmetic
            }
            return info;
        }

        /// <summary>Same as "git ls-remote": asks GitHub which commit the branch points at.</summary>
        private async Task<string> LsRemoteAsync()
        {
            var url = "https://github.com/" + _settings.RepoSlug + ".git/info/refs?service=git-upload-pack";
            var r = await Net.SendAsync(HttpMethod.Get, url).ConfigureAwait(false);
            if (r.Status == HttpStatusCode.Unauthorized || r.Status == HttpStatusCode.NotFound)
                throw new FriendlyException("The GitHub repo " + _settings.RepoSlug + " wasn't found.",
                    "Check the repo name on the GitHub page, and make sure the repo is public.");
            if (!r.Ok)
                throw new FriendlyException("GitHub answered with error " + (int)r.Status + " for " + _settings.RepoSlug + ".", "It will be retried shortly.");
            var m = Regex.Match(r.Body, "([0-9a-f]{40}) refs/heads/" + Regex.Escape(_settings.Branch) + "(\\s|\\x00|$)");
            if (!m.Success)
                throw new FriendlyException("The branch \"" + _settings.Branch + "\" doesn't exist in " + _settings.RepoSlug + ".",
                    "Check the branch name on the GitHub page (usually \"main\").");
            return m.Groups[1].Value;
        }

        /// <summary>Downloads the repo at <paramref name="sha"/> and unpacks it into cache\repo.</summary>
        public async Task DownloadSnapshotAsync(string sha, ProgressHandler progress)
        {
            var zip = Path.Combine(AppPaths.Cache, "repo-" + sha + ".zip");
            var unpack = Path.Combine(AppPaths.Cache, "repo-new");
            var url = "https://codeload.github.com/" + _settings.RepoSlug + "/zip/" + sha;
            await Net.DownloadFileAsync(url, zip, "server files from GitHub", progress).ConfigureAwait(false);

            try
            {
                FileUtil.DeleteDirectory(unpack);
                ZipFile.ExtractToDirectory(zip, unpack);
                // GitHub zips contain one top folder named "<repo>-<sha>".
                var dirs = Directory.GetDirectories(unpack);
                var top = dirs.Length == 1 && Directory.GetFiles(unpack).Length == 0 ? dirs[0] : unpack;
                FileUtil.DeleteDirectory(AppPaths.RepoSnapshot);
                Directory.Move(top, AppPaths.RepoSnapshot);
            }
            catch (InvalidDataException ex)
            {
                throw new FriendlyException("The download from GitHub was damaged.", "It will be retried next time.", ex);
            }
            finally
            {
                FileUtil.DeleteDirectory(unpack);
                try { File.Delete(zip); } catch { }
            }
        }

        /// <summary>Latest published release of the app (for self-update). Returns null if there is none.</summary>
        public async Task<ReleaseInfo> GetLatestReleaseAsync()
        {
            var r = await Net.SendAsync(HttpMethod.Get, ApiBase + "/releases/latest", Headers()).ConfigureAwait(false);
            if (r.Status == HttpStatusCode.NotFound) return null;
            if (!r.Ok) throw new FriendlyException("Couldn't check for CardboardBox updates (GitHub error " + (int)r.Status + ").");
            var d = Json.ParseObject(r.Body);
            var tag = d.Str("tag_name") ?? "";
            Version.TryParse(tag.TrimStart('v', 'V'), out var v);
            string exe = null;
            foreach (var a in d.Arr("assets"))
            {
                var asset = a as Dictionary<string, object>;
                var name = asset.Str("name") ?? "";
                if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) { exe = asset.Str("browser_download_url"); break; }
            }
            return new ReleaseInfo { Tag = tag, Version = v, ExeUrl = exe, PageUrl = d.Str("html_url"), Notes = d.Str("body") };
        }

        /// <summary>Reads one file from the repo as it is right now (null if it doesn't exist).</summary>
        public async Task<byte[]> GetFileAsync(string repoPath)
        {
            var url = ApiBase + "/contents/" + string.Join("/", repoPath.Split('/').Select(Uri.EscapeDataString)) + "?ref=" + Uri.EscapeDataString(_settings.Branch);
            var r = await Net.SendAsync(HttpMethod.Get, url, Headers()).ConfigureAwait(false);
            if (r.Status == HttpStatusCode.NotFound) return null;
            if (!r.Ok) ThrowPushError(r);
            var content = Json.ParseObject(r.Body).Str("content") ?? "";
            return Convert.FromBase64String(content.Replace("\n", "").Replace("\r", ""));
        }

        /// <summary>Creates or replaces one file in the repo (needs a token with write access).</summary>
        public async Task PutFileAsync(string repoPath, byte[] content, string message)
        {
            var headers = Headers(requireToken: true);
            var url = ApiBase + "/contents/" + string.Join("/", repoPath.Split('/').Select(Uri.EscapeDataString));

            string existingSha = null;
            var get = await Net.SendAsync(HttpMethod.Get, url + "?ref=" + Uri.EscapeDataString(_settings.Branch), headers).ConfigureAwait(false);
            if (get.Ok) existingSha = Json.ParseObject(get.Body).Str("sha");
            else if (get.Status != HttpStatusCode.NotFound) ThrowPushError(get);

            var body = new Dictionary<string, object>
            {
                ["message"] = message,
                ["content"] = Convert.ToBase64String(content),
                ["branch"] = _settings.Branch,
            };
            if (existingSha != null) body["sha"] = existingSha;

            var put = await Net.SendAsync(HttpMethod.Put, url, headers, Json.Write(body), timeoutSeconds: 120, attempts: 1).ConfigureAwait(false);
            if (!put.Ok) ThrowPushError(put);
        }

        private void ThrowPushError(Net.Response r)
        {
            string detail = null;
            try { detail = Json.ParseObject(r.Body).Str("message"); } catch { }
            if (r.Status == HttpStatusCode.Unauthorized)
                throw new FriendlyException("GitHub rejected the token.", "Create a new token and paste it on the GitHub page.");
            if (r.Status == HttpStatusCode.Forbidden || r.Status == HttpStatusCode.NotFound)
                throw new FriendlyException("This token isn't allowed to change " + _settings.RepoSlug + ".",
                    "The token needs \"Contents: Read and write\" permission for that repo. (" + detail + ")");
            throw new FriendlyException("GitHub refused the change (error " + (int)r.Status + ").", detail);
        }
    }

    public static class FileUtil
    {
        public static void DeleteDirectory(string path)
        {
            if (!Directory.Exists(path)) return;
            // Minecraft library folders can go past Windows' old 260-character limit;
            // the "\\?\" prefix lets Windows handle long paths.
            var full = Path.GetFullPath(path);
            if (!full.StartsWith(@"\\")) path = @"\\?\" + full;
            for (int i = 0; i < 3; i++)
            {
                try
                {
                    foreach (var f in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                        File.SetAttributes(f, FileAttributes.Normal);
                    Directory.Delete(path, true);
                    return;
                }
                catch (IOException) when (i < 2) { System.Threading.Thread.Sleep(300); }
                catch (UnauthorizedAccessException) when (i < 2) { System.Threading.Thread.Sleep(300); }
            }
        }

        public static string Relative(string root, string full)
        {
            root = root.TrimEnd('\\', '/') + "\\";
            return full.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? full.Substring(root.Length) : full;
        }

        public static string FormatBytes(long bytes)
        {
            if (bytes < 1024) return bytes + " B";
            double v = bytes;
            string[] units = { "KB", "MB", "GB", "TB" };
            int u = -1;
            do { v /= 1024; u++; } while (v >= 1024 && u < units.Length - 1);
            return v.ToString(v >= 100 ? "0" : v >= 10 ? "0.0" : "0.00") + " " + units[u];
        }
    }
}
