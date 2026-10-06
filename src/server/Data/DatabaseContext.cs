using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace NubeZero.Server.Data
{
    public class User
    {
        public long Id { get; set; }
        public string Username { get; set; }
        public string PasswordHash { get; set; }
        public string Role { get; set; } = "Estandar"; // "Admin", "Estandar", "Visitante"
    }

    public class AuthSession
    {
        public string Token { get; set; }
        public string Username { get; set; }
        public string Role { get; set; }
    }

    public class Session
    {
        public string Token { get; set; }
        public long UserId { get; set; }
        public DateTime FechaExpiracion { get; set; }
    }

    public class FileMeta
    {
        public string FilePath { get; set; }
        public string UploadedBy { get; set; }
        public long Size { get; set; }
    }

    public class NoteItem
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string Contenido { get; set; } = "";
        public string CreadoPor { get; set; } = "";
        public DateTime FechaCreacion { get; set; } = DateTime.UtcNow;
    }

    public class DatabaseState
    {
        public List<User> Usuarios { get; set; } = new List<User>();
        public List<Session> Sesiones { get; set; } = new List<Session>();
        public List<FileMeta> FileMetadata { get; set; } = new List<FileMeta>();
        public List<NoteItem> Notas { get; set; } = new List<NoteItem>();
        public long NextUserId { get; set; } = 1;
    }

    public class DatabaseContext
    {
        private readonly string _dbPath;
        private DatabaseState _state;
        private readonly object _lock = new object();

        public DatabaseContext(string customStoragePath = null)
        {
            string baseDir;
            if (!string.IsNullOrWhiteSpace(customStoragePath))
            {
                baseDir = customStoragePath;
            }
            else
            {
                baseDir = Directory.GetCurrentDirectory();
            }
            _dbPath = Path.Combine(baseDir, "database.json");
            EnsureDatabaseExists();
        }

        private void EnsureDatabaseExists()
        {
            string dir = Path.GetDirectoryName(_dbPath);
            if (!Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            if (File.Exists(_dbPath))
            {
                try
                {
                    string json = File.ReadAllText(_dbPath);
                    _state = JsonSerializer.Deserialize<DatabaseState>(json) ?? new DatabaseState();
                }
                catch (JsonException ex)
                {
                    throw new InvalidDataException("La base de datos contiene JSON inválido; se detuvo el inicio para evitar perder datos o recrear credenciales.", ex);
                }
            }
            else
            {
                _state = new DatabaseState();
                Save();
            }

            _state.Usuarios ??= new List<User>();
            _state.Sesiones ??= new List<Session>();
            _state.FileMetadata ??= new List<FileMeta>();
            if (_state.Notas == null)
            {
                _state.Notas = new List<NoteItem>();
            }

            if (_state.Usuarios != null && _state.Usuarios.Count > 0)
            {
                bool modified = false;
                for (int i = 0; i < _state.Usuarios.Count; i++)
                {
                    if (string.Equals(_state.Usuarios[i].Username, "admin", StringComparison.OrdinalIgnoreCase)
                        && _state.Usuarios[i].PasswordHash == LegacyHashPassword("admin"))
                    {
                        string replacementPassword = CreateRandomPassword();
                        _state.Usuarios[i].PasswordHash = HashPassword(replacementPassword);
                        Console.WriteLine("La contraseña predeterminada admin/admin fue revocada.");
                        Console.WriteLine("Contraseña de recuperación de admin (guárdala y cámbiala al iniciar sesión):");
                        Console.WriteLine(replacementPassword);
                        modified = true;
                    }

                    if (string.IsNullOrWhiteSpace(_state.Usuarios[i].Role))
                    {
                        if (i == 0 || string.Equals(_state.Usuarios[i].Username, "admin", StringComparison.OrdinalIgnoreCase))
                            _state.Usuarios[i].Role = "Admin";
                        else
                            _state.Usuarios[i].Role = "Estandar";
                        modified = true;
                    }
                }
                if (modified)
                {
                    Save();
                }
            }
            else
            {
                CheckAndCreateInitialUser();
            }
        }

        private void Save()
        {
            lock (_lock)
            {
                // Limpiar sesiones expiradas para evitar fuga de memoria
                _state.Sesiones.RemoveAll(s => s.FechaExpiracion <= DateTime.UtcNow);

                string tempPath = _dbPath + ".tmp";
                
                // Usar stream para evitar cargar un string gigante en RAM
                using (var fs = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    JsonSerializer.Serialize(fs, _state, new JsonSerializerOptions { WriteIndented = true });
                }
                
                if (File.Exists(_dbPath))
                    File.Replace(tempPath, _dbPath, null);
                else
                    File.Move(tempPath, _dbPath);
            }
        }

        private void CheckAndCreateInitialUser()
        {
            Console.WriteLine("\n=======================================================");
            Console.WriteLine("    PRIMER INICIO DE NUBE-ZERO - CONFIGURACIÓN INICIAL   ");
            Console.WriteLine("=======================================================");
            Console.WriteLine("No se encontraron usuarios en la base de datos.");

            string username = "";
            string password = "";

            if (Console.IsInputRedirected)
            {
                Console.WriteLine("Entorno no interactivo detectado (ej. systemd).");
                username = "admin";
                string encodedPassword = Environment.GetEnvironmentVariable("NUBEZERO_INITIAL_ADMIN_PASSWORD_BASE64");
                if (!string.IsNullOrWhiteSpace(encodedPassword))
                {
                    byte[] passwordBytes;
                    try
                    {
                        passwordBytes = Convert.FromBase64String(encodedPassword);
                    }
                    catch (FormatException ex)
                    {
                        throw new InvalidOperationException("NUBEZERO_INITIAL_ADMIN_PASSWORD_BASE64 no contiene Base64 válido.", ex);
                    }

                    try
                    {
                        password = Encoding.UTF8.GetString(passwordBytes);
                    }
                    finally
                    {
                        Array.Clear(passwordBytes, 0, passwordBytes.Length);
                    }
                }
                if (!IsPasswordAcceptable(password))
                    throw new InvalidOperationException("Configura una contraseña inicial de al menos 12 caracteres durante la instalación antes del primer inicio.");
                Console.WriteLine("Creando la cuenta inicial admin con la contraseña privada configurada en la instalación.");
            }
            else
            {
                while (string.IsNullOrWhiteSpace(username))
                {
                    Console.Write("Introduce el nuevo nombre de administrador: ");
                    username = Console.ReadLine()?.Trim();
                }

                while (!IsPasswordAcceptable(password))
                {
                    Console.Write("Introduce una contraseña de al menos 12 caracteres: ");
                    password = Console.ReadLine()?.Trim();
                }
            }

            string hash = HashPassword(password);

            lock (_lock)
            {
                _state.Usuarios.Add(new User
                {
                    Id = _state.NextUserId++,
                    Username = username,
                    PasswordHash = hash,
                    Role = "Admin"
                });
                Save();
            }

            Console.WriteLine($"\nUsuario '{username}' creado con éxito. Ya puedes iniciar sesión desde la aplicación.");
            Console.WriteLine("=======================================================\n");
        }

        public AuthSession Authenticate(string username, string password)
        {
            if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password) || password.Length > 1024) return null;

            lock (_lock)
            {
                var user = _state.Usuarios.FirstOrDefault(u => string.Equals(u.Username, username, StringComparison.OrdinalIgnoreCase));
                if (user == null || !VerifyPassword(password, user.PasswordHash)) return null;

                if (!IsCurrentPasswordHash(user.PasswordHash))
                {
                    user.PasswordHash = HashPassword(password);
                    Save();
                }

                string token = CreateSecureToken();
                _state.Sesiones.Add(new Session
                {
                    Token = token,
                    UserId = user.Id,
                    FechaExpiracion = DateTime.UtcNow.AddMinutes(30)
                });
                Save();

                return new AuthSession
                {
                    Token = token,
                    Username = user.Username,
                    Role = user.Role ?? "Estandar"
                };
            }
        }

        public string CreateSession(string username, string password)
        {
            var auth = Authenticate(username, password);
            return auth?.Token;
        }

        public AuthSession ValidateToken(string token)
        {
            if (string.IsNullOrWhiteSpace(token)) return null;

            lock (_lock)
            {
                var session = _state.Sesiones.FirstOrDefault(s => s.Token == token && s.FechaExpiracion > DateTime.UtcNow);
                if (session == null) return null;

                session.FechaExpiracion = DateTime.UtcNow.AddMinutes(30);

                var user = _state.Usuarios.FirstOrDefault(u => u.Id == session.UserId);
                if (user == null) return null;

                return new AuthSession
                {
                    Token = token,
                    Username = user.Username,
                    Role = user.Role ?? "Estandar"
                };
            }
        }

        public string ValidateTokenAndGetUser(string token)
        {
            return ValidateToken(token)?.Username;
        }

        public void ExtendSessionForLongOperation(string token)
        {
            lock (_lock)
            {
                var session = _state.Sesiones.FirstOrDefault(s => s.Token == token);
                if (session != null)
                {
                    session.FechaExpiracion = DateTime.UtcNow.AddHours(2).AddMinutes(10);
                }
            }
        }

        public void RefreshSessionAfterLongOperation(string token)
        {
            lock (_lock)
            {
                var session = _state.Sesiones.FirstOrDefault(s => s.Token == token);
                if (session != null)
                {
                    session.FechaExpiracion = DateTime.UtcNow.AddMinutes(30);
                }
            }
        }

        public List<NubeZero.Shared.UserDTO> ListUsers()
        {
            lock (_lock)
            {
                return _state.Usuarios.Select(u => new NubeZero.Shared.UserDTO
                {
                    Id = u.Id,
                    Username = u.Username,
                    Role = u.Role ?? "Estandar"
                }).ToList();
            }
        }

        public bool AddUser(string username, string password, string role = "Estandar")
        {
            if (!IsUsernameAcceptable(username) || !IsPasswordAcceptable(password)) return false;

            string validRole = "Estandar";
            if (role?.Equals("Admin", StringComparison.OrdinalIgnoreCase) == true) validRole = "Admin";
            else if (role?.Equals("Visitante", StringComparison.OrdinalIgnoreCase) == true) validRole = "Visitante";
            else if (role?.Equals("Estandar", StringComparison.OrdinalIgnoreCase) == true) validRole = "Estandar";

            lock (_lock)
            {
                if (_state.Usuarios.Any(u => string.Equals(u.Username, username, StringComparison.OrdinalIgnoreCase)))
                {
                    return false; // El usuario ya existe
                }

                string hash = HashPassword(password);
                _state.Usuarios.Add(new User
                {
                    Id = _state.NextUserId++,
                    Username = username.Trim(),
                    PasswordHash = hash,
                    Role = validRole
                });
                Save();
                return true;
            }
        }

        public bool DeleteUser(string username, out string error)
        {
            error = null;
            if (string.IsNullOrWhiteSpace(username))
            {
                error = "Nombre de usuario inválido.";
                return false;
            }

            lock (_lock)
            {
                var user = _state.Usuarios.FirstOrDefault(u => u.Username.Equals(username, StringComparison.OrdinalIgnoreCase));
                if (user == null)
                {
                    error = "El usuario no existe.";
                    return false;
                }

                // Si es admin, verificar que no sea el único admin del sistema
                if (user.Role == "Admin")
                {
                    int adminCount = _state.Usuarios.Count(u => u.Role == "Admin");
                    if (adminCount <= 1)
                    {
                        error = "No puedes eliminar el único Administrador del sistema.";
                        return false;
                    }
                }

                _state.Sesiones.RemoveAll(s => s.UserId == user.Id);
                _state.Usuarios.Remove(user);
                Save();
                return true;
            }
        }

        public bool ChangePassword(string username, string newPassword, string currentSessionToken = null)
        {
            if (!IsPasswordAcceptable(newPassword)) return false;

            lock (_lock)
            {
                var user = _state.Usuarios.FirstOrDefault(u => u.Username.Equals(username, StringComparison.OrdinalIgnoreCase));
                if (user == null) return false;

                user.PasswordHash = HashPassword(newPassword);
                _state.Sesiones.RemoveAll(s => s.UserId == user.Id && s.Token != currentSessionToken);
                Save();
                return true;
            }
        }

        public bool ValidatePassword(string username, string password)
        {
            if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password) || password.Length > 1024) return false;
            lock (_lock)
            {
                var user = _state.Usuarios.FirstOrDefault(u => u.Username.Equals(username, StringComparison.OrdinalIgnoreCase));
                if (user == null || !VerifyPassword(password, user.PasswordHash))
                    return false;

                if (!IsCurrentPasswordHash(user.PasswordHash))
                {
                    user.PasswordHash = HashPassword(password);
                    Save();
                }
                return true;
            }
        }

        public string GetUserRole(string username)
        {
            if (string.IsNullOrWhiteSpace(username)) return null;

            lock (_lock)
            {
                var user = _state.Usuarios.FirstOrDefault(u => u.Username.Equals(username, StringComparison.OrdinalIgnoreCase));
                return user?.Role;
            }
        }

        public void SaveFileMetadata(string filePath, string username, long size)
        {
            lock (_lock)
            {
                var meta = _state.FileMetadata.FirstOrDefault(m => m.FilePath == filePath);
                if (meta != null)
                {
                    meta.UploadedBy = username;
                    meta.Size = size;
                }
                else
                {
                    _state.FileMetadata.Add(new FileMeta
                    {
                        FilePath = filePath,
                        UploadedBy = username,
                        Size = size
                    });
                }
                Save();
            }
        }

        public string GetFileOwner(string filePath)
        {
            lock (_lock)
            {
                var meta = _state.FileMetadata.FirstOrDefault(m => m.FilePath == filePath);
                return meta?.UploadedBy ?? "System";
            }
        }

        public void DeleteFileMetadata(string filePath)
        {
            lock (_lock)
            {
                _state.FileMetadata.RemoveAll(m => m.FilePath == filePath);
                Save();
            }
        }

        public void RenameFileMetadata(string oldPath, string newPath)
        {
            lock (_lock)
            {
                var meta = _state.FileMetadata.FirstOrDefault(m => m.FilePath == oldPath);
                if (meta != null)
                {
                    meta.FilePath = newPath;
                    Save();
                }
            }
        }

        public List<NoteItem> GetNotas()
        {
            lock (_lock)
            {
                if (_state.Notas == null) _state.Notas = new List<NoteItem>();
                return new List<NoteItem>(_state.Notas);
            }
        }

        public NoteItem AddNota(string contenido, string username)
        {
            lock (_lock)
            {
                if (_state.Notas == null) _state.Notas = new List<NoteItem>();
                var nota = new NoteItem
                {
                    Id = Guid.NewGuid().ToString("N"),
                    Contenido = contenido,
                    CreadoPor = username,
                    FechaCreacion = DateTime.UtcNow
                };
                _state.Notas.Add(nota);
                Save();
                return nota;
            }
        }

        public bool DeleteNota(string id, string username, string role)
        {
            lock (_lock)
            {
                if (_state.Notas == null) return false;
                var nota = _state.Notas.FirstOrDefault(n => n.Id == id);
                if (nota == null) return false;

                if (role == "Admin" || string.Equals(nota.CreadoPor, username, StringComparison.OrdinalIgnoreCase))
                {
                    _state.Notas.Remove(nota);
                    Save();
                    return true;
                }
                return false;
            }
        }

        private const int PasswordHashIterations = 210000;
        private const int PasswordSaltSize = 16;
        private const int PasswordHashSize = 32;
        private const string PasswordHashScheme = "pbkdf2-sha256";

        private static bool IsPasswordAcceptable(string password)
        {
            return !string.IsNullOrWhiteSpace(password) && password.Length >= 12 && password.Length <= 1024;
        }

        private static bool IsUsernameAcceptable(string username)
        {
            return !string.IsNullOrWhiteSpace(username)
                && username.Length <= 64
                && !username.Any(char.IsControl);
        }

        private static string CreateSecureToken()
        {
            byte[] token = new byte[32];
            using (var random = RandomNumberGenerator.Create())
                random.GetBytes(token);

            try
            {
                return Convert.ToBase64String(token).TrimEnd('=').Replace('+', '-').Replace('/', '_');
            }
            finally
            {
                Array.Clear(token, 0, token.Length);
            }
        }

        private static string CreateRandomPassword()
        {
            byte[] randomBytes = new byte[24];
            using (var random = RandomNumberGenerator.Create())
                random.GetBytes(randomBytes);

            try
            {
                return Convert.ToBase64String(randomBytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
            }
            finally
            {
                Array.Clear(randomBytes, 0, randomBytes.Length);
            }
        }

        private string HashPassword(string password)
        {
            byte[] salt = new byte[PasswordSaltSize];
            byte[] passwordBytes = Encoding.UTF8.GetBytes(password);
            byte[] hash = null;
            using (var random = RandomNumberGenerator.Create())
                random.GetBytes(salt);

            try
            {
#if NET6_0_OR_GREATER
                hash = Rfc2898DeriveBytes.Pbkdf2(passwordBytes, salt, PasswordHashIterations, HashAlgorithmName.SHA256, PasswordHashSize);
#else
                using (var kdf = new Rfc2898DeriveBytes(passwordBytes, salt, PasswordHashIterations, HashAlgorithmName.SHA256))
                    hash = kdf.GetBytes(PasswordHashSize);
#endif

                return string.Join("$", PasswordHashScheme, PasswordHashIterations.ToString(CultureInfo.InvariantCulture),
                    Convert.ToBase64String(salt), Convert.ToBase64String(hash));
            }
            finally
            {
                Array.Clear(salt, 0, salt.Length);
                Array.Clear(passwordBytes, 0, passwordBytes.Length);
                if (hash != null)
                    Array.Clear(hash, 0, hash.Length);
            }
        }

        private static bool VerifyPassword(string password, string storedHash)
        {
            if (string.IsNullOrEmpty(storedHash))
                return false;

            string[] parts = storedHash.Split('$');
            if (parts.Length == 4 && parts[0] == PasswordHashScheme
                && int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out int iterations)
                && iterations >= 100000 && iterations <= 500000)
            {
                byte[] salt;
                byte[] expectedHash;
                try
                {
                    salt = Convert.FromBase64String(parts[2]);
                    expectedHash = Convert.FromBase64String(parts[3]);
                }
                catch (FormatException)
                {
                    return false;
                }

                if (salt.Length != PasswordSaltSize || expectedHash.Length != PasswordHashSize)
                {
                    Array.Clear(salt, 0, salt.Length);
                    Array.Clear(expectedHash, 0, expectedHash.Length);
                    return false;
                }

                byte[] passwordBytes = Encoding.UTF8.GetBytes(password);
                byte[] actualHash;
                try
                {
#if NET6_0_OR_GREATER
                    actualHash = Rfc2898DeriveBytes.Pbkdf2(passwordBytes, salt, iterations, HashAlgorithmName.SHA256, PasswordHashSize);
#else
                    using (var kdf = new Rfc2898DeriveBytes(passwordBytes, salt, iterations, HashAlgorithmName.SHA256))
                        actualHash = kdf.GetBytes(PasswordHashSize);
#endif
                }
                finally
                {
                    Array.Clear(passwordBytes, 0, passwordBytes.Length);
                    Array.Clear(salt, 0, salt.Length);
                }

                bool matches = FixedTimeEquals(actualHash, expectedHash);
                Array.Clear(actualHash, 0, actualHash.Length);
                Array.Clear(expectedHash, 0, expectedHash.Length);
                return matches;
            }

            string legacyHash = LegacyHashPassword(password);
            return FixedTimeEquals(Encoding.UTF8.GetBytes(legacyHash), Encoding.UTF8.GetBytes(storedHash));
        }

        private static bool IsCurrentPasswordHash(string storedHash)
        {
            string[] parts = storedHash?.Split('$');
            return parts != null && parts.Length == 4 && parts[0] == PasswordHashScheme
                && int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out int iterations)
                && iterations == PasswordHashIterations;
        }

        private static string LegacyHashPassword(string password)
        {
            using (var sha256 = SHA256.Create())
            {
                byte[] bytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(password));
                var builder = new StringBuilder();
                foreach (byte b in bytes)
                    builder.Append(b.ToString("x2"));
                Array.Clear(bytes, 0, bytes.Length);
                return builder.ToString();
            }
        }

        private static bool FixedTimeEquals(byte[] left, byte[] right)
        {
            if (left.Length != right.Length)
                return false;

            int difference = 0;
            for (int i = 0; i < left.Length; i++)
                difference |= left[i] ^ right[i];
            return difference == 0;
        }
    }
}
