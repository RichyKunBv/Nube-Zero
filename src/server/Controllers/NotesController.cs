using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Threading.Tasks;
using NubeZero.Server.Data;
using NubeZero.Shared;

namespace NubeZero.Server.Controllers
{
    public class NotesController
    {
        private readonly DatabaseContext _dbContext;

        public NotesController(DatabaseContext dbContext)
        {
            _dbContext = dbContext;
        }

        public async Task HandleGetNotesAsync(HttpListenerContext context)
        {
            var response = context.Response;
            try
            {
                var notas = _dbContext.GetNotas()
                    .Select(n => new NotaDTO
                    {
                        Id = n.Id,
                        Contenido = n.Contenido,
                        CreadoPor = n.CreadoPor,
                        FechaCreacion = n.FechaCreacion
                    })
                    .ToList();

                response.StatusCode = 200;
                response.ContentType = "application/json; charset=utf-8";
                string json = JsonSerializer.Serialize(notas);
                byte[] buffer = System.Text.Encoding.UTF8.GetBytes(json);
                await response.OutputStream.WriteAsync(buffer, 0, buffer.Length);
                response.Close();
            }
            catch (Exception ex)
            {
                await WriteErrorAsync(response, 500, $"Error al obtener notas: {ex.Message}");
            }
        }

        public async Task HandleAddNoteAsync(HttpListenerContext context, string username)
        {
            var request = context.Request;
            var response = context.Response;

            try
            {
                using var reader = new StreamReader(request.InputStream, request.ContentEncoding);
                string body = await reader.ReadToEndAsync();

                string contenido = "";
                if (!string.IsNullOrWhiteSpace(body))
                {
                    try
                    {
                        using var doc = JsonDocument.Parse(body);
                        if (doc.RootElement.TryGetProperty("contenido", out var prop))
                        {
                            contenido = prop.GetString() ?? "";
                        }
                        else if (doc.RootElement.TryGetProperty("content", out var propContent))
                        {
                            contenido = propContent.GetString() ?? "";
                        }
                    }
                    catch
                    {
                        contenido = body.Trim();
                    }
                }

                if (string.IsNullOrWhiteSpace(contenido))
                {
                    await WriteErrorAsync(response, 400, "El contenido de la nota no puede estar vacío.");
                    return;
                }

                var created = _dbContext.AddNota(contenido.Trim(), username);
                var dto = new NotaDTO
                {
                    Id = created.Id,
                    Contenido = created.Contenido,
                    CreadoPor = created.CreadoPor,
                    FechaCreacion = created.FechaCreacion
                };

                response.StatusCode = 201;
                response.ContentType = "application/json; charset=utf-8";
                string json = JsonSerializer.Serialize(dto);
                byte[] buffer = System.Text.Encoding.UTF8.GetBytes(json);
                await response.OutputStream.WriteAsync(buffer, 0, buffer.Length);
                response.Close();
            }
            catch (Exception ex)
            {
                await WriteErrorAsync(response, 500, $"Error al guardar nota: {ex.Message}");
            }
        }

        public async Task HandleDeleteNoteAsync(HttpListenerContext context, string username, string role)
        {
            var request = context.Request;
            var response = context.Response;

            try
            {
                string id = request.QueryString["id"] ?? "";
                if (string.IsNullOrWhiteSpace(id))
                {
                    await WriteErrorAsync(response, 400, "Falta el ID de la nota.");
                    return;
                }

                bool deleted = _dbContext.DeleteNota(id, username, role);
                if (!deleted)
                {
                    await WriteErrorAsync(response, 403, "No se encontró la nota o no tienes permisos para eliminarla.");
                    return;
                }

                response.StatusCode = 200;
                response.ContentType = "application/json; charset=utf-8";
                byte[] buffer = System.Text.Encoding.UTF8.GetBytes("{\"success\": true}");
                await response.OutputStream.WriteAsync(buffer, 0, buffer.Length);
                response.Close();
            }
            catch (Exception ex)
            {
                await WriteErrorAsync(response, 500, $"Error al eliminar nota: {ex.Message}");
            }
        }

        private async Task WriteErrorAsync(HttpListenerResponse response, int statusCode, string message)
        {
            response.StatusCode = statusCode;
            response.ContentType = "application/json; charset=utf-8";
            string json = JsonSerializer.Serialize(new { error = message });
            byte[] buffer = System.Text.Encoding.UTF8.GetBytes(json);
            await response.OutputStream.WriteAsync(buffer, 0, buffer.Length);
            response.Close();
        }
    }
}
