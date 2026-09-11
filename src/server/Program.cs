using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using NubeZero.Server.Services;
using NubeZero.Server.Controllers;
using NubeZero.Server.Data;
using NubeZero.Server.Auth;

namespace NubeZero.Server
{
    class Program
    {
        private static StorageService _storageService;
        private static FileController _fileController;
        private static DatabaseContext _dbContext;
        private static AuthController _authController;
        private static AuthInterceptor _authInterceptor;

        static async Task Main(string[] args)
        {
            int port = 8080;
            if (args.Length >= 2 && args[0] == "--port" && int.TryParse(args[1], out int p))
            {
                port = p;
            }

            Console.WriteLine($"Iniciando servidor Nube-Zero en el puerto {port}...");
            
            _storageService = new StorageService();
            _dbContext = new DatabaseContext();
            _authController = new AuthController(_dbContext);
            _authInterceptor = new AuthInterceptor(_dbContext);
            _fileController = new FileController(_storageService, _dbContext);
            
            using (HttpListener listener = new HttpListener())
            {
                listener.Prefixes.Add($"http://+:{port}/");
                listener.Start();
                Console.WriteLine($"Servidor escuchando en el puerto {port}...");
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
                
                // Manejo de pre-flight CORS
                if (request.HttpMethod == "OPTIONS")
                {
                    response.AppendHeader("Access-Control-Allow-Methods", "GET, POST, DELETE, OPTIONS");
                    response.AppendHeader("Access-Control-Allow-Headers", "Content-Type, Authorization");
                    response.StatusCode = 204;
                    response.Close();
                    return;
                }

                if (request.Url.AbsolutePath == "/api/login")
                {
                    await _authController.HandleLoginAsync(context);
                    return;
                }

                string username = null;
                if (request.Url.AbsolutePath.StartsWith("/api/") && request.Url.AbsolutePath != "/api/status")
                {
                    username = _authInterceptor.ValidateRequest(request);
                    if (username == null)
                    {
                        await _authInterceptor.WriteUnauthorizedAsync(response);
                        return;
                    }
                }

                if (request.Url.AbsolutePath == "/api/users/add")
                {
                    await _authController.HandleRegisterAsync(context);
                    return;
                }

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
                    await _fileController.HandleUploadAsync(context, reqPath, username);
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
                    byte[] buffer = System.Text.Encoding.UTF8.GetBytes($"{{\"status\": \"online\", \"version\": \"{NubeZero.Shared.AppVersion.Texto}\"}}");
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
