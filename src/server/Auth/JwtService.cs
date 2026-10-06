using System;
using System.Collections.Generic;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.IdentityModel.Tokens;

namespace NubeZero.Server.Auth
{
    /// <summary>
    /// Simple JWT service. In production replace the secret with a secure key vault.
    /// </summary>
    public static class JwtService
    {
// Secret key – loaded from environment variable for security. Fallback to a generated key if not set (not recommended for production).
        private static readonly string SecretKey = Environment.GetEnvironmentVariable("JWT_SECRET_KEY") ?? "ReplaceThisWithAStrongSecretKeyForProduction";
        private static readonly byte[] SecretKeyBytes = Encoding.UTF8.GetBytes(SecretKey);
        private static readonly SymmetricSecurityKey SigningKey = new SymmetricSecurityKey(SecretKeyBytes);
        private static readonly SigningCredentials SigningCredentials = new SigningCredentials(SigningKey, SecurityAlgorithms.HmacSha256);

        public static string GenerateToken(string username, string role, TimeSpan? expiry = null)
        {
            var tokenHandler = new JwtSecurityTokenHandler();
            var claims = new List<Claim>
            {
                new Claim(ClaimTypes.Name, username),
                new Claim(ClaimTypes.Role, role)
            };
            var tokenDescriptor = new SecurityTokenDescriptor
            {
                Subject = new ClaimsIdentity(claims),
                Expires = DateTime.UtcNow.Add(expiry ?? TimeSpan.FromHours(4)),
                SigningCredentials = SigningCredentials
            };
            var token = tokenHandler.CreateToken(tokenDescriptor);
            return tokenHandler.WriteToken(token);
        }

        public static ClaimsPrincipal ValidateToken(string token)
        {
            var tokenHandler = new JwtSecurityTokenHandler();
            var validationParameters = new TokenValidationParameters
            {
                ValidateIssuer = false,
                ValidateAudience = false,
                ValidateIssuerSigningKey = true,
                IssuerSigningKey = SigningKey,
                ClockSkew = TimeSpan.Zero
            };
            try
            {
                var principal = tokenHandler.ValidateToken(token, validationParameters, out SecurityToken validatedToken);
                return principal;
            }
            catch
            {
                return null;
            }
        }
    }
}
