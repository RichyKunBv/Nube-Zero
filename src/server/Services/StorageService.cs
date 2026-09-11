using System;
using System.IO;

namespace NubeZero.Server.Services
{
    public class StorageService
    {
        private readonly string _baseStoragePath;

        public StorageService(string customStoragePath = null)
        {
            if (!string.IsNullOrWhiteSpace(customStoragePath))
            {
                _baseStoragePath = customStoragePath;
            }
            else
            {
                _baseStoragePath = Path.Combine(Directory.GetCurrentDirectory(), "Storage");
            }
            EnsureStorageExists();
        }

        private void EnsureStorageExists()
        {
            if (!Directory.Exists(_baseStoragePath))
            {
                Directory.CreateDirectory(_baseStoragePath);
                Console.WriteLine($"[StorageService] Creado directorio raíz: {_baseStoragePath}");
            }
        }

        public string GetSafePath(string requestedRelativePath)
        {
            if (string.IsNullOrWhiteSpace(requestedRelativePath))
            {
                return _baseStoragePath;
            }

            // Eliminar caracteres raros al inicio si los hay
            requestedRelativePath = requestedRelativePath.TrimStart('/', '\\');
            
            // Combinar la ruta base con la solicitada
            string fullPath = Path.GetFullPath(Path.Combine(_baseStoragePath, requestedRelativePath));

            // Verificación estricta de Path Traversal
            // Asegurar que la ruta resuelta empieza EXACTAMENTE por nuestra ruta base
            if (!fullPath.StartsWith(_baseStoragePath, StringComparison.OrdinalIgnoreCase))
            {
                throw new UnauthorizedAccessException("Intento de Path Traversal detectado y bloqueado.");
            }

            return fullPath;
        }

        public string GetRelativePath(string fullPath)
        {
            if (!fullPath.StartsWith(_baseStoragePath, StringComparison.OrdinalIgnoreCase))
                return fullPath;
                
            string rel = fullPath.Substring(_baseStoragePath.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            // Normalizar a barras '/' para JSON independientemente del SO
            return rel.Replace('\\', '/');
        }
        
        public string BasePath => _baseStoragePath;
    }
}
