using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
#if NET472
using Mono.Data.Sqlite;
using SqliteConnection = Mono.Data.Sqlite.SqliteConnection;
#else
using Microsoft.Data.Sqlite;
#endif
namespace NubeZero.Server.Data
{
    public class DatabaseContext
    {
        private readonly string _dbPath;

        public DatabaseContext()
        {
            // Ubicación base en el mismo directorio que el ejecutable
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            _dbPath = Path.Combine(baseDir, "Storage", "usuarios.db");
            EnsureDatabaseExists();
        }

        private void EnsureDatabaseExists()
        {
            // Asegurar directorio
            string dir = Path.GetDirectoryName(_dbPath);
            if (!Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            bool isNew = !File.Exists(_dbPath);

            using (var connection = GetConnection())
            {
                connection.Open();

                var command = connection.CreateCommand();
                command.CommandText = @"
                    CREATE TABLE IF NOT EXISTS Usuarios (
                        Id INTEGER PRIMARY KEY AUTOINCREMENT,
                        Username TEXT UNIQUE NOT NULL,
                        PasswordHash TEXT NOT NULL
                    );

                    CREATE TABLE IF NOT EXISTS Sesiones (
                        Token TEXT PRIMARY KEY,
                        UserId INTEGER NOT NULL,
                        FechaExpiracion DATETIME NOT NULL,
                        FOREIGN KEY(UserId) REFERENCES Usuarios(Id)
                    );

                    CREATE TABLE IF NOT EXISTS FileMetadata (
                        FilePath TEXT PRIMARY KEY,
                        UploadedBy TEXT NOT NULL,
                        Size INTEGER NOT NULL
                    );
                ";
                command.ExecuteNonQuery();

                if (isNew)
                {
                    CheckAndCreateInitialUser(connection);
                }
            }
        }

        private void CheckAndCreateInitialUser(SqliteConnection connection)
        {
            var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM Usuarios";
            long count = (long)command.ExecuteScalar();

            if (count == 0)
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

                var insertCmd = connection.CreateCommand();
                insertCmd.CommandText = "INSERT INTO Usuarios (Username, PasswordHash) VALUES (@user, @hash)";
                insertCmd.Parameters.AddWithValue("@user", username);
                insertCmd.Parameters.AddWithValue("@hash", hash);
                insertCmd.ExecuteNonQuery();

                Console.WriteLine($"\nUsuario '{username}' creado con éxito. Ya puedes iniciar sesión desde la aplicación.");
                Console.WriteLine("=======================================================\n");
            }
        }

        public string CreateSession(string username, string password)
        {
            string hash = HashPassword(password);

            using (var connection = GetConnection())
            {
                connection.Open();
                var cmd = connection.CreateCommand();
                cmd.CommandText = "SELECT Id FROM Usuarios WHERE Username = @u AND PasswordHash = @p";
                cmd.Parameters.AddWithValue("@u", username);
                cmd.Parameters.AddWithValue("@p", hash);

                var result = cmd.ExecuteScalar();
                if (result == null) return null;

                long userId = (long)result;
                string token = Guid.NewGuid().ToString("N");

                var insertCmd = connection.CreateCommand();
                insertCmd.CommandText = "INSERT INTO Sesiones (Token, UserId, FechaExpiracion) VALUES (@t, @u, @e)";
                insertCmd.Parameters.AddWithValue("@t", token);
                insertCmd.Parameters.AddWithValue("@u", userId);
                insertCmd.Parameters.AddWithValue("@e", DateTime.UtcNow.AddDays(30)); // Sesión de 30 días
                insertCmd.ExecuteNonQuery();

                return token;
            }
        }

        public string ValidateTokenAndGetUser(string token)
        {
            using (var connection = GetConnection())
            {
                connection.Open();
                var cmd = connection.CreateCommand();
                cmd.CommandText = @"
                    SELECT u.Username 
                    FROM Sesiones s
                    JOIN Usuarios u ON s.UserId = u.Id
                    WHERE s.Token = @t AND s.FechaExpiracion > @now";
                cmd.Parameters.AddWithValue("@t", token);
                cmd.Parameters.AddWithValue("@now", DateTime.UtcNow);

                var result = cmd.ExecuteScalar();
                return result as string;
            }
        }

        public void SaveFileMetadata(string filePath, string username, long size)
        {
            using (var connection = GetConnection())
            {
                connection.Open();
                var cmd = connection.CreateCommand();
                cmd.CommandText = @"
                    INSERT INTO FileMetadata (FilePath, UploadedBy, Size)
                    VALUES (@path, @user, @size)
                    ON CONFLICT(FilePath) DO UPDATE SET 
                        UploadedBy = excluded.UploadedBy,
                        Size = excluded.Size;
                ";
                cmd.Parameters.AddWithValue("@path", filePath);
                cmd.Parameters.AddWithValue("@user", username);
                cmd.Parameters.AddWithValue("@size", size);
                cmd.ExecuteNonQuery();
            }
        }

        public string GetFileOwner(string filePath)
        {
            using (var connection = GetConnection())
            {
                connection.Open();
                var cmd = connection.CreateCommand();
                cmd.CommandText = "SELECT UploadedBy FROM FileMetadata WHERE FilePath = @path";
                cmd.Parameters.AddWithValue("@path", filePath);

                var result = cmd.ExecuteScalar();
                return result as string ?? "System";
            }
        }

        public void DeleteFileMetadata(string filePath)
        {
            using (var connection = GetConnection())
            {
                connection.Open();
                var cmd = connection.CreateCommand();
                cmd.CommandText = "DELETE FROM FileMetadata WHERE FilePath = @path";
                cmd.Parameters.AddWithValue("@path", filePath);
                cmd.ExecuteNonQuery();
            }
        }
        
        public void RenameFileMetadata(string oldPath, string newPath)
        {
            using (var connection = GetConnection())
            {
                connection.Open();
                var cmd = connection.CreateCommand();
                cmd.CommandText = "UPDATE FileMetadata SET FilePath = @newPath WHERE FilePath = @oldPath";
                cmd.Parameters.AddWithValue("@oldPath", oldPath);
                cmd.Parameters.AddWithValue("@newPath", newPath);
                cmd.ExecuteNonQuery();
            }
        }

        public SqliteConnection GetConnection()
        {
            return new SqliteConnection($"Data Source={_dbPath}");
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
