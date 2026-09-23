using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using NubeZero.Shared;

namespace NubeZero.Server.Services
{
    public class DiscoveryService : IDisposable
    {
        private readonly int _discoveryPort;
        private readonly int _serverHttpPort;
        private readonly string _serverName;
        private readonly string _magicKey;
        private UdpClient _udpClient;
        private CancellationTokenSource _cts;
        private Task _listenTask;

        public DiscoveryService(int serverHttpPort, string serverName = null, int discoveryPort = DiscoveryConstants.DefaultPort, string magicKey = DiscoveryConstants.DefaultMagicKey)
        {
            _serverHttpPort = serverHttpPort;
            _serverName = string.IsNullOrWhiteSpace(serverName) ? Environment.MachineName : serverName.Trim();
            _discoveryPort = discoveryPort;
            _magicKey = string.IsNullOrWhiteSpace(magicKey) ? DiscoveryConstants.DefaultMagicKey : magicKey.Trim();
        }

        public void Start()
        {
            if (_udpClient != null) return;

            try
            {
                _cts = new CancellationTokenSource();
                _udpClient = new UdpClient();
                // Permitir reutilizar puerto en caso de reinicio rápido y escuchar en todas las interfaces
                _udpClient.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                _udpClient.Client.Bind(new IPEndPoint(IPAddress.Any, _discoveryPort));

                Console.WriteLine($"[Discovery] Servicio activo en UDP {_discoveryPort} (Nombre: '{_serverName}') - Modo reactivo silencioso.");
                _listenTask = Task.Run(ListenLoopAsync);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Discovery] No se pudo iniciar el servicio en UDP {_discoveryPort}: {ex.Message}");
            }
        }

        private async Task ListenLoopAsync()
        {
            while (!_cts.IsCancellationRequested)
            {
                try
                {
#if NETCOREAPP || NET5_0_OR_GREATER || NET10_0_OR_GREATER
                    var result = await _udpClient.ReceiveAsync(_cts.Token);
#else
                    // Para Mono / .NET Framework 4.7.2
                    var result = await _udpClient.ReceiveAsync();
#endif
                    if (result.Buffer == null || result.Buffer.Length == 0)
                        continue;

                    string message = Encoding.UTF8.GetString(result.Buffer);

                    // Seguridad: Validar que contenga la clave mágica exacta
                    // Si es un escaneo de red aleatorio o paquete corrupto, ignoramos en silencio total.
                    if (!message.Contains(_magicKey))
                    {
                        continue;
                    }

                    // Respuesta directa (Unicast) exclusivamente al cliente que preguntó
                    var response = new DiscoveryResponse
                    {
                        ServerName = _serverName,
                        Port = _serverHttpPort,
                        Version = AppVersion.Texto,
                        IpAddress = string.Empty // El cliente usa RemoteEndPoint.Address
                    };

                    string jsonResponse = JsonSerializer.Serialize(response);
                    byte[] responseBytes = Encoding.UTF8.GetBytes(jsonResponse);

                    await _udpClient.SendAsync(responseBytes, responseBytes.Length, result.RemoteEndPoint);
                    Console.WriteLine($"[Discovery] Respondido a cliente en {result.RemoteEndPoint.Address}");
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch (SocketException)
                {
                    if (_cts.IsCancellationRequested) break;
                }
                catch (Exception ex)
                {
                    if (_cts.IsCancellationRequested) break;
                    Console.WriteLine($"[Discovery] Error procesando solicitud: {ex.Message}");
                }
            }
        }

        public void Stop()
        {
            try
            {
                _cts?.Cancel();
                _udpClient?.Close();
                _udpClient?.Dispose();
                _udpClient = null;
            }
            catch { }
        }

        public void Dispose()
        {
            Stop();
        }
    }
}
