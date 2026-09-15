using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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

    public class DatabaseState
    {
        public List<User> Usuarios { get; set; } = new List<User>();
        public List<Session> Sesiones { get; set; } = new List<Session>();
        public List<FileMeta> FileMetadata { get; set; } = new List<FileMeta>();
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
                catch
                {
                    _state = new DatabaseState();
                }
            }
            else
            {
                _state = new DatabaseState();
                Save();
            }

            if (_state.Usuarios != null && _state.Usuarios.Count > 0)
            {
                bool modified = false;
                for (int i = 0; i < _state.Usuarios.Count; i++)
                {
                    if (string.IsNullOrWhiteSpace(_state.Usuarios[i].Role))
                    {
                        if (i == 0 || _state.Usuarios[i].Username.Equals("admin", StringComparison.OrdinalIgnoreCase))
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
                
                // Mover de forma atómica para prevenir corrupción si se va la luz (compatible con .NET 4.7.2)
                try
                {
                    if (File.Exists(_dbPath))
                        File.Delete(_dbPath);
                    File.Move(tempPath, _dbPath);
                }
                catch
                {
                    throw;
                }
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
                Console.WriteLine("Creando usuario administrador por defecto.");
                username = "admin";
                password = "admin";
                Console.WriteLine($"-> Usuario: {username}");
                Console.WriteLine($"-> Contraseña: {password}");
                Console.WriteLine("¡Por favor cambia esta contraseña inmediatamente!");
            }
            else
            {
                while (string.IsNullOrWhiteSpace(username))
                {
                    Console.Write("Introduce el nuevo nombre de administrador: ");
                    username = Console.ReadLine()?.Trim();
                }

                while (string.IsNullOrWhiteSpace(password))
                {
                    Console.Write("Introduce la nueva contraseña: ");
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
            if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password)) return null;

            string hash = HashPassword(password);

            lock (_lock)
            {
                var user = _state.Usuarios.FirstOrDefault(u => u.Username.Equals(username, StringComparison.OrdinalIgnoreCase) && u.PasswordHash == hash);
                if (user == null) return null;

                string token = Guid.NewGuid().ToString("N");
                _state.Sesiones.Add(new Session
                {
                    Token = token,
                    UserId = user.Id,
                    FechaExpiracion = DateTime.UtcNow.AddMinutes(5)
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

                // Renovar la sesión por 5 minutos adicionales (inactividad)
                session.FechaExpiracion = DateTime.UtcNow.AddMinutes(5);

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
            if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password)) return false;

            string validRole = "Estandar";
            if (role?.Equals("Admin", StringComparison.OrdinalIgnoreCase) == true) validRole = "Admin";
            else if (role?.Equals("Visitante", StringComparison.OrdinalIgnoreCase) == true) validRole = "Visitante";
            else if (role?.Equals("Estandar", StringComparison.OrdinalIgnoreCase) == true) validRole = "Estandar";

            lock (_lock)
            {
                if (_state.Usuarios.Any(u => u.Username.Equals(username, StringComparison.OrdinalIgnoreCase)))
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

        public bool ChangePassword(string username, string newPassword)
        {
            if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(newPassword)) return false;

            lock (_lock)
            {
                var user = _state.Usuarios.FirstOrDefault(u => u.Username.Equals(username, StringComparison.OrdinalIgnoreCase));
                if (user == null) return false;

                user.PasswordHash = HashPassword(newPassword);
                Save();
                return true;
            }
        }

        public bool ValidatePassword(string username, string password)
        {
            if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password)) return false;

            string hash = HashPassword(password);
            lock (_lock)
            {
                return _state.Usuarios.Any(u => u.Username.Equals(username, StringComparison.OrdinalIgnoreCase) && u.PasswordHash == hash);
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

        private string HashPassword(string password)
        {
            using (var sha256 = SHA256.Create())
            {
                byte[] bytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(password));
                var builder = new StringBuilder();
                foreach (byte b in bytes)
                {
                    builder.Append(b.ToString("x2"));
                }
                return builder.ToString();
            }
        }
    }
}
