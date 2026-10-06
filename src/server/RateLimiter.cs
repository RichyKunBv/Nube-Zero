using System;
using System.Collections.Concurrent;
using System.Net;

namespace NubeZero.Server
{
    public static class RateLimiter
    {
        private static readonly ConcurrentDictionary<IPAddress, ClientInfo> _clients = new();
        private const int Limit = 10; // max requests per window
        private static readonly TimeSpan Window = TimeSpan.FromSeconds(1);

        private class ClientInfo
        {
            public int Count;
            public DateTime WindowStart;
        }

        public static bool Allow(IPAddress ip)
        {
            var now = DateTime.UtcNow;
            var client = _clients.GetOrAdd(ip, _ => new ClientInfo { Count = 0, WindowStart = now });

            // Reset window if elapsed
            if (now - client.WindowStart > Window)
            {
                client.Count = 0;
                client.WindowStart = now;
            }

            if (client.Count >= Limit)
            {
                return false;
            }

            client.Count++;
            return true;
        }
    }
}
