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
    internal sealed class UpdateCheckResult
    {
        internal bool UpdateAvailable { get; }
        internal string LatestVersion { get; }
        internal string ReleaseUrl { get; }
        internal string DownloadUrl { get; }
        internal bool IsDevBuild { get; }

        internal UpdateCheckResult(bool updateAvailable, string latestVersion, string releaseUrl, string downloadUrl, bool isDevBuild = false)
        {
            UpdateAvailable = updateAvailable;
            LatestVersion = latestVersion;
            ReleaseUrl = releaseUrl;
            DownloadUrl = downloadUrl;
            IsDevBuild = isDevBuild;
        }
    }

    internal static class UpdateCheckService
    {
        private const string ApiUrl = "https://api.github.com/repos/Matthew-05/DocuLink/releases/latest";

        internal static async Task<UpdateCheckResult> CheckAsync()
        {
            bool isDevBuild = AppVersion.Current == "dev";
            return await CheckCoreAsync(isDevBuild).ConfigureAwait(false);
        }

        private static async Task<UpdateCheckResult> CheckCoreAsync(bool isDevBuild)
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;

            using (var client = new HttpClient())
            {
                client.DefaultRequestHeaders.UserAgent.ParseAdd("DocuLink-Addin/" + AppVersion.Current);
                client.Timeout = TimeSpan.FromSeconds(10);

                string json = await client.GetStringAsync(ApiUrl).ConfigureAwait(false);

                var serializer = new JavaScriptSerializer();
                var data = serializer.Deserialize<Dictionary<string, object>>(json);

                var tagName = data["tag_name"] as string ?? "";
                var htmlUrl = data.ContainsKey("html_url") ? data["html_url"] as string : "https://github.com/Matthew-05/DocuLink/releases";
                var latestVersion = tagName.TrimStart('v');

                string downloadUrl = null;
                if (data.ContainsKey("assets") && data["assets"] is ArrayList assets)
                {
                    foreach (var item in assets)
                    {
                        if (!(item is Dictionary<string, object> asset)) continue;
                        var name = asset.ContainsKey("name") ? asset["name"] as string ?? "" : "";
                        if (name.EndsWith(".msi", StringComparison.OrdinalIgnoreCase))
                        {
                            downloadUrl = asset.ContainsKey("browser_download_url") ? asset["browser_download_url"] as string : null;
                            break;
                        }
                    }
                }

                if (!Version.TryParse(latestVersion, out var latest)) return null;

                if (isDevBuild)
                    return new UpdateCheckResult(true, latestVersion, htmlUrl, downloadUrl, isDevBuild: true);

                if (!Version.TryParse(AppVersion.Current, out var current)) return null;
                return new UpdateCheckResult(latest > current, latestVersion, htmlUrl, downloadUrl);
            }
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
