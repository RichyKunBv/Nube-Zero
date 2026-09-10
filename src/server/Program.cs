using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using NubeZero.Server.Services;
using NubeZero.Server.Controllers;

namespace NubeZero.Server
{
    class Program
    {
        private static StorageService _storageService;
        private static FileController _fileController;

        static async Task Main(string[] args)
        {
            Console.WriteLine("Iniciando servidor Nube-Zero en Raspberry Pi Zero W...");
            
            _storageService = new StorageService();
            _fileController = new FileController(_storageService);
            
            using (HttpListener listener = new HttpListener())
            {
                listener.Prefixes.Add("http://+:8080/");
                listener.Start();
                Console.WriteLine("Servidor escuchando en el puerto 8080...");
                Console.WriteLine($"Directorio de almacenamiento: {_storageService.BasePath}");

                var cts = new CancellationTokenSource();
                Console.CancelKeyPress += (s, e) =>
                {
                    Console.WriteLine("Apagando servidor...");
                    cts.Cancel();
                    e.Cancel = true;
                };

                try
                {
                    while (!cts.IsCancellationRequested)
                    {
                        var context = await listener.GetContextAsync();
                        _ = Task.Run(() => HandleRequestAsync(context), cts.Token);
                    }
                }
                catch (HttpListenerException) { }
                finally
                {
                    listener.Stop();
                }
            }
        }

        static async Task HandleRequestAsync(HttpListenerContext context)
        {
            var request = context.Request;
            var response = context.Response;

            try
            {
                Console.WriteLine($"[{request.HttpMethod}] {request.Url.AbsolutePath}");
                response.AppendHeader("Access-Control-Allow-Origin", "*");

                // Obtener el path del querystring. Ej: /api/files?path=/mi_foto.jpg
                string reqPath = request.QueryString["path"] ?? "";

                if (request.Url.AbsolutePath == "/api/files" && request.HttpMethod == "GET")
                {
                    await _fileController.HandleListDirectoryAsync(context, reqPath);
                }
                else if (request.Url.AbsolutePath == "/api/download" && request.HttpMethod == "GET")
                {
                    await _fileController.HandleDownloadAsync(context, reqPath);
                }
                else if (request.Url.AbsolutePath == "/api/upload" && request.HttpMethod == "POST")
                {
                    await _fileController.HandleUploadAsync(context, reqPath);
                }
                else if (request.Url.AbsolutePath == "/api/delete" && request.HttpMethod == "DELETE")
                {
                    await _fileController.HandleDeleteAsync(context, reqPath);
                }
                else if (request.Url.AbsolutePath == "/api/folder" && request.HttpMethod == "POST")
                {
                    await _fileController.HandleCreateFolderAsync(context, reqPath);
                }
                else if (request.Url.AbsolutePath == "/api/rename" && request.HttpMethod == "POST")
                {
                    string newName = request.QueryString["newname"] ?? "";
                    await _fileController.HandleRenameAsync(context, reqPath, newName);
                }
                else if (request.Url.AbsolutePath == "/api/status" && request.HttpMethod == "GET")
                {
                    response.ContentType = "application/json";
                    response.StatusCode = 200;
                    byte[] buffer = System.Text.Encoding.UTF8.GetBytes("{\"status\": \"online\", \"version\": \"1.0\"}");
                    await response.OutputStream.WriteAsync(buffer, 0, buffer.Length);
                    response.Close();
                }
                else
                {
                    response.StatusCode = 404;
                    byte[] buffer = System.Text.Encoding.UTF8.GetBytes("{\"error\": \"Not Found\"}");
                    await response.OutputStream.WriteAsync(buffer, 0, buffer.Length);
                    response.Close();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error general: {ex.Message}");
                context.Response.Abort();
            }
        }
    }
}
