using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text.Json;
using System.Threading.Tasks;
using NubeZero.Shared;
using NubeZero.Server.Services;

namespace NubeZero.Server.Controllers
{
    public class FileController
    {
        private readonly StorageService _storageService;
        private readonly NubeZero.Server.Data.DatabaseContext _dbContext;
        
        public FileController(StorageService storageService, NubeZero.Server.Data.DatabaseContext dbContext)
        {
            _storageService = storageService;
            _dbContext = dbContext;
        }

        public async Task HandleListDirectoryAsync(HttpListenerContext context, string relativePath)
        {
            var response = context.Response;
            try
            {
                string safePath = _storageService.GetSafePath(relativePath);

                if (!Directory.Exists(safePath))
                {
                    await WriteErrorAsync(response, 404, "Directorio no encontrado.");
                    return;
                }

                var filesList = new List<ArchivoDTO>();

                // Directorios
                foreach (var dir in Directory.EnumerateDirectories(safePath))
                {
                    var info = new DirectoryInfo(dir);
                    filesList.Add(new ArchivoDTO
                    {
                        Nombre = info.Name,
                        EsCarpeta = true,
                        FechaModificacion = info.LastWriteTimeUtc,
                        PesoBytes = 0,
                        ModificadoPor = "System"
                    });
                }

                // Archivos
                foreach (var file in Directory.EnumerateFiles(safePath))
                {
                    var info = new FileInfo(file);
                    string owner = _dbContext.GetFileOwner(file);
                    filesList.Add(new ArchivoDTO
                    {
                        Nombre = info.Name,
                        EsCarpeta = false,
                        FechaModificacion = info.LastWriteTimeUtc,
                        PesoBytes = info.Length,
                        ModificadoPor = owner
                    });
                }

                response.ContentType = "application/json";
                response.StatusCode = 200;
                await JsonSerializer.SerializeAsync(response.OutputStream, filesList);
            }
            catch (UnauthorizedAccessException)
            {
                await WriteErrorAsync(response, 403, "Acceso denegado.");
            }
            catch (Exception ex)
            {
                await WriteErrorAsync(response, 500, $"Error interno: {ex.Message}");
            }
            finally
            {
                response.Close();
            }
        }

        public async Task HandleDownloadAsync(HttpListenerContext context, string relativePath)
        {
            var response = context.Response;
            try
            {
                string safePath = _storageService.GetSafePath(relativePath);

                if (!File.Exists(safePath))
                {
                    await WriteErrorAsync(response, 404, "Archivo no encontrado.");
                    return;
                }

                var fileInfo = new FileInfo(safePath);
                response.ContentType = "application/octet-stream";
                response.ContentLength64 = fileInfo.Length;
                
                // Forzar que el navegador descargue en lugar de mostrar (útil para pruebas)
                response.AddHeader("Content-Disposition", $"attachment; filename=\"{fileInfo.Name}\"");

                // Stream super eficiente con buffer pequeño adaptado a HW bajo
                using (var fs = new FileStream(safePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true))
                {
                    await fs.CopyToAsync(response.OutputStream, 81920);
                }
            }
            catch (UnauthorizedAccessException)
            {
                await WriteErrorAsync(response, 403, "Acceso denegado.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error descarga: {ex.Message}");
                response.StatusCode = 500;
            }
            finally
            {
                response.Close();
            }
        }

        public async Task HandleUploadAsync(HttpListenerContext context, string relativePath, string username)
        {
            var request = context.Request;
            var response = context.Response;
            try
            {
                if (string.IsNullOrWhiteSpace(relativePath))
                {
                    await WriteErrorAsync(response, 400, "Debe especificar una ruta destino.");
                    return;
                }

                string safePath = _storageService.GetSafePath(relativePath);

                // Asegurar que el directorio padre exista
                string parentDir = Path.GetDirectoryName(safePath);
                if (parentDir != null && !Directory.Exists(parentDir))
                {
                    Directory.CreateDirectory(parentDir);
                }

                long fileLength = request.ContentLength64 > 0 ? request.ContentLength64 : 0;

                // Escribir directamente desde el InputStream al disco
                using (var fs = new FileStream(safePath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true))
                {
                    await request.InputStream.CopyToAsync(fs, 81920);
                    if (fileLength == 0) fileLength = fs.Length;
                }

                _dbContext.SaveFileMetadata(safePath, username, fileLength);

                response.StatusCode = 201; // Created
                response.ContentType = "application/json";
                byte[] buffer = System.Text.Encoding.UTF8.GetBytes("{\"status\":\"ok\"}");
                await response.OutputStream.WriteAsync(buffer, 0, buffer.Length);
            }
            catch (UnauthorizedAccessException)
            {
                await WriteErrorAsync(response, 403, "Acceso denegado.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error subida: {ex.Message}");
                await WriteErrorAsync(response, 500, $"Error interno: {ex.Message}");
            }
            finally
            {
                response.Close();
            }
        }

        public async Task HandleDeleteAsync(HttpListenerContext context, string relativePath)
        {
            var response = context.Response;
            try
            {
                if (string.IsNullOrWhiteSpace(relativePath))
                {
                    await WriteErrorAsync(response, 400, "Debe especificar una ruta.");
                    return;
                }

                string safePath = _storageService.GetSafePath(relativePath);

                if (File.Exists(safePath))
                {
                    File.Delete(safePath);
                    _dbContext.DeleteFileMetadata(safePath);
                }
                else if (Directory.Exists(safePath))
                {
                    Directory.Delete(safePath, true);
                    // Opcional: borrar recursivamente los metadatos de los hijos
                }
                else
                {
                    await WriteErrorAsync(response, 404, "Archivo o directorio no encontrado.");
                    return;
                }

                response.StatusCode = 200;
                response.ContentType = "application/json";
                byte[] buffer = System.Text.Encoding.UTF8.GetBytes("{\"status\":\"deleted\"}");
                await response.OutputStream.WriteAsync(buffer, 0, buffer.Length);
            }
            catch (UnauthorizedAccessException)
            {
                await WriteErrorAsync(response, 403, "Acceso denegado.");
            }
            catch (Exception ex)
            {
                await WriteErrorAsync(response, 500, $"Error al borrar: {ex.Message}");
            }
            finally
            {
                response.Close();
            }
        }

        public async Task HandleCreateFolderAsync(HttpListenerContext context, string relativePath)
        {
            var response = context.Response;
            try
            {
                if (string.IsNullOrWhiteSpace(relativePath))
                {
                    await WriteErrorAsync(response, 400, "Debe especificar una ruta.");
                    return;
                }

                string safePath = _storageService.GetSafePath(relativePath);

                if (!Directory.Exists(safePath))
                {
                    Directory.CreateDirectory(safePath);
                }

                response.StatusCode = 201;
                response.ContentType = "application/json";
                byte[] buffer = System.Text.Encoding.UTF8.GetBytes("{\"status\":\"created\"}");
                await response.OutputStream.WriteAsync(buffer, 0, buffer.Length);
            }
            catch (UnauthorizedAccessException)
            {
                await WriteErrorAsync(response, 403, "Acceso denegado.");
            }
            catch (Exception ex)
            {
                await WriteErrorAsync(response, 500, $"Error al crear carpeta: {ex.Message}");
            }
            finally
            {
                response.Close();
            }
        }

        public async Task HandleRenameAsync(HttpListenerContext context, string relativePath, string newName)
        {
            var response = context.Response;
            try
            {
                if (string.IsNullOrWhiteSpace(relativePath) || string.IsNullOrWhiteSpace(newName))
                {
                    await WriteErrorAsync(response, 400, "Debe especificar ruta original y nuevo nombre.");
                    return;
                }

                string safePath = _storageService.GetSafePath(relativePath);
                string parentDir = Path.GetDirectoryName(safePath);
                
                // Aseguramos que el nuevo nombre no sea un path traversal hack
                if (newName.Contains("/") || newName.Contains("\\"))
                {
                    await WriteErrorAsync(response, 400, "El nuevo nombre no debe contener barras.");
                    return;
                }

                string newSafePath = Path.Combine(parentDir, newName);

                if (File.Exists(safePath))
                {
                    File.Move(safePath, newSafePath);
                    _dbContext.RenameFileMetadata(safePath, newSafePath);
                }
                else if (Directory.Exists(safePath))
                {
                    Directory.Move(safePath, newSafePath);
                }
                else
                {
                    await WriteErrorAsync(response, 404, "Archivo original no encontrado.");
                    return;
                }

                response.StatusCode = 200;
                response.ContentType = "application/json";
                byte[] buffer = System.Text.Encoding.UTF8.GetBytes("{\"status\":\"renamed\"}");
                await response.OutputStream.WriteAsync(buffer, 0, buffer.Length);
            }
            catch (UnauthorizedAccessException)
            {
                await WriteErrorAsync(response, 403, "Acceso denegado.");
            }
            catch (Exception ex)
            {
                await WriteErrorAsync(response, 500, $"Error al renombrar: {ex.Message}");
            }
            finally
            {
                response.Close();
            }
        }

        private async Task WriteErrorAsync(HttpListenerResponse response, int statusCode, string message)
        {
            try
            {
                response.StatusCode = statusCode;
                response.ContentType = "application/json";
                string json = JsonSerializer.Serialize(new { error = message });
                byte[] buffer = System.Text.Encoding.UTF8.GetBytes(json);
                await response.OutputStream.WriteAsync(buffer, 0, buffer.Length);
            }
            catch
            {
                // Ignorar si el canal ya está cerrado
            }
        }
    }
}
