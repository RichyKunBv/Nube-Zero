using System;

namespace NubeZero.Shared
{
    public static class DiscoveryConstants
    {
        public const int DefaultPort = 8088;
        public const string DefaultMagicKey = "NZ_DISCOVER_V1";
    }

    public class DiscoveryRequest
    {
        public string MagicKey { get; set; } = DiscoveryConstants.DefaultMagicKey;
        public string ClientVersion { get; set; } = AppVersion.Texto;
    }

    public class DiscoveryResponse
    {
        public string ServerName { get; set; } = string.Empty;
        public string IpAddress { get; set; } = string.Empty;
        public int Port { get; set; } = 8080;
        public string Version { get; set; } = AppVersion.Texto;

        public override string ToString()
        {
            return $"{ServerName} (http://{IpAddress}:{Port})";
        }
    }
}
