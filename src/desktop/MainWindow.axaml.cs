using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using NubeZero.Shared;

namespace NubeZero.Desktop;

public partial class MainWindow : Window
{
    private HttpClient _httpClient = new HttpClient
    {
        BaseAddress = new Uri("https://localhost:8080"),
        Timeout = TimeSpan.FromHours(2)
    };
    private string _token = string.Empty;
    private string _username = string.Empty;
    private string _role = "Estandar";
    private byte[] _encryptionKey = Array.Empty<byte>();
    private string _currentPath = string.Empty;
    private string _transferMessage = string.Empty;
    private long _lastProgressUpdate;
    private readonly ConcurrentDictionary<string, bool> _thumbnailRequests = new ConcurrentDictionary<string, bool>();
    private readonly SemaphoreSlim _thumbnailSlots = new SemaphoreSlim(2, 2);
    private bool _hasSavedMacCredentials;
    private bool _isNotesTabActive = false;

    public MainWindow()
    {
        InitializeComponent();
        
        // Agregar recursos de conversores dinámicamente
        Resources.Add("BoolToIconConverter", new BoolToIconConverter());
        Resources.Add("BytesToSizeConverter", new BytesToSizeConverter());

        ChkRememberPassword.IsVisible = OperatingSystem.IsMacOS();
        try
        {
            var connectionSettings = ServerConnectionSettings.Load();
            if (connectionSettings != null)
            {
                TxtServerUrl.Text = connectionSettings.ServerUrl;
                TxtServerFingerprint.Text = connectionSettings.CertificateFingerprint;
            }
        }
        catch (Exception ex)
        {
            ShowLoginError($"No se pudo recuperar la conexión guardada: {ex.Message}");
        }

        if (OperatingSystem.IsMacOS())
        {
            try
            {
                var savedCredentials = MacOsCredentialStore.Load();
                if (savedCredentials != null)
                {
                    _hasSavedMacCredentials = true;
                    TxtServerUrl.Text = savedCredentials.ServerUrl;
                    TxtServerFingerprint.Text = savedCredentials.CertificateFingerprint;
                    TxtUser.Text = savedCredentials.Username;
                    TxtPassword.Text = savedCredentials.Password;
                    ChkRememberPassword.IsChecked = true;
                }
            }
            catch (Exception ex)
            {
                ShowLoginError($"No se pudo acceder al llavero de macOS: {ex.Message}");
            }
        }
        
        // Evento Global de Drag & Drop
        AddHandler(DragDrop.DropEvent, OnDrop);
    }

    private async void BtnDiscover_Click(object? sender, RoutedEventArgs e)
    {
        BtnDiscover.IsEnabled = false;
        BtnDiscover.Content = "⏳";
        TxtDiscoveryStatus.Text = "Buscando servidores en la red local...";
        TxtDiscoveryStatus.IsVisible = true;
        CmbDiscoveredServers.IsVisible = false;

        try
        {
            var servers = await NetworkDiscoveryClient.DiscoverServersAsync(timeoutMs: 1500);
            if (servers.Count == 0)
            {
                TxtDiscoveryStatus.Text = "No se detectaron servidores. Ingresa la IP manualmente.";
            }
            else if (servers.Count == 1)
            {
                var s = servers[0];
                TxtServerUrl.Text = $"https://{s.IpAddress}:{s.Port}";
                TxtDiscoveryStatus.Text = $"✓ Servidor encontrado: {s.ServerName} ({s.IpAddress})";
            }
            else
            {
                TxtDiscoveryStatus.Text = $"Se encontraron {servers.Count} servidores:";
                CmbDiscoveredServers.Items.Clear();
                foreach (var s in servers)
                {
                    CmbDiscoveredServers.Items.Add(new ComboBoxItem
                    {
                        Content = $"☁️ {s.ServerName} ({s.IpAddress}:{s.Port}) - {s.Version}",
                        Tag = $"https://{s.IpAddress}:{s.Port}"
                    });
                }
                CmbDiscoveredServers.IsVisible = true;
                CmbDiscoveredServers.SelectedIndex = 0;
            }
        }
        catch (Exception ex)
        {
            TxtDiscoveryStatus.Text = $"Error al buscar: {ex.Message}";
        }
        finally
        {
            BtnDiscover.IsEnabled = true;
            BtnDiscover.Content = "🔍";
        }
    }

    private void CmbDiscoveredServers_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (CmbDiscoveredServers.SelectedItem is ComboBoxItem item && item.Tag is string url)
        {
            TxtServerUrl.Text = url;
        }
    }

    private async void BtnLogin_Click(object? sender, RoutedEventArgs e)
    {
        string user = TxtUser.Text?.Trim() ?? "";
        string pass = TxtPassword.Text ?? "";
        string serverUrl = TxtServerUrl.Text?.Trim() ?? "https://localhost:8080";
        string fingerprint = TxtServerFingerprint.Text?.Trim() ?? string.Empty;

        if (string.IsNullOrEmpty(user) || string.IsNullOrEmpty(pass))
        {
            ShowLoginError("Por favor, ingresa credenciales.");
            return;
        }

        try
        {
            if (!serverUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                && !serverUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                serverUrl = "https://" + serverUrl;

            if (!Uri.TryCreate(serverUrl, UriKind.Absolute, out var serverUri)
                || serverUri.Scheme != Uri.UriSchemeHttps
                || !string.IsNullOrEmpty(serverUri.UserInfo))
            {
                ShowLoginError("La conexión debe usar HTTPS y una URL válida.");
                return;
            }

            if (string.IsNullOrWhiteSpace(fingerprint))
            {
                ShowLoginError("Verifica por SSH e introduce la huella SHA-256 del certificado del servidor.");
                return;
            }

            ServerConnectionSettings.Save(serverUrl, fingerprint);

            BtnLogin.IsEnabled = false;
            BtnLogin.Content = "Conectando...";
            ShowLoginError("Validando credenciales; en una Raspberry Pi Zero puede tardar un poco.");

            _httpClient.Dispose();
            var handler = new SocketsHttpHandler
            {
                PooledConnectionLifetime = TimeSpan.FromMinutes(15),
                ConnectTimeout = TimeSpan.FromSeconds(30)
            };
            handler.SslOptions.RemoteCertificateValidationCallback = PinnedCertificateHandler.CreateCallback(fingerprint);
            _httpClient = new HttpClient(handler)
            {
                BaseAddress = serverUri,
                Timeout = TimeSpan.FromHours(2)
            };
            
            var loginData = new { Username = user, Password = pass };
            var content = new StringContent(JsonSerializer.Serialize(loginData), Encoding.UTF8, "application/json");
            
            using var response = await _httpClient.PostAsync("/api/login", content);
            if (response.IsSuccessStatusCode)
            {
                var json = await response.Content.ReadAsStringAsync();
                var result = JsonSerializer.Deserialize<JsonElement>(json);
                _token = result.GetProperty("token").GetString() ?? "";
                _username = result.TryGetProperty("username", out var uProp) ? uProp.GetString() ?? user : user;
                _role = result.TryGetProperty("role", out var rProp) ? rProp.GetString() ?? "Estandar" : "Estandar";

                if (OperatingSystem.IsMacOS())
                {
                    try
                    {
                        if (ChkRememberPassword.IsChecked == true)
                        {
                            MacOsCredentialStore.Save(serverUrl, _username, pass, fingerprint);
                            _hasSavedMacCredentials = true;
                        }
                        else if (_hasSavedMacCredentials)
                        {
                            MacOsCredentialStore.Delete();
                            _hasSavedMacCredentials = false;
                        }
                    }
                    catch (Exception ex)
                    {
                        ShowLoginError($"Sesión iniciada, pero no se pudo guardar en el llavero: {ex.Message}");
                    }
                }
                
                _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _token);
                await LoadEncryptionKeyAsync();
                _currentPath = string.Empty;
                
                // Actualizar interfaz según el rol
                TxtUserInfo.Text = $"👤 {_username} [{_role}]";
                BtnUsersAdmin.IsVisible = (_role == "Admin");
                BtnUploadFab.IsVisible = (_role != "Visitante");

                LoginView.IsVisible = false;
                MainView.IsVisible = true;
                
                await LoadFilesAsync();
            }
            else
            {
                ShowLoginError(await GetApiFailureMessageAsync(response));
            }
        }
        catch (Exception ex)
        {
            ShowLoginError($"No se pudo completar la conexión: {ex.Message}");
        }
        finally
        {
            BtnLogin.IsEnabled = true;
            BtnLogin.Content = "Conectar";
        }
    }

    private static async Task<string> GetApiFailureMessageAsync(HttpResponseMessage response)
    {
        string responseBody = await response.Content.ReadAsStringAsync();
        string detail = string.Empty;
        try
        {
            using var document = JsonDocument.Parse(responseBody);
            if (document.RootElement.TryGetProperty("error", out var error))
                detail = error.GetString() ?? string.Empty;
        }
        catch (JsonException)
        {
        }

        if (string.IsNullOrWhiteSpace(detail) && !string.IsNullOrWhiteSpace(responseBody))
        {
            detail = responseBody.Trim();
            if (detail.Length > 300) detail = detail.Substring(0, 300) + "…";
        }

        return string.IsNullOrWhiteSpace(detail)
            ? $"El servidor respondió con HTTP {(int)response.StatusCode} ({response.ReasonPhrase})."
            : $"El servidor respondió con HTTP {(int)response.StatusCode}: {detail}";
    }

    private async void BtnCheckUpdates_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button button) button.IsEnabled = false;
        SetUpdateStatus("Buscando actualización...");

        try
        {
            string assetName = UpdateService.GetCurrentAssetName();
            var result = await UpdateService.CheckForUpdatesAsync(AppVersion.Texto, assetName);

            if (result.status == UpdateStatus.UpToDate)
            {
                SetUpdateStatus($"Ya tienes la versión {AppVersion.Texto}.");
            }
            else if (result.status == UpdateStatus.Newer)
            {
                SetUpdateStatus("Esta versión es más nueva que la última publicada.");
            }
            else if (result.status == UpdateStatus.Outdated && result.downloadUrl != null)
            {
                string tempDir = Path.Combine(Path.GetTempPath(), "NubeZeroUpdates");
                Directory.CreateDirectory(tempDir);
                string installerPath = Path.Combine(tempDir, assetName);

                var progress = new Progress<(long bytesRead, long? totalBytes)>(p =>
                {
                    if (p.totalBytes.HasValue && p.totalBytes.Value > 0)
                    {
                        double percent = (double)p.bytesRead / p.totalBytes.Value * 100;
                        double mbRead = p.bytesRead / (1024.0 * 1024.0);
                        double mbTotal = p.totalBytes.Value / (1024.0 * 1024.0);
                        SetUpdateStatus($"Descargando v{result.latestVersion} ({percent:F0}% - {mbRead:F1}/{mbTotal:F1} MB)...");
                    }
                    else
                    {
                        double mbRead = p.bytesRead / (1024.0 * 1024.0);
                        SetUpdateStatus($"Descargando v{result.latestVersion} ({mbRead:F1} MB)...");
                    }
                });

                SetUpdateStatus($"Descargando v{result.latestVersion}...");
                await UpdateService.DownloadFileAsync(result.downloadUrl, installerPath, progress);

                SetUpdateStatus("Instalador descargado. Abriendo instalador...");
                UpdateService.LaunchInstaller(installerPath);
                SetUpdateStatus("Instalador iniciado. Sigue los pasos para actualizar.");
            }
            else if (result.status == UpdateStatus.Outdated)
            {
                SetUpdateStatus($"v{result.latestVersion} está disponible, pero no hay instalador para {assetName}.");
            }
            else
            {
                SetUpdateStatus("No se pudo consultar GitHub. Revisa tu conexión e inténtalo de nuevo.");
            }
        }
        catch (Exception ex)
        {
            SetUpdateStatus($"No se pudo actualizar: {ex.Message}");
        }
        finally
        {
            if (sender is Button updateButton) updateButton.IsEnabled = true;
        }
    }

    private void SetUpdateStatus(string message)
    {
        if (LoginView.IsVisible)
        {
            TxtUpdateStatus.Text = message;
            TxtUpdateStatus.IsVisible = true;
        }
        else
        {
            TxtStatus.Text = message;
        }
    }

    private void ShowLoginError(string msg)
    {
        TxtLoginError.Text = msg;
        TxtLoginError.IsVisible = true;
    }

    private async void BtnRefresh_Click(object? sender, RoutedEventArgs e)
    {
        if (_isNotesTabActive)
        {
            await LoadNotesAsync();
        }
        else
        {
            await LoadFilesAsync();
        }
    }

    private async void BtnTabFiles_Click(object? sender, RoutedEventArgs e)
    {
        _isNotesTabActive = false;
        BtnTabFiles.Background = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#00D2FF"));
        BtnTabFiles.Foreground = Avalonia.Media.Brushes.White;
        BtnTabFiles.FontWeight = Avalonia.Media.FontWeight.Bold;

        BtnTabNotes.Background = Avalonia.Media.Brushes.Transparent;
        BtnTabNotes.Foreground = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#A0A0A0"));
        BtnTabNotes.FontWeight = Avalonia.Media.FontWeight.SemiBold;

        FilesContainer.IsVisible = true;
        NotesContainer.IsVisible = false;
        BtnUploadFab.IsVisible = (_role != "Visitante");

        await LoadFilesAsync();
    }

    private async void BtnTabNotes_Click(object? sender, RoutedEventArgs e)
    {
        _isNotesTabActive = true;
        BtnTabNotes.Background = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#00D2FF"));
        BtnTabNotes.Foreground = Avalonia.Media.Brushes.White;
        BtnTabNotes.FontWeight = Avalonia.Media.FontWeight.Bold;

        BtnTabFiles.Background = Avalonia.Media.Brushes.Transparent;
        BtnTabFiles.Foreground = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#A0A0A0"));
        BtnTabFiles.FontWeight = Avalonia.Media.FontWeight.SemiBold;

        FilesContainer.IsVisible = false;
        NotesContainer.IsVisible = true;
        BtnUploadFab.IsVisible = false;

        await LoadNotesAsync();
    }

    private async System.Threading.Tasks.Task LoadNotesAsync()
    {
        try
        {
            TxtStatus.Text = "Cargando notas...";
            var response = await _httpClient.GetAsync("/api/notes");
            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            {
                MainView.IsVisible = false;
                LoginView.IsVisible = true;
                ShowLoginError("Sesión expirada. Vuelve a iniciar sesión.");
                return;
            }

            if (response.IsSuccessStatusCode)
            {
                var json = await response.Content.ReadAsStringAsync();
                var notes = JsonSerializer.Deserialize<List<NotaDTO>>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new List<NotaDTO>();
                
                LstNotes.ItemsSource = notes;
                TxtNoNotes.IsVisible = (notes.Count == 0);
                TxtStatus.Text = $"Conectado ({notes.Count} notas)";

                Dispatcher.UIThread.Post(() =>
                {
                    ScrollNotes.ScrollToEnd();
                }, DispatcherPriority.Background);
            }
            else
            {
                TxtStatus.Text = "Error al sincronizar notas.";
            }
        }
        catch (Exception ex)
        {
            TxtStatus.Text = $"Error: {ex.Message}";
        }
    }

    private async void BtnSendNote_Click(object? sender, RoutedEventArgs e)
    {
        await SendNoteAsync();
    }

    private async void TxtNoteInput_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && !e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            e.Handled = true;
            await SendNoteAsync();
        }
    }

    private async System.Threading.Tasks.Task SendNoteAsync()
    {
        string text = TxtNoteInput.Text?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(text)) return;

        BtnSendNote.IsEnabled = false;
        try
        {
            var payload = new { contenido = text };
            var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
            var response = await _httpClient.PostAsync("/api/notes", content);

            if (response.IsSuccessStatusCode)
            {
                TxtNoteInput.Text = string.Empty;
                await LoadNotesAsync();
            }
            else
            {
                TxtStatus.Text = "No se pudo guardar la nota.";
            }
        }
        catch (Exception ex)
        {
            TxtStatus.Text = $"Error al enviar nota: {ex.Message}";
        }
        finally
        {
            BtnSendNote.IsEnabled = true;
        }
    }

    private async void BtnCopyNote_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.DataContext is NotaDTO nota)
        {
            try
            {
                var topLevel = TopLevel.GetTopLevel(this);
                if (topLevel?.Clipboard != null)
                {
                    await topLevel.Clipboard.SetTextAsync(nota.Contenido);
                    string originalContent = btn.Content?.ToString() ?? "📋 Copiar";
                    btn.Content = "¡Copiado! ✅";
                    _ = System.Threading.Tasks.Task.Delay(1500).ContinueWith(_ =>
                    {
                        Dispatcher.UIThread.Post(() => btn.Content = originalContent);
                    });
                }
            }
            catch (Exception ex)
            {
                TxtStatus.Text = $"No se pudo copiar: {ex.Message}";
            }
        }
    }

    private async void BtnDeleteNote_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.DataContext is NotaDTO nota)
        {
            try
            {
                var response = await _httpClient.DeleteAsync($"/api/notes?id={Uri.EscapeDataString(nota.Id)}");
                if (response.IsSuccessStatusCode)
                {
                    await LoadNotesAsync();
                }
                else
                {
                    TxtStatus.Text = "No se pudo eliminar la nota.";
                }
            }
            catch (Exception ex)
            {
                TxtStatus.Text = $"Error al eliminar: {ex.Message}";
            }
        }
    }

    private async System.Threading.Tasks.Task LoadFilesAsync()
    {
        try
        {
            TxtStatus.Text = "Sincronizando...";
            var response = await _httpClient.GetAsync($"/api/files?path={Uri.EscapeDataString(_currentPath)}");
            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            {
                MainView.IsVisible = false;
                LoginView.IsVisible = true;
                ShowLoginError("Sesión expirada. Ingresa de nuevo.");
                return;
            }
            
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException(await GetApiFailureMessageAsync(response));

            var json = await response.Content.ReadAsStringAsync();
            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            var files = JsonSerializer.Deserialize<List<ArchivoDTO>>(json, options);
            
            LstFiles.ItemsSource = files;
            TxtCurrentPath.Text = string.IsNullOrEmpty(_currentPath) ? "/" : "/" + _currentPath;
            BtnParentFolder.IsVisible = !string.IsNullOrEmpty(_currentPath);
            TxtStatus.Text = $"{files?.Count ?? 0} elementos";
            TxtDragHint.IsVisible = (files == null || files.Count == 0);
        }
        catch (Exception ex)
        {
            TxtStatus.Text = $"Error: {ex.Message}";
        }
    }

    private async void LstFiles_DoubleTapped(object? sender, TappedEventArgs e)
    {
        if (LstFiles.SelectedItem is ArchivoDTO { EsCarpeta: true } folder)
        {
            _currentPath = string.IsNullOrEmpty(_currentPath) ? folder.Nombre : $"{_currentPath}/{folder.Nombre}";
            await LoadFilesAsync();
        }
    }

    private async void BtnParentFolder_Click(object? sender, RoutedEventArgs e)
    {
        int separator = _currentPath.LastIndexOf('/');
        _currentPath = separator < 0 ? string.Empty : _currentPath.Substring(0, separator);
        await LoadFilesAsync();
    }

    private string GetRemotePath(string name)
    {
        string path = string.IsNullOrEmpty(_currentPath) ? name : $"{_currentPath}/{name}";
        return "/" + path;
    }

    private void FileRow_AttachedToVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (sender is Control control && control.DataContext is ArchivoDTO file)
        {
            _ = LoadThumbnailAsync(file);
        }
    }

    private async System.Threading.Tasks.Task LoadThumbnailAsync(ArchivoDTO file)
    {
        if (file.ThumbnailSource != null) return;
        string extension = Path.GetExtension(file.Nombre).ToLowerInvariant();
        bool isImage = new[] { ".jpg", ".jpeg", ".png", ".gif", ".webp", ".bmp" }.Contains(extension);
        bool isVideo = new[] { ".mp4", ".mov", ".mkv", ".avi", ".webm", ".m4v", ".3gp", ".wmv", ".mpeg", ".mpg" }.Contains(extension);
        long maxSourceBytes = isVideo ? 32 * 1024 * 1024 : 20 * 1024 * 1024;
        if (file.EsCarpeta || file.PesoBytes > maxSourceBytes || (!isImage && !isVideo)) return;

        string cacheDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NubeZero", "thumbnails");
        Directory.CreateDirectory(cacheDirectory);
        string cacheKey = $"{_httpClient.BaseAddress}|{_currentPath}|{file.Nombre}|{file.PesoBytes}|{file.FechaModificacion.Ticks}";
        string cachePath = Path.Combine(cacheDirectory, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(cacheKey))) + ".png");
        if (File.Exists(cachePath))
        {
            var cachedBitmap = await System.Threading.Tasks.Task.Run(() => new Bitmap(cachePath)).ConfigureAwait(false);
            Dispatcher.UIThread.Post(() => file.ThumbnailSource = cachedBitmap);
            return;
        }
        if (!_thumbnailRequests.TryAdd(cachePath, true)) return;

        string sourcePath = cachePath + ".source";
        string encryptedPath = sourcePath + ".encrypted";
        bool hasThumbnailSlot = false;
        try
        {
            await _thumbnailSlots.WaitAsync().ConfigureAwait(false);
            hasThumbnailSlot = true;
            string remotePath = Uri.EscapeDataString(GetRemotePath(file.Nombre));
            using var response = await _httpClient.GetAsync($"/api/download?path={remotePath}", HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength > maxSourceBytes + 80) return;

            using var input = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
            using (var output = new FileStream(encryptedPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true))
            {
                var buffer = new byte[81920];
                long downloaded = 0;
                int count;
                while ((count = await input.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false)) > 0)
                {
                    downloaded += count;
                    if (downloaded > maxSourceBytes + 80) return;
                    await output.WriteAsync(buffer, 0, count).ConfigureAwait(false);
                }
            }

            using (var encrypted = new FileStream(encryptedPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var plaintext = new FileStream(sourcePath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true))
            {
                await FileEncryptionService.DecryptAsync(encrypted, plaintext, _encryptionKey).ConfigureAwait(false);
            }
            if (new FileInfo(sourcePath).Length > maxSourceBytes) return;

            if (isVideo)
            {
                if (!await TryCreateVideoThumbnailAsync(sourcePath, cachePath).ConfigureAwait(false)) return;
            }
            else
            {
                using (var source = File.OpenRead(sourcePath))
                using (var bitmap = Bitmap.DecodeToWidth(source, 112, BitmapInterpolationMode.MediumQuality))
                {
                    bitmap.Save(cachePath, new PngBitmapEncoderOptions());
                }
            }

            TrimThumbnailCache(cacheDirectory);
            var thumbnail = await System.Threading.Tasks.Task.Run(() => new Bitmap(cachePath)).ConfigureAwait(false);
            Dispatcher.UIThread.Post(() => file.ThumbnailSource = thumbnail);
        }
        catch
        {
        }
        finally
        {
            _thumbnailRequests.TryRemove(cachePath, out _);
            if (File.Exists(sourcePath)) File.Delete(sourcePath);
            if (File.Exists(encryptedPath)) File.Delete(encryptedPath);
            if (hasThumbnailSlot) _thumbnailSlots.Release();
        }
    }

    private static async System.Threading.Tasks.Task<bool> TryCreateVideoThumbnailAsync(string sourcePath, string thumbnailPath)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "ffmpeg",
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("-v");
        startInfo.ArgumentList.Add("quiet");
        startInfo.ArgumentList.Add("-i");
        startInfo.ArgumentList.Add(sourcePath);
        startInfo.ArgumentList.Add("-frames:v");
        startInfo.ArgumentList.Add("1");
        startInfo.ArgumentList.Add("-vf");
        startInfo.ArgumentList.Add("scale=112:-1");
        startInfo.ArgumentList.Add("-y");
        startInfo.ArgumentList.Add(thumbnailPath);

        using var process = Process.Start(startInfo);
        if (process == null) return false;

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            return process.ExitCode == 0 && File.Exists(thumbnailPath);
        }
        catch
        {
            try { process.Kill(true); } catch { }
            return false;
        }
    }

    private static void TrimThumbnailCache(string cacheDirectory)
    {
        foreach (var file in new DirectoryInfo(cacheDirectory).GetFiles("*.png").OrderByDescending(file => file.LastWriteTimeUtc).Skip(80))
        {
            try { file.Delete(); } catch { }
        }
    }

    private async void OnDrop(object? sender, DragEventArgs e)
    {
        if (LoginView.IsVisible) return; // No permitir drop en login
        
        if (_role == "Visitante")
        {
            TxtStatus.Text = "Permiso denegado: Los visitantes solo pueden descargar.";
            return;
        }

        var files = e.DataTransfer.TryGetFiles();
        if (files == null) return;

        foreach (var file in files)
        {
            var localPath = file.TryGetLocalPath();
            if (!string.IsNullOrEmpty(localPath))
            {
                await UploadFileAsync(localPath);
            }
        }
        
        await LoadFilesAsync();
    }

    private async void BtnUploadManual_Click(object? sender, RoutedEventArgs e)
    {
        if (_role == "Visitante")
        {
            TxtStatus.Text = "Permiso denegado: Los visitantes solo pueden descargar.";
            return;
        }

        var topLevel = TopLevel.GetTopLevel(this);
        var files = await topLevel!.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Seleccionar archivos para subir",
            AllowMultiple = true
        });

        foreach (var file in files)
        {
            var localPath = file.TryGetLocalPath();
            if (!string.IsNullOrEmpty(localPath))
            {
                await UploadFileAsync(localPath);
            }
        }
        
        await LoadFilesAsync();
    }

    private async System.Threading.Tasks.Task UploadFileAsync(string localPath)
    {
        try
        {
            string fileName = Path.GetFileName(localPath);
            ShowTransfer($"Subiendo {fileName}...");

            var fi = new FileInfo(localPath);
            long fileLength = fi.Length;

            using var fs = new FileStream(localPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
            using var content = new EncryptedProgressStreamContent(fs, _encryptionKey, UpdateTransferProgress, fileLength);

            string relativePath = Uri.EscapeDataString(GetRemotePath(fileName));
            using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/upload?path={relativePath}")
            {
                Content = content
            };

            if (fileLength >= 0)
            {
                request.Headers.TransferEncodingChunked = false;
            }

            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();
            TxtStatus.Text = "Subida exitosa";
        }
        catch (Exception ex)
        {
            TxtStatus.Text = $"Error al subir: {ex.Message}";
        }
        finally
        {
            HideTransfer();
        }
    }

    private async void BtnDownload_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is MenuItem menuItem && menuItem.DataContext is ArchivoDTO dto)
        {
            if (dto.EsCarpeta)
            {
                TxtStatus.Text = "Descarga de carpetas no soportada aún.";
                return;
            }

            var topLevel = TopLevel.GetTopLevel(this);
            var folder = await topLevel!.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "Seleccionar carpeta de destino"
            });

            if (folder.Count > 0)
            {
                string destPath = Path.Combine(folder[0].TryGetLocalPath()!, dto.Nombre);
                string temporaryPath = destPath + ".nubezero-" + Guid.NewGuid().ToString("N") + ".part";
                string encryptedTemporaryPath = temporaryPath + ".encrypted";
                ShowTransfer($"Descargando {dto.Nombre}...");
                
                try
                {
                    string relativePath = Uri.EscapeDataString(GetRemotePath(dto.Nombre));
                    using var response = await _httpClient.GetAsync($"/api/download?path={relativePath}", HttpCompletionOption.ResponseHeadersRead);
                    response.EnsureSuccessStatusCode();
                    
                    long total = response.Content.Headers.ContentLength ?? -1;
                    long transferred = 0;
                    using var input = await response.Content.ReadAsStreamAsync();
                    using (var fs = new FileStream(encryptedTemporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
                    {
                        var buffer = new byte[81920];
                        int count;
                        while ((count = await input.ReadAsync(buffer, 0, buffer.Length)) > 0)
                        {
                            await fs.WriteAsync(buffer, 0, count);
                            transferred += count;
                            UpdateTransferProgress(transferred, total);
                        }
                        await fs.FlushAsync();
                    }

                    if (total >= 0 && transferred != total)
                        throw new IOException("La descarga llegó incompleta.");

                    using (var encrypted = new FileStream(encryptedTemporaryPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                    using (var plaintext = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
                    {
                        await FileEncryptionService.DecryptAsync(encrypted, plaintext, _encryptionKey);
                    }

                    File.Move(temporaryPath, destPath, true);
                    TxtStatus.Text = "Descarga completada";
                }
                catch (Exception ex)
                {
                    TxtStatus.Text = $"Error descarga: {ex.Message}";
                }
                finally
                {
                    if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
                    if (File.Exists(encryptedTemporaryPath)) File.Delete(encryptedTemporaryPath);
                    HideTransfer();
                }
            }
        }
    }

    private async void BtnDelete_Click(object? sender, RoutedEventArgs e)
    {
        if (_role == "Visitante")
        {
            TxtStatus.Text = "Permiso denegado: Los visitantes no pueden eliminar archivos.";
            return;
        }

        if (sender is MenuItem menuItem && menuItem.DataContext is ArchivoDTO dto)
        {
            try
            {
                string relativePath = Uri.EscapeDataString(GetRemotePath(dto.Nombre));
                var response = await _httpClient.DeleteAsync($"/api/delete?path={relativePath}");
                response.EnsureSuccessStatusCode();
                TxtStatus.Text = "Eliminado";
                await LoadFilesAsync();
            }
            catch (Exception ex)
            {
                TxtStatus.Text = $"Error al eliminar: {ex.Message}";
            }
        }
    }

    private void ShowTransfer(string message)
    {
        Interlocked.Exchange(ref _lastProgressUpdate, 0);
        _transferMessage = message;
        TxtTransferStatus.Text = _transferMessage;
        PrgTransfer.Value = 0;
        TransferPanel.IsVisible = true;
    }

    private void UpdateTransferProgress(long transferred, long total)
    {
        long now = Environment.TickCount64;
        if (total > transferred && now - Interlocked.Read(ref _lastProgressUpdate) < 150) return;
        Interlocked.Exchange(ref _lastProgressUpdate, now);

        Dispatcher.UIThread.Post(() =>
        {
            if (total > 0)
            {
                PrgTransfer.Value = Math.Min(100, transferred * 100d / total);
                if (transferred >= total)
                {
                    string nextStep = _transferMessage.StartsWith("Subiendo", StringComparison.Ordinal)
                        ? "esperando confirmación del servidor"
                        : "verificando descarga";
                    TxtTransferStatus.Text = $"{_transferMessage} • {nextStep}";
                }
                else
                {
                    TxtTransferStatus.Text = $"{_transferMessage} • {transferred / 1048576d:0.#} / {total / 1048576d:0.#} MB";
                }
            }
        });
    }

    private void HideTransfer()
    {
        Dispatcher.UIThread.Post(() => TransferPanel.IsVisible = false);
    }

    private void BtnLogout_Click(object? sender, RoutedEventArgs e)
    {
        _token = string.Empty;
        _username = string.Empty;
        _role = "Estandar";
        Array.Clear(_encryptionKey, 0, _encryptionKey.Length);
        _encryptionKey = Array.Empty<byte>();
        _currentPath = string.Empty;

        if (_httpClient != null)
        {
            _httpClient.DefaultRequestHeaders.Authorization = null;
        }
        
        TxtUser.Text = string.Empty;
        TxtPassword.Text = string.Empty;
        TxtLoginError.IsVisible = false;
        
        UsersAdminView.IsVisible = false;
        ChangePasswordView.IsVisible = false;
        MainView.IsVisible = false;
        LoginView.IsVisible = true;
    }

    private async Task LoadEncryptionKeyAsync()
    {
        using var response = await _httpClient.GetAsync("/api/encryption-key");
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"No se pudo obtener la clave de cifrado: {await GetApiFailureMessageAsync(response)}");

        var result = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
        string encodedKey = result.GetProperty("key").GetString() ?? string.Empty;
        byte[] key;
        try
        {
            key = Convert.FromBase64String(encodedKey);
        }
        catch (FormatException ex)
        {
            throw new InvalidDataException("El servidor devolvió una clave de cifrado inválida.", ex);
        }

        if (key.Length != 32)
        {
            Array.Clear(key, 0, key.Length);
            throw new InvalidDataException("La clave de cifrado del servidor debe contener 32 bytes.");
        }

        Array.Clear(_encryptionKey, 0, _encryptionKey.Length);
        _encryptionKey = key;
    }

    #region Gestión de Usuarios (Admin)

    private async void BtnUsersAdmin_Click(object? sender, RoutedEventArgs e)
    {
        TxtUserAdminError.IsVisible = false;
        TxtNewUser.Text = string.Empty;
        TxtNewPassword.Text = string.Empty;
        UsersAdminView.IsVisible = true;
        await LoadUsersAdminAsync();
    }

    private void BtnCloseUsersAdmin_Click(object? sender, RoutedEventArgs e)
    {
        UsersAdminView.IsVisible = false;
    }

    private async System.Threading.Tasks.Task LoadUsersAdminAsync()
    {
        try
        {
            var response = await _httpClient.GetAsync("/api/users");
            if (response.IsSuccessStatusCode)
            {
                var json = await response.Content.ReadAsStringAsync();
                var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                var users = JsonSerializer.Deserialize<List<UserDTO>>(json, options);
                LstUsersAdmin.ItemsSource = users;
            }
        }
        catch (Exception ex)
        {
            TxtUserAdminError.Text = $"Error al cargar usuarios: {ex.Message}";
            TxtUserAdminError.IsVisible = true;
        }
    }

    private async void BtnConfirmAddUser_Click(object? sender, RoutedEventArgs e)
    {
        string user = TxtNewUser.Text?.Trim() ?? "";
        string pass = TxtNewPassword.Text?.Trim() ?? "";
        string role = (CmbNewUserRole.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Estandar";

        if (string.IsNullOrEmpty(user) || string.IsNullOrEmpty(pass))
        {
            TxtUserAdminError.Text = "Llena todos los campos.";
            TxtUserAdminError.IsVisible = true;
            return;
        }

        try
        {
            var registerData = new { Username = user, Password = pass, Role = role };
            var content = new StringContent(JsonSerializer.Serialize(registerData), Encoding.UTF8, "application/json");
            
            var response = await _httpClient.PostAsync("/api/users/add", content);
            if (response.IsSuccessStatusCode)
            {
                TxtNewUser.Text = string.Empty;
                TxtNewPassword.Text = string.Empty;
                TxtUserAdminError.IsVisible = false;
                await LoadUsersAdminAsync();
            }
            else if (response.StatusCode == System.Net.HttpStatusCode.Conflict)
            {
                TxtUserAdminError.Text = "El usuario ya existe.";
                TxtUserAdminError.IsVisible = true;
            }
            else
            {
                var errJson = await response.Content.ReadAsStringAsync();
                TxtUserAdminError.Text = $"Error: {errJson}";
                TxtUserAdminError.IsVisible = true;
            }
        }
        catch (Exception ex)
        {
            TxtUserAdminError.Text = $"Error: {ex.Message}";
            TxtUserAdminError.IsVisible = true;
        }
    }

    private async void BtnDeleteUserAdmin_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string targetUsername)
        {
            if (targetUsername.Equals(_username, StringComparison.OrdinalIgnoreCase))
            {
                TxtUserAdminError.Text = "No puedes eliminar tu propia cuenta.";
                TxtUserAdminError.IsVisible = true;
                return;
            }

            try
            {
                var response = await _httpClient.DeleteAsync($"/api/users/delete?username={Uri.EscapeDataString(targetUsername)}");
                if (response.IsSuccessStatusCode)
                {
                    TxtUserAdminError.IsVisible = false;
                    await LoadUsersAdminAsync();
                }
                else
                {
                    var json = await response.Content.ReadAsStringAsync();
                    TxtUserAdminError.Text = $"Error: {json}";
                    TxtUserAdminError.IsVisible = true;
                }
            }
            catch (Exception ex)
            {
                TxtUserAdminError.Text = $"Error: {ex.Message}";
                TxtUserAdminError.IsVisible = true;
            }
        }
    }

    #endregion

    #region Cambio de Contraseña

    private void BtnOpenChangePassword_Click(object? sender, RoutedEventArgs e)
    {
        TxtCurrentPassword.Text = string.Empty;
        TxtNewPasswordChange.Text = string.Empty;
        TxtConfirmPasswordChange.Text = string.Empty;
        TxtChangePasswordError.IsVisible = false;
        ChangePasswordView.IsVisible = true;
    }

    private void BtnCloseChangePassword_Click(object? sender, RoutedEventArgs e)
    {
        ChangePasswordView.IsVisible = false;
    }

    private async void BtnConfirmChangePassword_Click(object? sender, RoutedEventArgs e)
    {
        string currentPass = TxtCurrentPassword.Text?.Trim() ?? "";
        string newPass = TxtNewPasswordChange.Text?.Trim() ?? "";
        string confirmPass = TxtConfirmPasswordChange.Text?.Trim() ?? "";

        if (string.IsNullOrEmpty(currentPass) || string.IsNullOrEmpty(newPass))
        {
            TxtChangePasswordError.Text = "Por favor completa todos los campos.";
            TxtChangePasswordError.IsVisible = true;
            return;
        }

        if (newPass != confirmPass)
        {
            TxtChangePasswordError.Text = "Las nuevas contraseñas no coinciden.";
            TxtChangePasswordError.IsVisible = true;
            return;
        }

        try
        {
            var reqData = new { CurrentPassword = currentPass, NewPassword = newPass };
            var content = new StringContent(JsonSerializer.Serialize(reqData), Encoding.UTF8, "application/json");

            var response = await _httpClient.PostAsync("/api/users/password", content);
            if (response.IsSuccessStatusCode)
            {
                ChangePasswordView.IsVisible = false;
                TxtStatus.Text = "Contraseña actualizada correctamente.";
            }
            else
            {
                var json = await response.Content.ReadAsStringAsync();
                try
                {
                    var doc = JsonDocument.Parse(json);
                    if (doc.RootElement.TryGetProperty("error", out var err))
                    {
                        TxtChangePasswordError.Text = err.GetString();
                    }
                    else
                    {
                        TxtChangePasswordError.Text = "No se pudo actualizar la contraseña.";
                    }
                }
                catch
                {
                    TxtChangePasswordError.Text = "Error al actualizar contraseña.";
                }
                TxtChangePasswordError.IsVisible = true;
            }
        }
        catch (Exception ex)
        {
            TxtChangePasswordError.Text = $"Error: {ex.Message}";
            TxtChangePasswordError.IsVisible = true;
        }
    }

    #endregion
}

// Conversores UI
public class BoolToIconConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is bool isFolder) return isFolder ? "📁" : "📄";
        return "❓";
    }
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotImplementedException();
}

public class BytesToSizeConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is long bytes)
        {
            string[] suf = { "B", "KiB", "MiB", "GiB", "TiB" };
            if (bytes == 0) return "0 B";
            double size = bytes;
            int place = 0;
            while (Math.Abs(size) >= 1024 && place < suf.Length - 1)
            {
                size /= 1024;
                place++;
            }
            return $"{size.ToString("0.#", culture)} {suf[place]}";
        }
        return value;
    }
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotImplementedException();
}