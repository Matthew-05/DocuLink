using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace DocuLink.Addin.Modules.Services
{
    internal sealed class ReleaseNote
    {
        internal string Version { get; }
        internal string Body { get; }
        internal DateTimeOffset? PublishedAt { get; }

        internal ReleaseNote(
            string version,
            string body,
            DateTimeOffset? publishedAt)
        {
            Version = version;
            Body = body;
            PublishedAt = publishedAt;
        }
    }

    internal sealed class UpdateCheckResult
    {
        internal bool UpdateAvailable { get; }
        internal string LatestVersion { get; }
        internal string ReleaseUrl { get; }
        internal string DownloadUrl { get; }
        internal bool IsDevBuild { get; }
        internal IReadOnlyList<ReleaseNote> ReleaseNotes { get; }

        internal UpdateCheckResult(
            bool updateAvailable,
            string latestVersion,
            string releaseUrl,
            string downloadUrl,
            IReadOnlyList<ReleaseNote> releaseNotes,
            bool isDevBuild = false)
        {
            UpdateAvailable = updateAvailable;
            LatestVersion = latestVersion;
            ReleaseUrl = releaseUrl;
            DownloadUrl = downloadUrl;
            IsDevBuild = isDevBuild;
            ReleaseNotes = releaseNotes ?? new List<ReleaseNote>();
        }
    }

    internal static class UpdateCheckService
    {
        private const string ReleasesApiUrl = "https://api.github.com/repos/Matthew-05/DocuLink/releases";
        private const string ReleasesPageUrl = ReleasesApiUrl + "?per_page=100&page=";
        private const string ReleasesUrl = "https://github.com/Matthew-05/DocuLink/releases";

        internal static async Task<UpdateCheckResult> CheckAsync()
        {
            bool isDevBuild = AppVersion.IsDevelopment;
            return await CheckCoreAsync(isDevBuild).ConfigureAwait(false);
        }

        private static async Task<UpdateCheckResult> CheckCoreAsync(bool isDevBuild)
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;

            using (var client = new HttpClient())
            {
                client.DefaultRequestHeaders.UserAgent.ParseAdd("DocuLink-Addin/" + AppVersion.Current);
                client.Timeout = TimeSpan.FromSeconds(10);

                var serializer = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
                var releaseData = await GetAllReleasesAsync(client, serializer).ConfigureAwait(false);
                var releases = new List<ReleaseNote>();
                Dictionary<string, object> latestStable = null;

                foreach (var data in releaseData)
                {
                    if (GetBoolean(data, "draft")) continue;

                    var tagName = GetString(data, "tag_name");
                    var version = tagName.TrimStart('v', 'V');
                    var isPrerelease = GetBoolean(data, "prerelease");
                    var publishedAt = ParsePublishedAt(GetString(data, "published_at"));

                    releases.Add(new ReleaseNote(
                        version,
                        GetString(data, "body"),
                        publishedAt));

                    if (latestStable == null && !isPrerelease && Version.TryParse(version, out _))
                        latestStable = data;
                }

                releases.Sort((left, right) => Nullable.Compare(right.PublishedAt, left.PublishedAt));

                if (latestStable == null) return null;

                var latestVersion = GetString(latestStable, "tag_name").TrimStart('v', 'V');
                var htmlUrl = GetString(latestStable, "html_url");
                if (string.IsNullOrEmpty(htmlUrl)) htmlUrl = ReleasesUrl;
                var downloadUrl = FindMsiDownloadUrl(latestStable);

                if (!Version.TryParse(latestVersion, out var latest)) return null;

                if (isDevBuild)
                    return new UpdateCheckResult(true, latestVersion, htmlUrl, downloadUrl, releases, isDevBuild: true);

                if (!Version.TryParse(AppVersion.Current, out var current)) return null;
                return new UpdateCheckResult(latest > current, latestVersion, htmlUrl, downloadUrl, releases);
            }
        }

        private static async Task<List<Dictionary<string, object>>> GetAllReleasesAsync(
            HttpClient client,
            JavaScriptSerializer serializer)
        {
            var releases = new List<Dictionary<string, object>>();

            for (var page = 1; ; page++)
            {
                var json = await client.GetStringAsync(ReleasesPageUrl + page).ConfigureAwait(false);
                var pageData = serializer.Deserialize<ArrayList>(json);
                if (pageData == null || pageData.Count == 0) break;

                foreach (var item in pageData)
                {
                    if (item is Dictionary<string, object> release)
                        releases.Add(release);
                }

                if (pageData.Count < 100) break;
            }

            return releases;
        }

        private static string FindMsiDownloadUrl(Dictionary<string, object> release)
        {
            if (!release.TryGetValue("assets", out var value) || !(value is ArrayList assets))
                return null;

            foreach (var item in assets)
            {
                if (!(item is Dictionary<string, object> asset)) continue;

                var name = GetString(asset, "name");
                if (name.EndsWith(".msi", StringComparison.OrdinalIgnoreCase))
                    return GetString(asset, "browser_download_url");
            }

            return null;
        }

        private static string GetString(Dictionary<string, object> data, string key)
        {
            return data.TryGetValue(key, out var value) ? value as string ?? string.Empty : string.Empty;
        }

        private static bool GetBoolean(Dictionary<string, object> data, string key)
        {
            return data.TryGetValue(key, out var value) && value is bool boolean && boolean;
        }

        private static DateTimeOffset? ParsePublishedAt(string value)
        {
            return DateTimeOffset.TryParse(value, out var publishedAt)
                ? publishedAt
                : (DateTimeOffset?)null;
        }

        internal static async Task<string> DownloadAsync(string url, string version, IProgress<int> progress, CancellationToken cancellationToken = default)
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;

            using (var client = new HttpClient())
            {
                client.DefaultRequestHeaders.UserAgent.ParseAdd("DocuLink-Addin/" + AppVersion.Current);

                using (var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false))
                {
                    response.EnsureSuccessStatusCode();

                    var total = response.Content.Headers.ContentLength;
                    var localPath = Path.Combine(Path.GetTempPath(), $"DocuLink-Setup-{version}.msi");

                    using (var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                    using (var file = new FileStream(localPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
                    {
                        var buffer = new byte[81920];
                        long downloaded = 0;
                        int read;

                        while ((read = await stream.ReadAsync(buffer, 0, buffer.Length, cancellationToken).ConfigureAwait(false)) > 0)
                        {
                            await file.WriteAsync(buffer, 0, read, cancellationToken).ConfigureAwait(false);
                            downloaded += read;
                            if (total.HasValue && total.Value > 0)
                                progress?.Report((int)(downloaded * 100 / total.Value));
                        }
                    }

                    return localPath;
                }
            }
        }
    }
}
