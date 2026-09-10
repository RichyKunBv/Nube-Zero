using System;

namespace NubeZero.Shared
{
    public class ArchivoDTO
    {
        public string Nombre { get; set; }
        public long PesoBytes { get; set; }
        public DateTime FechaModificacion { get; set; }
        public string ModificadoPor { get; set; }
        public bool EsCarpeta { get; set; }
    }
}
