using System;

namespace NubeZero.Shared
{
    public class UserDTO
    {
        public long Id { get; set; }
        public string Username { get; set; } = string.Empty;
        public string Role { get; set; } = "Estandar";
    }
}
