using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace NubeZero.Shared
{
    public static class NetworkDiscoveryClient
    {
        public static async Task<List<DiscoveryResponse>> DiscoverServersAsync(
            int timeoutMs = 1500, 
            int discoveryPort = DiscoveryConstants.DefaultPort, 
            string magicKey = DiscoveryConstants.DefaultMagicKey)
        {
            var servers = new List<DiscoveryResponse>();
            var seen = new HashSet<string>();

            using (var udpClient = new UdpClient())
            {
                try
                {
                    udpClient.EnableBroadcast = true;
                    udpClient.Client.ReceiveTimeout = timeoutMs;

                    var request = new DiscoveryRequest
                    {
                        MagicKey = magicKey,
                        ClientVersion = AppVersion.Texto
                    };
                    string jsonRequest = JsonSerializer.Serialize(request);
                    byte[] requestBytes = Encoding.UTF8.GetBytes(jsonRequest);

                    var broadcastEndpoint = new IPEndPoint(IPAddress.Broadcast, discoveryPort);
                    await udpClient.SendAsync(requestBytes, requestBytes.Length, broadcastEndpoint);

                    using (var cts = new CancellationTokenSource(timeoutMs))
                    {
                        while (!cts.IsCancellationRequested)
                        {
                            try
                            {
                                var receiveTask = udpClient.ReceiveAsync();
                                var completedTask = await Task.WhenAny(receiveTask, Task.Delay(timeoutMs, cts.Token));

                                if (completedTask != receiveTask)
                                {
                                    break; // Tiempo de espera agotado
                                }

                                var result = await receiveTask;
                                if (result.Buffer == null || result.Buffer.Length == 0) continue;

                                string responseJson = Encoding.UTF8.GetString(result.Buffer);
                                var response = JsonSerializer.Deserialize<DiscoveryResponse>(responseJson);

                                if (response != null)
                                {
                                    string serverIp = result.RemoteEndPoint.Address.ToString();
                                    if (result.RemoteEndPoint.Address.IsIPv4MappedToIPv6)
                                    {
                                        serverIp = result.RemoteEndPoint.Address.MapToIPv4().ToString();
                                    }

                                    response.IpAddress = serverIp;
                                    string key = $"{serverIp}:{response.Port}";
                                    if (!seen.Contains(key))
                                    {
                                        seen.Add(key);
                                        servers.Add(response);
                                    }
                                }
                            }
                            catch (OperationCanceledException)
                            {
                                break;
                            }
                            catch
                            {
                                // Ignorar paquetes ajenos o no válidos
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[DiscoveryClient] Error durante búsqueda: {ex.Message}");
                }
            }

            return servers;
        }
    }
}
