using System;
using System.Net;
using System.Threading.Tasks;
using NubeZero.Server.Data;

namespace NubeZero.Server.Auth
{
    public class AuthInterceptor
    {
        private readonly DatabaseContext _dbContext;

        public AuthInterceptor(DatabaseContext dbContext)
        {
            _dbContext = dbContext;
        }

        /// <summary>
        /// Valida la petición entrante. Devuelve el nombre de usuario si es válido, de lo contrario null.
        /// </summary>
        public string ValidateRequest(HttpListenerRequest request)
        {
            string authHeader = request.Headers["Authorization"];
            if (string.IsNullOrWhiteSpace(authHeader) || !authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            string token = authHeader.Substring("Bearer ".Length).Trim();
            
            return _dbContext.ValidateTokenAndGetUser(token);
        }

        public async Task WriteUnauthorizedAsync(HttpListenerResponse response)
        {
            try
            {
                response.StatusCode = 401;
                response.ContentType = "application/json";
                byte[] buffer = System.Text.Encoding.UTF8.GetBytes("{\"error\":\"No autorizado. Token inválido o expirado.\"}");
                await response.OutputStream.WriteAsync(buffer, 0, buffer.Length);
            }
            catch
            {
            }
            finally
            {
                response.Close();
            }
        }
    }
}
