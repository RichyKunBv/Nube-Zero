using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using NubeZero.Shared;

namespace NubeZero.Server.Services
{
    public static class EncryptionService
    {
        private const string KeyEnvironmentVariable = "NUBEZERO_FILE_ENCRYPTION_KEY";

        public static void ValidateConfiguration()
        {
            byte[] key = GetMasterKey();
            Array.Clear(key, 0, key.Length);
        }

        public static byte[] GetMasterKey()
        {
            string encodedKey = Environment.GetEnvironmentVariable(KeyEnvironmentVariable);
            if (string.IsNullOrWhiteSpace(encodedKey))
                throw new InvalidOperationException($"{KeyEnvironmentVariable} debe contener una clave Base64 de 32 bytes.");

            byte[] key;
            try
            {
                key = Convert.FromBase64String(encodedKey);
            }
            catch (FormatException ex)
            {
                throw new InvalidOperationException($"{KeyEnvironmentVariable} no contiene Base64 válido.", ex);
            }

            if (key.Length != 32)
            {
                Array.Clear(key, 0, key.Length);
                throw new InvalidOperationException($"{KeyEnvironmentVariable} debe decodificarse exactamente a 32 bytes.");
            }

            return key;
        }

        public static string GetMasterKeyForClient()
        {
            byte[] key = GetMasterKey();
            try
            {
                return Convert.ToBase64String(key);
            }
            finally
            {
                Array.Clear(key, 0, key.Length);
            }
        }

        public static void MigrateLegacyFiles(string storagePath)
        {
            byte[] key = GetMasterKey();
            try
            {
                string markerPath = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(storagePath)), ".nubezero-encryption-v1");
                if (File.Exists(markerPath))
                {
                    ValidateMigrationMarker(markerPath, key);
                    return;
                }

                var directories = new System.Collections.Generic.Stack<string>();
                directories.Push(storagePath);

                while (directories.Count > 0)
                {
                    string directory = directories.Pop();
                    foreach (string childDirectory in Directory.GetDirectories(directory))
                    {
                        if ((File.GetAttributes(childDirectory) & FileAttributes.ReparsePoint) == 0)
                            directories.Push(childDirectory);
                    }

                    foreach (string filePath in Directory.GetFiles(directory))
                    {
                        if ((File.GetAttributes(filePath) & FileAttributes.ReparsePoint) != 0)
                            continue;
                        string fileName = Path.GetFileName(filePath);
                        bool staleUpload = fileName.StartsWith(".nubezero-", StringComparison.Ordinal)
                            && (fileName.EndsWith(".upload", StringComparison.Ordinal)
                                || fileName.EndsWith(".upload.encrypted", StringComparison.Ordinal));
                        bool staleMigration = fileName.Contains(".nubezero-")
                                && (fileName.EndsWith(".tmp", StringComparison.Ordinal)
                                    || fileName.EndsWith(".plain", StringComparison.Ordinal));
                        if (staleUpload || staleMigration)
                        {
                            File.Delete(filePath);
                            continue;
                        }
                        MigrateFile(filePath, key);
                    }
                }

                string markerTemporaryPath = markerPath + ".tmp";
                File.WriteAllText(markerTemporaryPath, CreateMigrationMarker(key));
                File.Move(markerTemporaryPath, markerPath);
            }
            finally
            {
                Array.Clear(key, 0, key.Length);
            }
        }

        private static string CreateMigrationMarker(byte[] key)
        {
            using (var hmac = new HMACSHA256(key))
                return Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes("NubeZero encrypted storage v1")));
        }

        private static void ValidateMigrationMarker(string markerPath, byte[] key)
        {
            string expected = CreateMigrationMarker(key);
            string actual = File.ReadAllText(markerPath).Trim();
            byte[] expectedBytes;
            byte[] actualBytes;
            try
            {
                expectedBytes = Convert.FromBase64String(expected);
                actualBytes = Convert.FromBase64String(actual);
            }
            catch (FormatException ex)
            {
                throw new CryptographicException("El marcador de cifrado del almacenamiento no es válido.", ex);
            }

            int difference = expectedBytes.Length ^ actualBytes.Length;
            int length = Math.Min(expectedBytes.Length, actualBytes.Length);
            for (int i = 0; i < length; i++)
                difference |= expectedBytes[i] ^ actualBytes[i];
            Array.Clear(expectedBytes, 0, expectedBytes.Length);
            Array.Clear(actualBytes, 0, actualBytes.Length);

            if (difference != 0)
                throw new CryptographicException("La clave configurada no coincide con la usada para cifrar este almacenamiento.");
        }

        private static void MigrateFile(string filePath, byte[] key)
        {
            string encryptedTemporaryPath = filePath + ".nubezero-" + Guid.NewGuid().ToString("N") + ".tmp";
            string plaintextTemporaryPath = null;
            bool startsWithCurrentFormat;

            using (var source = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                byte[] magic = new byte[4];
                int read = source.Read(magic, 0, magic.Length);
                startsWithCurrentFormat = read == magic.Length
                    && magic[0] == (byte)'N'
                    && magic[1] == (byte)'Z'
                    && magic[2] == (byte)'F'
                    && magic[3] == (byte)'1';
            }

            try
            {
                if (startsWithCurrentFormat)
                {
                    plaintextTemporaryPath = filePath + ".nubezero-" + Guid.NewGuid().ToString("N") + ".plain";
                    using (var encrypted = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                    using (var plaintext = new FileStream(plaintextTemporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    {
                        FileEncryptionService.DecryptAsync(encrypted, plaintext, key).GetAwaiter().GetResult();
                    }

                    using (var plaintext = new FileStream(plaintextTemporaryPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                    using (var encrypted = new FileStream(encryptedTemporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    {
                        FileEncryptionService.EncryptAsync(plaintext, encrypted, key).GetAwaiter().GetResult();
                    }
                }
                else
                {
                    using (var plaintext = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                    using (var encrypted = new FileStream(encryptedTemporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    {
                        FileEncryptionService.EncryptAsync(plaintext, encrypted, key).GetAwaiter().GetResult();
                    }
                }

                File.Replace(encryptedTemporaryPath, filePath, null);
            }
            finally
            {
                if (File.Exists(encryptedTemporaryPath))
                    File.Delete(encryptedTemporaryPath);
                if (plaintextTemporaryPath != null && File.Exists(plaintextTemporaryPath))
                    File.Delete(plaintextTemporaryPath);
            }
        }
    }
}
