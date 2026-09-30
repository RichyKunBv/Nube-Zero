using System;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Tasks;

namespace NubeZero.Shared
{
    public enum UpdateStatus
    {
        Error,
        Outdated,
        UpToDate,
        Newer
    }

    public static class UpdateService
    {
        private const string LatestReleaseUrl = "https://api.github.com/repos/RichyKunBv/Nube-Zero/releases/latest";
        private static readonly HttpClient Client = CreateClient();
        private static readonly HttpClient DownloadClient = CreateDownloadClient();

        private static HttpClient CreateClient()
        {
            var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("NubeZero-Updater");
            client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            return client;
        }

        private static HttpClient CreateDownloadClient()
        {
            var client = CreateClient();
            client.Timeout = TimeSpan.FromHours(2);
            return client;
        }

        public static string GetCurrentVersion() => AppVersion.Texto;

        public static string GetCurrentAssetName(bool isAndroid = false)
        {
            if (isAndroid) return "NubeZero.apk";

            string architecture = RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "arm64" : "x64";
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return $"NubeZero-{architecture}.exe";
            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) return $"NubeZero-{architecture}.dmg";
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux)) return $"NubeZero-{architecture}.AppImage";

            throw new PlatformNotSupportedException("Esta plataforma no tiene un instalador publicado.");
        }

        public static async Task<(UpdateStatus status, string? latestVersion, string? downloadUrl)> CheckForUpdatesAsync(
            string currentVersion,
            string assetName)
        {
            try
            {
                using (var response = await Client.GetAsync(LatestReleaseUrl))
                {
                    if (!response.IsSuccessStatusCode) return (UpdateStatus.Error, null, null);

                    using (var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync()))
                    {
                        var root = document.RootElement;
                        if (!root.TryGetProperty("tag_name", out var tagElement))
                            return (UpdateStatus.Error, null, null);

                        string latestVersion = (tagElement.GetString() ?? string.Empty).TrimStart('v', 'V');
                        if (!Version.TryParse(currentVersion.TrimStart('v', 'V'), out var current)
                            || !Version.TryParse(latestVersion, out var latest))
                            return (UpdateStatus.Error, null, null);

                        string? downloadUrl = FindAssetUrl(root, assetName);
                        if (current < latest) return (UpdateStatus.Outdated, latestVersion, downloadUrl);
                        if (current > latest) return (UpdateStatus.Newer, latestVersion, null);
                        return (UpdateStatus.UpToDate, latestVersion, null);
                    }
                }
            }
            catch
            {
                return (UpdateStatus.Error, null, null);
            }
        }

        public static async Task DownloadFileAsync(string downloadUrl, string destinationPath)
        {
            using (var response = await DownloadClient.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead))
            {
                response.EnsureSuccessStatusCode();
                using (var source = await response.Content.ReadAsStreamAsync())
                using (var destination = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true))
                {
                    await source.CopyToAsync(destination, 81920);
                }
            }
        }

        private static string? FindAssetUrl(JsonElement release, string assetName)
        {
            if (!release.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
                return null;

            foreach (var asset in assets.EnumerateArray())
            {
                if (asset.TryGetProperty("name", out var name)
                    && string.Equals(name.GetString(), assetName, StringComparison.OrdinalIgnoreCase)
                    && asset.TryGetProperty("browser_download_url", out var url))
                    return url.GetString();
            }

            return null;
        }
    }
}
