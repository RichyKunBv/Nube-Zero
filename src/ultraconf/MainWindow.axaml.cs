using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using NubeZero.Shared;
using NubeZero.UltraConf.Services;
using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;

namespace NubeZero.UltraConf;

public partial class MainWindow : Window
{
    private readonly DiskMigrationService _migrationService = new();
    private CancellationTokenSource? _migrationCts;
    private static readonly HttpClient _httpClient = new HttpClient();

    public MainWindow()
    {
        InitializeComponent();
        TxtVersion.Text = AppVersion.Texto;
    }

    private async void BtnBrowseSource_Click(object? sender, RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Seleccionar Carpeta o Disco Origen"
        });

        if (folders.Count > 0)
        {
            string path = folders[0].Path.LocalPath;
            TxtSourcePath.Text = path;
            UpdateSourceInfo(path);
        }
    }

    private async void BtnBrowseTarget_Click(object? sender, RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Seleccionar Carpeta o Disco Destino"
        });

        if (folders.Count > 0)
        {
            string path = folders[0].Path.LocalPath;
            TxtTargetPath.Text = path;
            UpdateTargetInfo(path);
        }
    }

    private void UpdateSourceInfo(string path)
    {
        try
        {
            var dir = new DirectoryInfo(path);
            if (dir.Exists)
            {
                TxtSourceInfo.Text = $"Carpeta válida: {dir.FullName}";
            }
        }
        catch (Exception ex)
        {
            TxtSourceInfo.Text = $"Error: {ex.Message}";
        }
    }

    private void UpdateTargetInfo(string path)
    {
        try
        {
            var dir = new DirectoryInfo(path);
            if (!dir.Exists)
            {
                TxtTargetInfo.Text = "La carpeta se creará al iniciar.";
                return;
            }

            var root = Path.GetPathRoot(path);
            if (!string.IsNullOrEmpty(root))
            {
                var drive = new DriveInfo(root);
                if (drive.IsReady)
                {
                    double freeGb = drive.AvailableFreeSpace / 1024.0 / 1024.0 / 1024.0;
                    TxtTargetInfo.Text = $"Espacio libre en unidad: {freeGb:F2} GB";
                    return;
                }
            }
            TxtTargetInfo.Text = "Destino listo.";
        }
        catch (Exception ex)
        {
            TxtTargetInfo.Text = $"Información: {ex.Message}";
        }
    }

    private async void BtnStartMigration_Click(object? sender, RoutedEventArgs e)
    {
        string source = TxtSourcePath.Text?.Trim() ?? "";
        string target = TxtTargetPath.Text?.Trim() ?? "";

        if (string.IsNullOrWhiteSpace(source) || !Directory.Exists(source))
        {
            TxtCurrentFile.Text = "❌ Por favor selecciona una ruta de origen válida existente.";
            return;
        }

        if (string.IsNullOrWhiteSpace(target))
        {
            TxtCurrentFile.Text = "❌ Por favor selecciona una ruta de destino.";
            return;
        }

        if (string.Equals(Path.GetFullPath(source), Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase))
        {
            TxtCurrentFile.Text = "❌ El origen y el destino no pueden ser la misma carpeta.";
            return;
        }

        BtnStartMigration.IsEnabled = false;
        BtnCancel.IsEnabled = true;
        _migrationCts = new CancellationTokenSource();

        TxtProgressPercent.Text = "Analizando archivos y espacio...";
        PbMigration.Value = 0;

        var progress = new Progress<MigrationProgress>(p =>
        {
            Dispatcher.UIThread.Post(() =>
            {
                PbMigration.Value = p.Percentage;
                TxtProgressPercent.Text = $"{p.Percentage:F1}% ({p.FilesCopied}/{p.TotalFiles} archivos) - {p.BytesCopied / 1024 / 1024} MB";
                TxtProgressSpeed.Text = $"{p.SpeedMBs:F2} MB/s";
                TxtCurrentFile.Text = $"Copiando: {p.CurrentFile}";
            });
        });

        try
        {
            await _migrationService.CopyStorageAsync(source, target, progress, _migrationCts.Token);

            TxtProgressPercent.Text = "✅ ¡Transferencia completada con éxito!";
            TxtCurrentFile.Text = "Todos los archivos y marcas de tiempo fueron replicados. Ya puedes conectar el nuevo disco a la Pi.";
            PbMigration.Value = 100;
        }
        catch (OperationCanceledException)
        {
            TxtProgressPercent.Text = "⚠️ Transferencia cancelada por el usuario.";
            TxtCurrentFile.Text = "Se detuvo la copia.";
        }
        catch (Exception ex)
        {
            TxtProgressPercent.Text = "❌ Error durante la transferencia";
            TxtCurrentFile.Text = ex.Message;
        }
        finally
        {
            BtnStartMigration.IsEnabled = true;
            BtnCancel.IsEnabled = false;
            _migrationCts?.Dispose();
            _migrationCts = null;
        }
    }

    private void BtnCancel_Click(object? sender, RoutedEventArgs e)
    {
        _migrationCts?.Cancel();
    }

    private async void BtnCheckServer_Click(object? sender, RoutedEventArgs e)
    {
        string url = TxtServerUrl.Text?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(url)) return;

        try
        {
            TxtServerStatus.Text = "Comprobando estado del servidor...";
            var res = await _httpClient.GetStringAsync($"{url.TrimEnd('/')}/api/status");
            TxtServerStatus.Text = $"Respuesta del servidor: {res}";
        }
        catch (Exception ex)
        {
            TxtServerStatus.Text = $"No se pudo contactar al servidor: {ex.Message}";
        }
    }

    private async void BtnEnableMaint_Click(object? sender, RoutedEventArgs e)
    {
        await SendMaintenanceCommandAsync(enable: true);
    }

    private async void BtnDisableMaint_Click(object? sender, RoutedEventArgs e)
    {
        await SendMaintenanceCommandAsync(enable: false);
    }

    private async void SendMaintenanceCommandAsync(bool enable)
    {
        string url = TxtServerUrl.Text?.Trim() ?? "";
        string token = TxtAdminToken.Text?.Trim() ?? "";

        if (string.IsNullOrWhiteSpace(url))
        {
            TxtServerStatus.Text = "Por favor ingresa la URL del servidor.";
            return;
        }

        try
        {
            string endpoint = enable ? "/api/system/maintenance/enable" : "/api/system/maintenance/disable";
            using var req = new HttpRequestMessage(HttpMethod.Post, $"{url.TrimEnd('/')}{endpoint}");
            if (!string.IsNullOrWhiteSpace(token))
            {
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            }

            var res = await _httpClient.SendAsync(req);
            string body = await res.Content.ReadAsStringAsync();
            TxtServerStatus.Text = $"Resultado ({(int)res.StatusCode}): {body}";
        }
        catch (Exception ex)
        {
            TxtServerStatus.Text = $"Error enviando comando: {ex.Message}";
        }
    }
}
