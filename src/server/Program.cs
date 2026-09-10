using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace NubeZero.Server
{
    class Program
    {
        static async Task Main(string[] args)
        {
            Console.WriteLine("Iniciando servidor Nube-Zero en Raspberry Pi Zero W...");
            
            using (HttpListener listener = new HttpListener())
            {
                // Usar "+" en Windows o "*" en Linux/Mono para bindear a todas las interfaces
                listener.Prefixes.Add("http://+:8080/");
                listener.Start();
                Console.WriteLine("Servidor escuchando en el puerto 8080...");

                var cts = new CancellationTokenSource();
                Console.CancelKeyPress += (s, e) =>
                {
                    Console.WriteLine("Apagando servidor...");
                    cts.Cancel();
                    e.Cancel = true; // Previene que el proceso muera inmediatamente
                };

                try
                {
                    while (!cts.IsCancellationRequested)
                    {
                        // Esperar asíncronamente la siguiente conexión
                        var context = await listener.GetContextAsync();
                        
                        // Derivar a un Task sin bloquear el hilo principal
                        _ = Task.Run(() => HandleRequestAsync(context), cts.Token);
                    }
                }
                catch (HttpListenerException)
                {
                    // Ocurre al cancelar/cerrar el listener
                }
                finally
                {
                    listener.Stop();
                }
            }
        }

        static async Task HandleRequestAsync(HttpListenerContext context)
        {
            try
            {
                var request = context.Request;
                var response = context.Response;

                Console.WriteLine($"[{request.HttpMethod}] {request.Url.AbsolutePath}");

                response.ContentType = "application/json";
                response.AppendHeader("Access-Control-Allow-Origin", "*");

                if (request.HttpMethod == "GET" && request.Url.AbsolutePath == "/api/status")
                {
                    string jsonResponse = "{\"status\": \"online\", \"message\": \"Nube-Zero Server corriendo\"}";
                    byte[] buffer = System.Text.Encoding.UTF8.GetBytes(jsonResponse);
                    
                    response.StatusCode = 200;
                    response.ContentLength64 = buffer.Length;
                    
                    using (var output = response.OutputStream)
                    {
                        await output.WriteAsync(buffer, 0, buffer.Length);
                    }
                }
                else
                {
                    response.StatusCode = 404;
                    using (var output = response.OutputStream)
                    {
                        byte[] buffer = System.Text.Encoding.UTF8.GetBytes("{\"error\": \"Not Found\"}");
                        await output.WriteAsync(buffer, 0, buffer.Length);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error manejando la petición: {ex.Message}");
                // No intentamos escribir en la respuesta si ya falló, el socket podría estar cerrado
            }
            finally
            {
                context.Response.Close();
            }
        }
    }
}
