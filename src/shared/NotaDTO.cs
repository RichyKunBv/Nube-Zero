using System;

namespace NubeZero.Shared
{
    public class NotaDTO
    {
        public string Id { get; set; } = string.Empty;
        public string Contenido { get; set; } = string.Empty;
        public string CreadoPor { get; set; } = string.Empty;
        public DateTime FechaCreacion { get; set; } = DateTime.UtcNow;
    }
}
