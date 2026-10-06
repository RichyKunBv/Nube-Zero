using System;
using System.IO;
using System.Text.Json;

namespace NubeZero.Desktop;

internal sealed class ServerConnectionSettings
{
    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "NubeZero",
        "connection.json");

    public string ServerUrl { get; set; } = string.Empty;
    public string CertificateFingerprint { get; set; } = string.Empty;

    internal static ServerConnectionSettings? Load()
    {
        if (!File.Exists(SettingsPath)) return null;
        return JsonSerializer.Deserialize<ServerConnectionSettings>(File.ReadAllText(SettingsPath));
    }

    internal static void Save(string serverUrl, string certificateFingerprint)
    {
        string directory = Path.GetDirectoryName(SettingsPath)!;
        Directory.CreateDirectory(directory);

        string temporaryPath = SettingsPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var settings = new ServerConnectionSettings
            {
                ServerUrl = serverUrl,
                CertificateFingerprint = certificateFingerprint
            };
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(settings));
            File.Move(temporaryPath, SettingsPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }
}
