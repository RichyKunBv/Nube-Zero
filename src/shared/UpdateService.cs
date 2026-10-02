using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
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

        public static async Task DownloadFileAsync(
            string downloadUrl,
            string destinationPath,
            IProgress<(long bytesRead, long? totalBytes)>? progress = null,
            CancellationToken cancellationToken = default)
        {
            using (var response = await DownloadClient.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken))
            {
                response.EnsureSuccessStatusCode();

                long? totalBytes = response.Content.Headers.ContentLength;

                using (var source = await response.Content.ReadAsStreamAsync())
                using (var destination = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true))
                {
                    byte[] buffer = new byte[81920];
                    long totalRead = 0;
                    int bytesRead;

                    while ((bytesRead = await source.ReadAsync(buffer, 0, buffer.Length, cancellationToken)) > 0)
                    {
                        await destination.WriteAsync(buffer, 0, bytesRead, cancellationToken);
                        totalRead += bytesRead;
                        progress?.Report((totalRead, totalBytes));
                    }
                }
            }
        }

        public static void LaunchInstaller(string installerPath)
        {
            if (string.IsNullOrWhiteSpace(installerPath) || !File.Exists(installerPath))
                throw new FileNotFoundException("El instalador no fue encontrado.", installerPath);

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                Process.Start(new ProcessStartInfo(installerPath)
                {
                    UseShellExecute = true
                });
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                Process.Start(new ProcessStartInfo("open", $"\"{installerPath}\"")
                {
                    UseShellExecute = false
                });
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                try
                {
                    using var chmod = Process.Start("chmod", $"+x \"{installerPath}\"");
                    chmod?.WaitForExit();
                }
                catch
                {
                    // Ignorar si chmod no se ejecuta
                }

                Process.Start(new ProcessStartInfo(installerPath)
                {
                    UseShellExecute = true
                });
            }
            else
            {
                throw new PlatformNotSupportedException("No se puede iniciar el instalador en esta plataforma.");
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
