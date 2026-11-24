#nullable enable
using System;
using System.Diagnostics;
using System.IO;

namespace NubeZero.Server.Services
{
    public static class MaintenanceService
    {
        private static readonly string FlagFilePath = Path.Combine(AppContext.BaseDirectory, "maintenance.flag");
        private static bool _isMaintenanceMode = File.Exists(FlagFilePath);

        public static bool IsMaintenanceMode
        {
            get => _isMaintenanceMode;
            private set => _isMaintenanceMode = value;
        }

        public static bool EnableMaintenance(string? storagePath = null)
        {
            try
            {
                File.WriteAllText(FlagFilePath, $"Maintenance enabled at {DateTime.UtcNow:O}");
                IsMaintenanceMode = true;
                Console.WriteLine("[Maintenance] Modo mantenimiento activado persistentemente.");

                // Si se proporcionó una ruta de almacenamiento o es un montaje Linux, intentar sincronizar y desmontar seguro
                SafelyUnmountIfApplicable(storagePath);
                return true;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[Maintenance] Error activando mantenimiento: {ex.Message}");
                return false;
            }
        }

        public static bool DisableMaintenance()
        {
            try
            {
                if (File.Exists(FlagFilePath))
                {
                    File.Delete(FlagFilePath);
                }
                IsMaintenanceMode = false;
                Console.WriteLine("[Maintenance] Modo mantenimiento desactivado.");
                return true;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[Maintenance] Error desactivando mantenimiento: {ex.Message}");
                return false;
            }
        }

        private static bool CheckIsLinux()
        {
#if NETCOREAPP || NET5_0_OR_GREATER
            return OperatingSystem.IsLinux();
#else
            int p = (int)Environment.OSVersion.Platform;
            return (p == 4) || (p == 6) || (p == 128);
#endif
        }

        private static void SafelyUnmountIfApplicable(string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return;

            try
            {
                // Sincronizar buffers al disco en Linux
                if (CheckIsLinux())
                {
                    Console.WriteLine("[Maintenance] Ejecutando sync del sistema de archivos...");
                    using (var syncProc = Process.Start(new ProcessStartInfo
                    {
                        FileName = "sync",
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false
                    }))
                    {
                        syncProc?.WaitForExit(5000);
                    }

                    // Si la ruta es un montaje directo (ej /media/... o /mnt/...), intentar un umount no forzado
                    if (path.StartsWith("/media/") || path.StartsWith("/mnt/"))
                    {
                        Console.WriteLine($"[Maintenance] Intentando desmontar unidad en {path}...");
                        using (var umountProc = Process.Start(new ProcessStartInfo
                        {
                            FileName = "umount",
                            Arguments = path,
                            RedirectStandardOutput = true,
                            RedirectStandardError = true,
                            UseShellExecute = false
                        }))
                        {
                            umountProc?.WaitForExit(5000);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[Maintenance] Nota: Sync/umount preventivo finalizó con: {ex.Message}");
            }
        }
    }
}
