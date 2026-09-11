using System;
using System.IO;
using System.Net;
using System.Text.Json;
using System.Threading.Tasks;
using NubeZero.Server.Data;

namespace NubeZero.Server.Controllers
{
    public class AuthController
    {
        private readonly DatabaseContext _dbContext;

        public AuthController(DatabaseContext dbContext)
        {
            _dbContext = dbContext;
        }

        public async Task HandleLoginAsync(HttpListenerContext context)
        {
            var request = context.Request;
            var response = context.Response;

            try
            {
                if (request.HttpMethod != "POST")
                {
                    await WriteErrorAsync(response, 405, "Método no permitido.");
                    return;
                }

                using var reader = new StreamReader(request.InputStream, request.ContentEncoding);
                string jsonBody = await reader.ReadToEndAsync();

                var loginData = JsonSerializer.Deserialize<LoginRequest>(jsonBody);

                if (loginData == null || string.IsNullOrWhiteSpace(loginData.Username) || string.IsNullOrWhiteSpace(loginData.Password))
                {
                    await WriteErrorAsync(response, 400, "Credenciales inválidas.");
                    return;
                }

                string token = _dbContext.CreateSession(loginData.Username, loginData.Password);

                if (token == null)
                {
                    await WriteErrorAsync(response, 401, "Usuario o contraseña incorrectos.");
                    return;
                }

                response.StatusCode = 200;
                response.ContentType = "application/json";
                var result = new { token = token, username = loginData.Username };
                string json = JsonSerializer.Serialize(result);
                byte[] buffer = System.Text.Encoding.UTF8.GetBytes(json);
                await response.OutputStream.WriteAsync(buffer, 0, buffer.Length);
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

        public async Task HandleRegisterAsync(HttpListenerContext context)
        {
            var request = context.Request;
            var response = context.Response;

            try
            {
                if (request.HttpMethod != "POST")
                {
                    await WriteErrorAsync(response, 405, "Método no permitido.");
                    return;
                }

                using var reader = new StreamReader(request.InputStream, request.ContentEncoding);
                string jsonBody = await reader.ReadToEndAsync();

                var registerData = JsonSerializer.Deserialize<LoginRequest>(jsonBody);

                if (registerData == null || string.IsNullOrWhiteSpace(registerData.Username) || string.IsNullOrWhiteSpace(registerData.Password))
                {
                    await WriteErrorAsync(response, 400, "Datos inválidos.");
                    return;
                }

                bool success = _dbContext.AddUser(registerData.Username, registerData.Password);
                if (!success)
                {
                    await WriteErrorAsync(response, 409, "El usuario ya existe.");
                    return;
                }

                response.StatusCode = 201; // Created
                response.ContentType = "application/json";
                byte[] buffer = System.Text.Encoding.UTF8.GetBytes("{\"message\": \"Usuario creado exitosamente\"}");
                await response.OutputStream.WriteAsync(buffer, 0, buffer.Length);
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

        private class LoginRequest
        {
            public string Username { get; set; }
            public string Password { get; set; }
        }
    }
}
