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

        public DatabaseContext()
        {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            _dbPath = Path.Combine(baseDir, "Storage", "database.json");
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

            if (_state.Usuarios.Count == 0)
            {
                CheckAndCreateInitialUser();
            }
        }

        private void Save()
        {
            lock (_lock)
            {
                string json = JsonSerializer.Serialize(_state, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(_dbPath, json);
            }
        }

        private void CheckAndCreateInitialUser()
        {
            Console.WriteLine("\n=======================================================");
            Console.WriteLine("    PRIMER INICIO DE NUBE-ZERO - CONFIGURACIÓN INICIAL   ");
            Console.WriteLine("=======================================================");
            Console.WriteLine("No se encontraron usuarios en la base de datos.");

            string username = "";
            while (string.IsNullOrWhiteSpace(username))
            {
                Console.Write("Introduce el nuevo nombre de administrador: ");
                username = Console.ReadLine()?.Trim();
            }

            string password = "";
            while (string.IsNullOrWhiteSpace(password))
            {
                Console.Write("Introduce la nueva contraseña: ");
                password = Console.ReadLine()?.Trim();
            }

            string hash = HashPassword(password);

            lock (_lock)
            {
                _state.Usuarios.Add(new User
                {
                    Id = _state.NextUserId++,
                    Username = username,
                    PasswordHash = hash
                });
                Save();
            }

            Console.WriteLine($"\nUsuario '{username}' creado con éxito. Ya puedes iniciar sesión desde la aplicación.");
            Console.WriteLine("=======================================================\n");
        }

        public string CreateSession(string username, string password)
        {
            string hash = HashPassword(password);

            lock (_lock)
            {
                var user = _state.Usuarios.FirstOrDefault(u => u.Username == username && u.PasswordHash == hash);
                if (user == null) return null;

                string token = Guid.NewGuid().ToString("N");
                _state.Sesiones.Add(new Session
                {
                    Token = token,
                    UserId = user.Id,
                    FechaExpiracion = DateTime.UtcNow.AddDays(30)
                });
                Save();

                return token;
            }
        }

        public string ValidateTokenAndGetUser(string token)
        {
            lock (_lock)
            {
                var session = _state.Sesiones.FirstOrDefault(s => s.Token == token && s.FechaExpiracion > DateTime.UtcNow);
                if (session == null) return null;

                var user = _state.Usuarios.FirstOrDefault(u => u.Id == session.UserId);
                return user?.Username;
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
