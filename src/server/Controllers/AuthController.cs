using System;
using System.IO;
using System.Net;
using System.Text.Json;
using System.Threading.Tasks;
using NubeZero.Server.Data;
using NubeZero.Shared;

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

                var loginData = JsonSerializer.Deserialize<LoginRequest>(jsonBody, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

                if (loginData == null || string.IsNullOrWhiteSpace(loginData.Username) || string.IsNullOrWhiteSpace(loginData.Password))
                {
                    await WriteErrorAsync(response, 400, "Credenciales inválidas.");
                    return;
                }

                var session = _dbContext.Authenticate(loginData.Username, loginData.Password);

                if (session == null)
                {
                    await WriteErrorAsync(response, 401, "Usuario o contraseña incorrectos.");
                    return;
                }

                response.StatusCode = 200;
                response.ContentType = "application/json";
                var result = new 
                { 
                    token = session.Token, 
                    username = session.Username,
                    role = session.Role
                };
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

        public async Task HandleListUsersAsync(HttpListenerContext context)
        {
            var response = context.Response;
            try
            {
                var users = _dbContext.ListUsers();
                response.StatusCode = 200;
                response.ContentType = "application/json";
                string json = JsonSerializer.Serialize(users);
                byte[] buffer = System.Text.Encoding.UTF8.GetBytes(json);
                await response.OutputStream.WriteAsync(buffer, 0, buffer.Length);
            }
            catch (Exception ex)
            {
                await WriteErrorAsync(response, 500, $"Error al listar usuarios: {ex.Message}");
            }
            finally
            {
                response.Close();
            }
        }

        public async Task HandleAddUserAsync(HttpListenerContext context)
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

                var registerData = JsonSerializer.Deserialize<CreateUserRequest>(jsonBody, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

                if (registerData == null || string.IsNullOrWhiteSpace(registerData.Username) || string.IsNullOrWhiteSpace(registerData.Password))
                {
                    await WriteErrorAsync(response, 400, "Datos inválidos. Se requiere nombre de usuario y contraseña.");
                    return;
                }

                string role = string.IsNullOrWhiteSpace(registerData.Role) ? "Estandar" : registerData.Role;
                bool success = _dbContext.AddUser(registerData.Username, registerData.Password, role);
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

        public async Task HandleDeleteUserAsync(HttpListenerContext context, string currentUsername)
        {
            var request = context.Request;
            var response = context.Response;

            try
            {
                if (request.HttpMethod != "DELETE")
                {
                    await WriteErrorAsync(response, 405, "Método no permitido.");
                    return;
                }

                string targetUser = request.QueryString["username"];
                if (string.IsNullOrWhiteSpace(targetUser))
                {
                    await WriteErrorAsync(response, 400, "Parámetro 'username' requerido.");
                    return;
                }

                if (targetUser.Equals(currentUsername, StringComparison.OrdinalIgnoreCase))
                {
                    await WriteErrorAsync(response, 400, "No puedes eliminar tu propia cuenta en uso.");
                    return;
                }

                bool success = _dbContext.DeleteUser(targetUser, out string error);
                if (!success)
                {
                    await WriteErrorAsync(response, 400, error ?? "No se pudo eliminar el usuario.");
                    return;
                }

                response.StatusCode = 200;
                response.ContentType = "application/json";
                byte[] buffer = System.Text.Encoding.UTF8.GetBytes("{\"message\": \"Usuario eliminado exitosamente\"}");
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

        public async Task HandleChangePasswordAsync(HttpListenerContext context, string loggedInUsername, string loggedInRole)
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

                var passData = JsonSerializer.Deserialize<ChangePasswordRequest>(jsonBody, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (passData == null || string.IsNullOrWhiteSpace(passData.NewPassword))
                {
                    await WriteErrorAsync(response, 400, "Nueva contraseña requerida.");
                    return;
                }

                string targetUser = string.IsNullOrWhiteSpace(passData.TargetUsername) ? loggedInUsername : passData.TargetUsername.Trim();

                // Si cambia su propia contraseña, debe validar la contraseña actual
                if (targetUser.Equals(loggedInUsername, StringComparison.OrdinalIgnoreCase))
                {
                    if (string.IsNullOrWhiteSpace(passData.CurrentPassword) || !_dbContext.ValidatePassword(loggedInUsername, passData.CurrentPassword))
                    {
                        await WriteErrorAsync(response, 401, "La contraseña actual es incorrecta.");
                        return;
                    }
                }
                else
                {
                    // Cambiar contraseña de otro usuario requiere rol Admin
                    if (loggedInRole != "Admin")
                    {
                        await WriteErrorAsync(response, 403, "Solo los administradores pueden cambiar contraseñas de otros usuarios.");
                        return;
                    }
                }

                bool success = _dbContext.ChangePassword(targetUser, passData.NewPassword);
                if (!success)
                {
                    await WriteErrorAsync(response, 400, "No se pudo actualizar la contraseña.");
                    return;
                }

                response.StatusCode = 200;
                response.ContentType = "application/json";
                byte[] buffer = System.Text.Encoding.UTF8.GetBytes("{\"message\": \"Contraseña actualizada exitosamente\"}");
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
            }
        }

        private class LoginRequest
        {
            public string Username { get; set; }
            public string Password { get; set; }
        }

        private class CreateUserRequest
        {
            public string Username { get; set; }
            public string Password { get; set; }
            public string Role { get; set; }
        }

        private class ChangePasswordRequest
        {
            public string CurrentPassword { get; set; }
            public string NewPassword { get; set; }
            public string TargetUsername { get; set; }
        }
    }
}
