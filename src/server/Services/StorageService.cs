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
                _baseStoragePath = Path.Combine(customStoragePath, "Storage");
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
            string rootPath = Path.GetFullPath(_baseStoragePath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string fullPath = Path.GetFullPath(Path.Combine(rootPath, requestedRelativePath));

            StringComparison comparison = Path.DirectorySeparatorChar == '\\'
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;
            string rootPrefix = rootPath + Path.DirectorySeparatorChar;
            if (!string.Equals(fullPath, rootPath, comparison) && !fullPath.StartsWith(rootPrefix, comparison))
            {
                throw new UnauthorizedAccessException("Intento de Path Traversal detectado y bloqueado.");
            }

            string relativePath = fullPath.Length == rootPath.Length
                ? string.Empty
                : fullPath.Substring(rootPrefix.Length);
            string currentPath = rootPath;
            foreach (string segment in relativePath.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries))
            {
                currentPath = Path.Combine(currentPath, segment);
                if ((File.Exists(currentPath) || Directory.Exists(currentPath))
                    && (File.GetAttributes(currentPath) & FileAttributes.ReparsePoint) != 0)
                    throw new UnauthorizedAccessException("No se permiten enlaces simbólicos dentro del almacenamiento.");
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
