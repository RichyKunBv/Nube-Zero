using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text.Json.Serialization;

namespace NubeZero.Shared
{
    public class ArchivoDTO : INotifyPropertyChanged
    {
        private object _thumbnailSource = null!;

        public string Nombre { get; set; } = string.Empty;
        public long PesoBytes { get; set; }
        public DateTime FechaModificacion { get; set; }
        public string ModificadoPor { get; set; } = string.Empty;
        public bool EsCarpeta { get; set; }

        [JsonIgnore]
        public object ThumbnailSource
        {
            get => _thumbnailSource;
            set
            {
                if (ReferenceEquals(_thumbnailSource, value)) return;
                _thumbnailSource = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ThumbnailSource)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Icono)));
            }
        }

        [JsonIgnore]
        public string Icono
        {
            get
            {
                if (ThumbnailSource != null) return string.Empty;
                if (EsCarpeta) return "📁";

                string extension = Path.GetExtension(Nombre ?? string.Empty).ToLowerInvariant();
                if (new[] { ".jpg", ".jpeg", ".png", ".gif", ".webp", ".bmp" }.Contains(extension)) return "🖼️";
                if (new[] { ".mp4", ".mov", ".mkv", ".avi", ".webm", ".m4v", ".3gp", ".wmv", ".mpeg", ".mpg" }.Contains(extension)) return "🎬";
                if (extension == ".apk") return "📱";
                if (new[] { ".zip", ".rar", ".7z", ".tar", ".gz" }.Contains(extension)) return "🗜️";
                if (new[] { ".pdf", ".doc", ".docx", ".txt", ".odt" }.Contains(extension)) return "📃";
                return "📄";
            }
        }

        [JsonIgnore]
        public string Categoria
        {
            get
            {
                if (EsCarpeta) return "Carpetas";
                string extension = Path.GetExtension(Nombre ?? string.Empty).ToLowerInvariant();
                if (new[] { ".jpg", ".jpeg", ".png", ".gif", ".webp", ".bmp", ".svg" }.Contains(extension)) return "Imágenes";
                if (new[] { ".mp4", ".mov", ".mkv", ".avi", ".webm", ".m4v", ".3gp", ".wmv", ".mpeg", ".mpg" }.Contains(extension)) return "Videos";
                if (extension == ".apk") return "APKs";
                if (new[] { ".pdf", ".doc", ".docx", ".txt", ".odt", ".xls", ".xlsx", ".ppt", ".pptx", ".csv", ".json", ".md" }.Contains(extension)) return "Documentos";
                if (new[] { ".zip", ".rar", ".7z", ".tar", ".gz" }.Contains(extension)) return "Comprimidos";
                return "Otros";
            }
        }

        public event PropertyChangedEventHandler PropertyChanged = delegate { };
    }
}
