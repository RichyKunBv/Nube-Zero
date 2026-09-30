using System.Linq;
using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using NubeZero.Shared;

namespace NubeZero.Mobile;

public partial class MainPage : ContentPage
{
    private HttpClient _httpClient;
    private string _token = string.Empty;
    private string _serverIp = string.Empty;
    private string _username = string.Empty;
    private string _role = "Estandar";
    private string _currentPath = string.Empty;
    private string _transferMessage = string.Empty;
    private long _lastProgressUpdate;
    private readonly ConcurrentDictionary<string, byte> _thumbnailRequests = new ConcurrentDictionary<string, byte>();
    private readonly SemaphoreSlim _thumbnailSlots = new SemaphoreSlim(2, 2);
    private List<DiscoveryResponse> _discoveredServers = new List<DiscoveryResponse>();

    public MainPage()
    {
        InitializeComponent();
        
        Resources.Add("BoolToIconConverter", new BoolToIconConverter());
        Resources.Add("FileIconConverter", new FileIconConverter());
        Resources.Add("BytesToSizeConverter", new BytesToSizeConverter());

        _ = CheckExistingSessionAsync();
    }

    private async void BtnDiscoverMobile_Clicked(object sender, EventArgs e)
    {
        BtnDiscoverMobile.IsEnabled = false;
        BtnDiscoverMobile.Text = "⏳";
        TxtDiscoveryStatus.Text = "Buscando servidores en la red local...";
        TxtDiscoveryStatus.IsVisible = true;
        PkrDiscoveredServers.IsVisible = false;

        try
        {
            var servers = await NetworkDiscoveryClient.DiscoverServersAsync(timeoutMs: 1500);
            _discoveredServers = servers;

            if (servers.Count == 0)
            {
                TxtDiscoveryStatus.Text = "No se detectaron servidores. Ingresa la IP manualmente.";
            }
            else if (servers.Count == 1)
            {
                var s = servers[0];
                TxtServerIp.Text = s.IpAddress;
                TxtDiscoveryStatus.Text = $"✓ Servidor encontrado: {s.ServerName} ({s.IpAddress})";
            }
            else
            {
                TxtDiscoveryStatus.Text = $"Se encontraron {servers.Count} servidores:";
                PkrDiscoveredServers.ItemsSource = servers.Select(s => $"☁️ {s.ServerName} ({s.IpAddress}:{s.Port})").ToList();
                PkrDiscoveredServers.IsVisible = true;
                PkrDiscoveredServers.SelectedIndex = 0;
            }
        }
        catch (Exception ex)
        {
            TxtDiscoveryStatus.Text = $"Error al buscar: {ex.Message}";
        }
        finally
        {
            BtnDiscoverMobile.IsEnabled = true;
            BtnDiscoverMobile.Text = "🔍";
        }
    }

    private void PkrDiscoveredServers_SelectedIndexChanged(object sender, EventArgs e)
    {
        if (PkrDiscoveredServers.SelectedIndex >= 0 && PkrDiscoveredServers.SelectedIndex < _discoveredServers.Count)
        {
            TxtServerIp.Text = _discoveredServers[PkrDiscoveredServers.SelectedIndex].IpAddress;
        }
    }

    private async Task CheckExistingSessionAsync()
    {
        try
        {
            var savedIp = await SecureStorage.Default.GetAsync("server_ip");
            var savedToken = await SecureStorage.Default.GetAsync("auth_token");
            var savedUser = await SecureStorage.Default.GetAsync("auth_user");
            var savedRole = await SecureStorage.Default.GetAsync("auth_role");
            var savedPassword = await SecureStorage.Default.GetAsync("auth_password");

            if (!string.IsNullOrEmpty(savedIp) && !string.IsNullOrEmpty(savedUser) && !string.IsNullOrEmpty(savedPassword))
            {
                _serverIp = savedIp;
                TxtServerIp.Text = savedIp;
                TxtUser.Text = savedUser;
                TxtPassword.Text = savedPassword;
                ChkRememberPassword.IsChecked = true;
                await AuthenticateAsync(savedUser, savedPassword);
                return;
            }

            if (string.IsNullOrEmpty(savedIp) || string.IsNullOrEmpty(savedToken)) return;

            _serverIp = savedIp;
            _token = savedToken;
            _username = savedUser ?? "Usuario";
            _role = savedRole ?? "Estandar";
            TxtServerIp.Text = savedIp;
            
            InitHttpClient();
            await TryConnectAsync();
        }
        catch (Exception ex)
        {
            try
            {
                SecureStorage.Default.RemoveAll();
            }
            catch
            {
            }

            ShowLoginError($"No se pudieron recuperar las credenciales guardadas: {ex.Message}");
        }
    }

    private void InitHttpClient()
    {
        if (_httpClient != null) _httpClient.Dispose();
        
        string cleanIp = _serverIp.Replace("http://", "").Replace("https://", "").Replace(":8080", "").TrimEnd('/');
        string baseAddress = $"http://{cleanIp}:8080";
        _httpClient = new HttpClient
        {
            BaseAddress = new Uri(baseAddress),
            Timeout = TimeSpan.FromHours(2)
        };
        if (!string.IsNullOrEmpty(_token))
        {
            _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _token);
        }
    }

    private async void BtnLogin_Clicked(object sender, EventArgs e)
    {
        _serverIp = TxtServerIp.Text?.Trim() ?? "";
        string user = TxtUser.Text?.Trim() ?? "";
        string pass = TxtPassword.Text?.Trim() ?? "";

        if (string.IsNullOrEmpty(_serverIp) || string.IsNullOrEmpty(user) || string.IsNullOrEmpty(pass))
        {
            ShowLoginError("Llena todos los campos.");
            return;
        }

        try
        {
            await AuthenticateAsync(user, pass);
        }
        catch (Exception ex)
        {
            ShowLoginError($"Error de red: {ex.Message}");
        }
    }

    private async void BtnCheckUpdates_Clicked(object sender, EventArgs e)
    {
        if (sender is Button button) button.IsEnabled = false;
        SetUpdateStatus("Buscando actualización...");

        try
        {
            string assetName = UpdateService.GetCurrentAssetName(isAndroid: true);
            var result = await UpdateService.CheckForUpdatesAsync(AppVersion.Texto, assetName);

            if (result.status == UpdateStatus.UpToDate)
            {
                SetUpdateStatus($"Ya tienes la versión {AppVersion.Texto}.");
                return;
            }

            if (result.status == UpdateStatus.Newer)
            {
                SetUpdateStatus("Esta versión es más nueva que la última publicada.");
                return;
            }

            if (result.status != UpdateStatus.Outdated || result.downloadUrl == null)
            {
                SetUpdateStatus(result.status == UpdateStatus.Outdated
                    ? $"v{result.latestVersion} está disponible, pero no se encontró el APK."
                    : "No se pudo consultar GitHub. Revisa tu conexión e inténtalo de nuevo.");
                return;
            }

            bool install = await DisplayAlert(
                "Actualización disponible",
                $"Se descargará Nube-Zero v{result.latestVersion} y se abrirá el instalador de Android.",
                "Continuar",
                "Cancelar");
            if (!install) return;

            SetUpdateStatus($"Descargando v{result.latestVersion}...");
            string apkPath = Path.Combine(FileSystem.CacheDirectory, "NubeZero-update.apk");
            await UpdateService.DownloadFileAsync(result.downloadUrl, apkPath);

            SetUpdateStatus("Abriendo el instalador de Android...");
            await Launcher.Default.OpenAsync(new OpenFileRequest(
                $"Instalar Nube-Zero v{result.latestVersion}",
                new ReadOnlyFile(apkPath)));
            SetUpdateStatus("Confirma la actualización en Android para terminar.");
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

    private async Task AuthenticateAsync(string user, string password)
    {
        InitHttpClient();
        var loginData = new { Username = user, Password = password };
        var content = new StringContent(JsonSerializer.Serialize(loginData), Encoding.UTF8, "application/json");
        var response = await _httpClient.PostAsync("/api/login", content);

        if (!response.IsSuccessStatusCode)
        {
            ShowLoginError("Credenciales o IP incorrectos.");
            return;
        }

        var json = await response.Content.ReadAsStringAsync();
        var result = JsonSerializer.Deserialize<JsonElement>(json);
        _token = result.GetProperty("token").GetString() ?? "";
        _username = result.TryGetProperty("username", out var userProperty) ? userProperty.GetString() ?? user : user;
        _role = result.TryGetProperty("role", out var roleProperty) ? roleProperty.GetString() ?? "Estandar" : "Estandar";

        _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _token);

        try
        {
            await SecureStorage.Default.SetAsync("server_ip", _serverIp);
            await SecureStorage.Default.SetAsync("auth_token", _token);
            await SecureStorage.Default.SetAsync("auth_user", _username);
            await SecureStorage.Default.SetAsync("auth_role", _role);

            if (ChkRememberPassword.IsChecked)
                await SecureStorage.Default.SetAsync("auth_password", password);
            else
                SecureStorage.Default.Remove("auth_password");
        }
        catch (Exception ex)
        {
            ShowLoginError($"Sesión iniciada, pero no se pudo guardar la credencial: {ex.Message}");
        }

        await TryConnectAsync();
    }

    private async Task TryConnectAsync()
    {
        _currentPath = string.Empty;
        TxtUserInfo.Text = $"👤 {_username} [{_role}]";
        BtnUsersAdmin.IsVisible = (_role == "Admin");
        BtnUploadFab.IsVisible = (_role != "Visitante");

        LoginView.IsVisible = false;
        MainView.IsVisible = true;
        await LoadFilesAsync();
    }

    private void ShowLoginError(string msg)
    {
        TxtLoginError.Text = msg;
        TxtLoginError.IsVisible = true;
    }

    private void BtnRefresh_Clicked(object sender, EventArgs e)
    {
        _ = LoadFilesAsync();
    }

    private void RefreshView_Refreshing(object sender, EventArgs e)
    {
        _ = LoadFilesAsync();
    }

    private async Task LoadFilesAsync()
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
                SecureStorage.Default.Remove("auth_token");
                RefreshView.IsRefreshing = false;
                return;
            }
            
            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync();
            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            var files = JsonSerializer.Deserialize<List<ArchivoDTO>>(json, options);
            
            LstFiles.ItemsSource = files;
            TxtCurrentPath.Text = string.IsNullOrEmpty(_currentPath) ? "/" : "/" + _currentPath;
            BtnParentFolder.IsVisible = !string.IsNullOrEmpty(_currentPath);
            TxtStatus.Text = $"{files?.Count ?? 0} elementos";
        }
        catch (Exception ex)
        {
            TxtStatus.Text = $"Error: {ex.Message}";
        }
        finally
        {
            RefreshView.IsRefreshing = false;
        }
    }

    private async void LstFiles_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.CurrentSelection.FirstOrDefault() is ArchivoDTO { EsCarpeta: true } folder)
        {
            LstFiles.SelectedItem = null;
            _currentPath = string.IsNullOrEmpty(_currentPath) ? folder.Nombre : $"{_currentPath}/{folder.Nombre}";
            await LoadFilesAsync();
        }
    }

    private async void BtnParentFolder_Clicked(object sender, EventArgs e)
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

    private void FileItem_Loaded(object sender, EventArgs e)
    {
        if (sender is Element element && element.BindingContext is ArchivoDTO file)
        {
            _ = LoadThumbnailAsync(file);
        }
    }

    private async Task LoadThumbnailAsync(ArchivoDTO file)
    {
        if (file.ThumbnailSource != null) return;
        string extension = Path.GetExtension(file.Nombre).ToLowerInvariant();
        bool isImage = new[] { ".jpg", ".jpeg", ".png", ".gif", ".webp", ".bmp" }.Contains(extension);
        bool isVideo = new[] { ".mp4", ".mov", ".mkv", ".avi", ".webm", ".m4v", ".3gp", ".wmv", ".mpeg", ".mpg" }.Contains(extension);
        long maxSourceBytes = isVideo ? 32 * 1024 * 1024 : 20 * 1024 * 1024;
        if (file.EsCarpeta || file.PesoBytes > maxSourceBytes || (!isImage && !isVideo)) return;

        string cacheDirectory = Path.Combine(FileSystem.CacheDirectory, "thumbnails");
        Directory.CreateDirectory(cacheDirectory);
        string cacheKey = $"{_serverIp}|{_currentPath}|{file.Nombre}|{file.PesoBytes}|{file.FechaModificacion.Ticks}";
        string cacheName = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(cacheKey))) + ".png";
        string thumbnailPath = Path.Combine(cacheDirectory, cacheName);
        if (File.Exists(thumbnailPath))
        {
            MainThread.BeginInvokeOnMainThread(() => file.ThumbnailSource = ImageSource.FromFile(thumbnailPath));
            return;
        }
        if (!_thumbnailRequests.TryAdd(thumbnailPath, 0)) return;

        string sourcePath = thumbnailPath + ".source";
        bool hasThumbnailSlot = false;
        try
        {
            await _thumbnailSlots.WaitAsync().ConfigureAwait(false);
            hasThumbnailSlot = true;
            string remotePath = Uri.EscapeDataString(GetRemotePath(file.Nombre));
            using var response = await _httpClient.GetAsync($"/api/download?path={remotePath}", HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength > maxSourceBytes) return;

            using var input = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
            using (var output = new FileStream(sourcePath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true))
            {
                var buffer = new byte[81920];
                long downloaded = 0;
                int count;
                while ((count = await input.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false)) > 0)
                {
                    downloaded += count;
                    if (downloaded > maxSourceBytes) return;
                    await output.WriteAsync(buffer, 0, count).ConfigureAwait(false);
                }
            }

            bool created = await Task.Run(() =>
            {
                if (isVideo)
                {
                    using var retriever = new Android.Media.MediaMetadataRetriever();
                    retriever.SetDataSource(sourcePath);
                    using var frame = retriever.GetFrameAtTime(0, Android.Media.Option.ClosestSync);
                    if (frame == null) return false;

                    double scale = Math.Min(1d, Math.Min(112d / frame.Width, 112d / frame.Height));
                    int width = Math.Max(1, (int)(frame.Width * scale));
                    int height = Math.Max(1, (int)(frame.Height * scale));
                    using var thumbnail = Android.Graphics.Bitmap.CreateScaledBitmap(frame, width, height, true);
                    using var videoOutput = new FileStream(thumbnailPath, FileMode.Create, FileAccess.Write, FileShare.None);
                    thumbnail.Compress(Android.Graphics.Bitmap.CompressFormat.Png!, 100, videoOutput);
                    return true;
                }

                var bounds = new Android.Graphics.BitmapFactory.Options { InJustDecodeBounds = true };
                Android.Graphics.BitmapFactory.DecodeFile(sourcePath, bounds);
                int sampleSize = 1;
                while (bounds.OutWidth / sampleSize > 112 || bounds.OutHeight / sampleSize > 112)
                {
                    sampleSize *= 2;
                }

                var options = new Android.Graphics.BitmapFactory.Options { InSampleSize = sampleSize };
                using var bitmap = Android.Graphics.BitmapFactory.DecodeFile(sourcePath, options);
                if (bitmap == null) return false;

                using var imageOutput = new FileStream(thumbnailPath, FileMode.Create, FileAccess.Write, FileShare.None);
                bitmap.Compress(Android.Graphics.Bitmap.CompressFormat.Png!, 100, imageOutput);
                return true;
            }).ConfigureAwait(false);
            if (!created) return;

            TrimThumbnailCache(cacheDirectory);
            MainThread.BeginInvokeOnMainThread(() => file.ThumbnailSource = ImageSource.FromFile(thumbnailPath));
        }
        catch
        {
        }
        finally
        {
            _thumbnailRequests.TryRemove(thumbnailPath, out _);
            if (File.Exists(sourcePath)) File.Delete(sourcePath);
            if (hasThumbnailSlot) _thumbnailSlots.Release();
        }
    }

    private static void TrimThumbnailCache(string cacheDirectory)
    {
        foreach (var file in new DirectoryInfo(cacheDirectory).GetFiles("*.png").OrderByDescending(file => file.LastWriteTimeUtc).Skip(80))
        {
            try { file.Delete(); } catch { }
        }
    }

    private void TxtServerIp_Completed(object sender, EventArgs e)
    {
        TxtUser.Focus();
    }

    private void TxtUser_Completed(object sender, EventArgs e)
    {
        TxtPassword.Focus();
    }

    private void BtnLogout_Clicked(object sender, EventArgs e)
    {
        SecureStorage.Default.Remove("auth_token");
        SecureStorage.Default.Remove("server_ip");
        SecureStorage.Default.Remove("auth_user");
        SecureStorage.Default.Remove("auth_role");
        SecureStorage.Default.Remove("auth_password");
        
        _token = string.Empty;
        _username = string.Empty;
        _role = "Estandar";
        _currentPath = string.Empty;
        
        if (_httpClient != null) 
        {
            _httpClient.Dispose();
            _httpClient = null;
        }

        TxtUser.Text = string.Empty;
        TxtPassword.Text = string.Empty;
        TxtLoginError.IsVisible = false;
        
        MainView.IsVisible = false;
        LoginView.IsVisible = true;
    }

    #region Gestión de Usuarios (Admin)

    private async void BtnUsersAdmin_Clicked(object sender, EventArgs e)
    {
        string choice = await DisplayActionSheet("Gestión de Usuarios", "Cancelar", null, "➕ Crear nuevo usuario", "📋 Ver y eliminar usuarios");
        
        if (choice == "➕ Crear nuevo usuario")
        {
            await CreateUserFlowAsync();
        }
        else if (choice == "📋 Ver y eliminar usuarios")
        {
            await ManageUsersListFlowAsync();
        }
    }

    private async Task CreateUserFlowAsync()
    {
        string newUser = await DisplayPromptAsync("Nuevo Usuario", "Ingresa el nombre del usuario:");
        if (string.IsNullOrWhiteSpace(newUser)) return;
        
        string newPass = await DisplayPromptAsync("Contraseña", $"Ingresa la contraseña para {newUser}:");
        if (string.IsNullOrWhiteSpace(newPass)) return;

        string roleSelection = await DisplayActionSheet("Selecciona el Nivel de Acceso", "Cancelar", null, "Estándar", "Visitante", "Admin");
        if (string.IsNullOrEmpty(roleSelection) || roleSelection == "Cancelar") return;

        string role = "Estandar";
        if (roleSelection == "Visitante") role = "Visitante";
        else if (roleSelection == "Admin") role = "Admin";

        try
        {
            var registerData = new { Username = newUser.Trim(), Password = newPass.Trim(), Role = role };
            var content = new StringContent(JsonSerializer.Serialize(registerData), Encoding.UTF8, "application/json");
            
            var response = await _httpClient.PostAsync("/api/users/add", content);
            if (response.IsSuccessStatusCode)
            {
                await DisplayAlert("Éxito", $"El usuario {newUser} [{role}] ha sido creado correctamente.", "OK");
            }
            else if (response.StatusCode == System.Net.HttpStatusCode.Conflict)
            {
                await DisplayAlert("Error", "Este usuario ya existe.", "OK");
            }
            else
            {
                await DisplayAlert("Error", "No se pudo crear el usuario.", "OK");
            }
        }
        catch (Exception ex)
        {
            await DisplayAlert("Error", $"Error de red: {ex.Message}", "OK");
        }
    }

    private async Task ManageUsersListFlowAsync()
    {
        try
        {
            var response = await _httpClient.GetAsync("/api/users");
            if (!response.IsSuccessStatusCode)
            {
                await DisplayAlert("Error", "No se pudo obtener la lista de usuarios.", "OK");
                return;
            }

            var json = await response.Content.ReadAsStringAsync();
            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            var users = JsonSerializer.Deserialize<List<UserDTO>>(json, options);

            if (users == null || users.Count == 0)
            {
                await DisplayAlert("Usuarios", "No hay usuarios registrados.", "OK");
                return;
            }

            var userOptions = users.Select(u => $"{u.Username} [{u.Role}]").ToArray();
            string selected = await DisplayActionSheet("Selecciona un usuario para gestionar", "Cerrar", null, userOptions);

            if (string.IsNullOrEmpty(selected) || selected == "Cerrar") return;

            string selectedUsername = selected.Split(' ')[0];
            if (selectedUsername.Equals(_username, StringComparison.OrdinalIgnoreCase))
            {
                await DisplayAlert("Aviso", "No puedes eliminar tu propia cuenta en uso.", "OK");
                return;
            }

            bool confirm = await DisplayAlert("Eliminar Usuario", $"¿Deseas eliminar permanentemente a '{selectedUsername}'?", "Sí, eliminar", "Cancelar");
            if (confirm)
            {
                var delResponse = await _httpClient.DeleteAsync($"/api/users/delete?username={Uri.EscapeDataString(selectedUsername)}");
                if (delResponse.IsSuccessStatusCode)
                {
                    await DisplayAlert("Éxito", $"Usuario {selectedUsername} eliminado.", "OK");
                }
                else
                {
                    var errJson = await delResponse.Content.ReadAsStringAsync();
                    await DisplayAlert("Error", $"No se pudo eliminar: {errJson}", "OK");
                }
            }
        }
        catch (Exception ex)
        {
            await DisplayAlert("Error", $"Error: {ex.Message}", "OK");
        }
    }

    #endregion

    #region Cambio de Contraseña

    private async void BtnChangePassword_Clicked(object sender, EventArgs e)
    {
        string currentPass = await DisplayPromptAsync("Cambiar Contraseña", "Ingresa tu contraseña actual:");
        if (string.IsNullOrWhiteSpace(currentPass)) return;

        string newPass = await DisplayPromptAsync("Cambiar Contraseña", "Ingresa la nueva contraseña:");
        if (string.IsNullOrWhiteSpace(newPass)) return;

        string confirmPass = await DisplayPromptAsync("Cambiar Contraseña", "Confirma la nueva contraseña:");
        if (newPass != confirmPass)
        {
            await DisplayAlert("Error", "Las contraseñas no coinciden.", "OK");
            return;
        }

        try
        {
            var reqData = new { CurrentPassword = currentPass, NewPassword = newPass };
            var content = new StringContent(JsonSerializer.Serialize(reqData), Encoding.UTF8, "application/json");

            var response = await _httpClient.PostAsync("/api/users/password", content);
            if (response.IsSuccessStatusCode)
            {
                await DisplayAlert("Éxito", "Contraseña actualizada correctamente.", "OK");
            }
            else
            {
                var json = await response.Content.ReadAsStringAsync();
                await DisplayAlert("Error", "No se pudo actualizar la contraseña. Verifica que la contraseña actual sea correcta.", "OK");
            }
        }
        catch (Exception ex)
        {
            await DisplayAlert("Error", $"Error: {ex.Message}", "OK");
        }
    }

    #endregion

    private async void BtnUpload_Clicked(object sender, EventArgs e)
    {
        if (_role == "Visitante")
        {
            await DisplayAlert("Permiso Denegado", "Los usuarios visitantes solo tienen permisos de descarga.", "OK");
            return;
        }

        try
        {
            var result = await FilePicker.Default.PickAsync(new PickOptions { PickerTitle = "Seleccionar archivo" });
            if (result != null)
            {
                await UploadFileAsync(result.FullPath, result.FileName);
            }
        }
        catch (Exception ex)
        {
            await DisplayAlert("Error", ex.Message, "OK");
        }
    }

    public async Task UploadFileAsync(string localPath, string fileName)
    {
        try
        {
            ShowTransfer($"Subiendo {fileName}...");
            
            using var fs = new FileStream(localPath, FileMode.Open, FileAccess.Read);
            using var content = new ProgressStreamContent(fs, UpdateTransferProgress);
            
            string relativePath = Uri.EscapeDataString(GetRemotePath(fileName));
            using var response = await _httpClient.PostAsync($"/api/upload?path={relativePath}", content);
            response.EnsureSuccessStatusCode();
            
            TxtStatus.Text = "Subida exitosa";
            await LoadFilesAsync();
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

    private async void BtnItemMenu_Clicked(object sender, EventArgs e)
    {
        if (sender is Button btn && btn.CommandParameter is ArchivoDTO dto)
        {
            string action;
            if (_role == "Visitante")
            {
                action = await DisplayActionSheet($"Opciones: {dto.Nombre}", "Cancelar", null, "Descargar", "Detalles");
            }
            else
            {
                action = await DisplayActionSheet($"Opciones: {dto.Nombre}", "Cancelar", "Eliminar", "Descargar", "Detalles");
            }

            if (action == "Descargar")
            {
                Android.Net.Uri? destination = null;
                MainActivity? activity = null;
                try
                {
                    string safeFileName = Path.GetFileName(dto.Nombre);
                    if (string.IsNullOrWhiteSpace(safeFileName)) safeFileName = "archivo_descargado";
                    activity = Microsoft.Maui.ApplicationModel.Platform.CurrentActivity as MainActivity
                        ?? throw new InvalidOperationException("No se pudo abrir el selector de destino Android.");

                    TxtStatus.Text = "Selecciona dónde guardar el archivo...";
                    destination = await activity.PickSaveDestinationAsync(safeFileName);
                    if (destination == null)
                    {
                        TxtStatus.Text = "Guardado cancelado";
                        return;
                    }

                    ShowTransfer($"Descargando {safeFileName}...");
                    string relativePath = Uri.EscapeDataString(GetRemotePath(dto.Nombre));
                    using var response = await _httpClient.GetAsync($"/api/download?path={relativePath}", HttpCompletionOption.ResponseHeadersRead);
                    response.EnsureSuccessStatusCode();

                    using var stream = await response.Content.ReadAsStreamAsync();
                    long total = response.Content.Headers.ContentLength ?? -1;
                    await activity.CopyToSaveDestinationAsync(destination, stream, total, UpdateTransferProgress);
                    TxtStatus.Text = "Archivo guardado correctamente";
                }
                catch (Exception ex)
                {
                    if (destination != null && activity != null)
                    {
                        try { Android.Provider.DocumentsContract.DeleteDocument(activity.ContentResolver!, destination); } catch { }
                    }
                    await DisplayAlert("Error", $"No se pudo descargar: {ex.Message}", "OK");
                    TxtStatus.Text = "Error al descargar";
                }
                finally
                {
                    HideTransfer();
                }
            }
            else if (action == "Eliminar")
            {
                if (_role == "Visitante")
                {
                    await DisplayAlert("Permiso Denegado", "Los visitantes no pueden eliminar archivos.", "OK");
                    return;
                }

                bool confirm = await DisplayAlert("Confirmar", $"¿Seguro que deseas eliminar '{dto.Nombre}'?", "Sí", "No");
                if (confirm)
                {
                    try
                    {
                        string relativePath = Uri.EscapeDataString(GetRemotePath(dto.Nombre));
                        var response = await _httpClient.DeleteAsync($"/api/delete?path={relativePath}");
                        response.EnsureSuccessStatusCode();
                        await LoadFilesAsync();
                    }
                    catch (Exception ex)
                    {
                        await DisplayAlert("Error", ex.Message, "OK");
                    }
                }
            }
            else if (action == "Detalles")
            {
                await DisplayAlert("Detalles", $"Nombre: {dto.Nombre}\nSubido por: {dto.ModificadoPor}\nPeso: {dto.PesoBytes} bytes\nFecha: {dto.FechaModificacion}", "OK");
            }
        }
    }

    private void ShowTransfer(string message)
    {
        Interlocked.Exchange(ref _lastProgressUpdate, 0);
        _transferMessage = message;
        TxtTransferStatus.Text = message;
        PrgTransfer.Progress = 0;
        TransferPanel.IsVisible = true;
    }

    private void UpdateTransferProgress(long transferred, long total)
    {
        long now = Environment.TickCount64;
        if (total > transferred && now - Interlocked.Read(ref _lastProgressUpdate) < 150) return;
        Interlocked.Exchange(ref _lastProgressUpdate, now);

        MainThread.BeginInvokeOnMainThread(() =>
        {
            if (total > 0)
            {
                PrgTransfer.Progress = Math.Min(1, (double)transferred / total);
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
        MainThread.BeginInvokeOnMainThread(() => TransferPanel.IsVisible = false);
    }
}

// Conversores UI
public class BoolToIconConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
    {
        if (value is bool isFolder) return isFolder ? "📁" : "📄";
        return "❓";
    }
    public object? ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture) => throw new NotImplementedException();
}

public class FileIconConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
    {
        if (value is not ArchivoDTO file) return "📄";
        if (file.EsCarpeta) return "📁";

        string extension = Path.GetExtension(file.Nombre).ToLowerInvariant();
        if (new[] { ".jpg", ".jpeg", ".png", ".gif", ".webp", ".bmp" }.Contains(extension)) return "🖼️";
        if (new[] { ".mp4", ".mov", ".mkv", ".avi", ".webm" }.Contains(extension)) return "🎬";
        if (extension == ".apk") return "📱";
        if (new[] { ".zip", ".rar", ".7z", ".tar", ".gz" }.Contains(extension)) return "🗜️";
        if (new[] { ".pdf", ".doc", ".docx", ".txt", ".odt" }.Contains(extension)) return "📃";
        return "📄";
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture) => throw new NotImplementedException();
}

public class BytesToSizeConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
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
    public object? ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture) => throw new NotImplementedException();
}
