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
            string storagePath = null;
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--port" && i + 1 < args.Length && int.TryParse(args[i + 1], out int p))
                {
                    port = p;
                }
                else if (args[i] == "--storage" && i + 1 < args.Length)
                {
                    storagePath = args[i + 1];
                }
            }

            Console.WriteLine($"Iniciando servidor Nube-Zero en el puerto {port}...");
            
            _storageService = new StorageService(storagePath);
            _dbContext = new DatabaseContext(storagePath);
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
                // Evitar fuga de sockets y threads inactivos forzando el cierre de la conexión TCP
                response.KeepAlive = false;

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

                AuthSession session = null;
                if (request.Url.AbsolutePath.StartsWith("/api/") && request.Url.AbsolutePath != "/api/status")
                {
                    session = _authInterceptor.ValidateSession(request);
                    if (session == null)
                    {
                        await _authInterceptor.WriteUnauthorizedAsync(response);
                        return;
                    }
                }

                // Rutas de administración y usuarios
                if (request.Url.AbsolutePath == "/api/users" && request.HttpMethod == "GET")
                {
                    if (session.Role != "Admin")
                    {
                        await _authInterceptor.WriteForbiddenAsync(response, "Solo los administradores pueden listar usuarios.");
                        return;
                    }
                    await _authController.HandleListUsersAsync(context);
                    return;
                }
                else if (request.Url.AbsolutePath == "/api/users/add" && request.HttpMethod == "POST")
                {
                    if (session.Role != "Admin")
                    {
                        await _authInterceptor.WriteForbiddenAsync(response, "Solo los administradores pueden registrar usuarios.");
                        return;
                    }
                    await _authController.HandleAddUserAsync(context);
                    return;
                }
                else if (request.Url.AbsolutePath == "/api/users/delete" && request.HttpMethod == "DELETE")
                {
                    if (session.Role != "Admin")
                    {
                        await _authInterceptor.WriteForbiddenAsync(response, "Solo los administradores pueden eliminar usuarios.");
                        return;
                    }
                    await _authController.HandleDeleteUserAsync(context, session.Username);
                    return;
                }
                else if (request.Url.AbsolutePath == "/api/users/password" && request.HttpMethod == "POST")
                {
                    await _authController.HandleChangePasswordAsync(context, session.Username, session.Role);
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
                    if (session.Role == "Visitante")
                    {
                        await _authInterceptor.WriteForbiddenAsync(response, "Los visitantes solo tienen permisos de descarga.");
                        return;
                    }
                    await _fileController.HandleUploadAsync(context, reqPath, session.Username);
                }
                else if (request.Url.AbsolutePath == "/api/delete" && request.HttpMethod == "DELETE")
                {
                    if (session.Role == "Visitante")
                    {
                        await _authInterceptor.WriteForbiddenAsync(response, "Los visitantes solo tienen permisos de descarga.");
                        return;
                    }
                    await _fileController.HandleDeleteAsync(context, reqPath);
                }
                else if (request.Url.AbsolutePath == "/api/folder" && request.HttpMethod == "POST")
                {
                    if (session.Role == "Visitante")
                    {
                        await _authInterceptor.WriteForbiddenAsync(response, "Los visitantes solo tienen permisos de descarga.");
                        return;
                    }
                    await _fileController.HandleCreateFolderAsync(context, reqPath);
                }
                else if (request.Url.AbsolutePath == "/api/rename" && request.HttpMethod == "POST")
                {
                    if (session.Role == "Visitante")
                    {
                        await _authInterceptor.WriteForbiddenAsync(response, "Los visitantes solo tienen permisos de descarga.");
                        return;
                    }
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
