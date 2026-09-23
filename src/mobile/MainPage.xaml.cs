using System.Linq;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using NubeZero.Shared;

namespace NubeZero.Mobile;

public partial class MainPage : ContentPage
{
    private HttpClient _httpClient;
    private string _token = string.Empty;
    private string _serverIp = string.Empty;
    private string _username = string.Empty;
    private string _role = "Estandar";
    private List<DiscoveryResponse> _discoveredServers = new List<DiscoveryResponse>();

    public MainPage()
    {
        InitializeComponent();
        
        Resources.Add("BoolToIconConverter", new BoolToIconConverter());
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
        var savedIp = await SecureStorage.Default.GetAsync("server_ip");
        var savedToken = await SecureStorage.Default.GetAsync("auth_token");
        var savedUser = await SecureStorage.Default.GetAsync("auth_user");
        var savedRole = await SecureStorage.Default.GetAsync("auth_role");

        if (!string.IsNullOrEmpty(savedIp) && !string.IsNullOrEmpty(savedToken))
        {
            _serverIp = savedIp;
            _token = savedToken;
            _username = savedUser ?? "Usuario";
            _role = savedRole ?? "Estandar";
            TxtServerIp.Text = savedIp;
            
            InitHttpClient();
            await TryConnectAsync();
        }
    }

    private void InitHttpClient()
    {
        if (_httpClient != null) _httpClient.Dispose();
        
        string cleanIp = _serverIp.Replace("http://", "").Replace("https://", "").Replace(":8080", "").TrimEnd('/');
        string baseAddress = $"http://{cleanIp}:8080";
        _httpClient = new HttpClient { BaseAddress = new Uri(baseAddress) };
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
            InitHttpClient();
            var loginData = new { Username = user, Password = pass };
            var content = new StringContent(JsonSerializer.Serialize(loginData), Encoding.UTF8, "application/json");
            
            var response = await _httpClient.PostAsync("/api/login", content);
            if (response.IsSuccessStatusCode)
            {
                var json = await response.Content.ReadAsStringAsync();
                var result = JsonSerializer.Deserialize<JsonElement>(json);
                _token = result.GetProperty("token").GetString() ?? "";
                _username = result.TryGetProperty("username", out var uProp) ? uProp.GetString() ?? user : user;
                _role = result.TryGetProperty("role", out var rProp) ? rProp.GetString() ?? "Estandar" : "Estandar";
                
                await SecureStorage.Default.SetAsync("server_ip", _serverIp);
                await SecureStorage.Default.SetAsync("auth_token", _token);
                await SecureStorage.Default.SetAsync("auth_user", _username);
                await SecureStorage.Default.SetAsync("auth_role", _role);
                
                _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _token);
                await TryConnectAsync();
            }
            else
            {
                ShowLoginError("Credenciales o IP incorrectos.");
            }
        }
        catch (Exception ex)
        {
            ShowLoginError($"Error de red: {ex.Message}");
        }
    }

    private async Task TryConnectAsync()
    {
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
            var response = await _httpClient.GetAsync("/api/files?path=");
            
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
        
        _token = string.Empty;
        _username = string.Empty;
        _role = "Estandar";
        
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
            TxtStatus.Text = $"Subiendo {fileName}...";
            
            using var fs = new FileStream(localPath, FileMode.Open, FileAccess.Read);
            var content = new StreamContent(fs);
            
            string relativePath = $"/{Uri.EscapeDataString(fileName)}";
            var response = await _httpClient.PostAsync($"/api/upload?path={relativePath}", content);
            response.EnsureSuccessStatusCode();
            
            TxtStatus.Text = "Subida exitosa";
            await LoadFilesAsync();
        }
        catch (Exception ex)
        {
            TxtStatus.Text = $"Error al subir: {ex.Message}";
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
                try
                {
                    TxtStatus.Text = $"Descargando {dto.Nombre}...";
                    string relativePath = $"/{Uri.EscapeDataString(dto.Nombre)}";
                    var response = await _httpClient.GetAsync($"/api/download?path={relativePath}", HttpCompletionOption.ResponseHeadersRead);
                    response.EnsureSuccessStatusCode();

                    string safeFileName = Path.GetFileName(dto.Nombre);
                    if (string.IsNullOrWhiteSpace(safeFileName)) safeFileName = "archivo_descargado";
                    string localFile = Path.Combine(FileSystem.CacheDirectory, safeFileName);

                    using (var stream = await response.Content.ReadAsStreamAsync())
                    using (var fs = File.Create(localFile))
                    {
                        await stream.CopyToAsync(fs);
                    }

                    await Share.RequestAsync(new ShareFileRequest
                    {
                        Title = $"Guardar {safeFileName}",
                        File = new ShareFile(localFile)
                    });
                    
                    TxtStatus.Text = "Descarga lista";
                }
                catch (Exception ex)
                {
                    await DisplayAlert("Error", $"No se pudo descargar: {ex.Message}", "OK");
                    TxtStatus.Text = "Error al descargar";
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
                        string relativePath = $"/{Uri.EscapeDataString(dto.Nombre)}";
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

public class BytesToSizeConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
    {
        if (value is long bytes)
        {
            string[] suf = { "B", "KB", "MB", "GB", "TB" };
            if (bytes == 0) return "0 B";
            long bytesCopy = Math.Abs(bytes);
            int place = System.Convert.ToInt32(Math.Floor(Math.Log(bytesCopy, 1024)));
            double num = Math.Round(bytesCopy / Math.Pow(1024, place), 1);
            return (Math.Sign(bytes) * num).ToString() + " " + suf[place];
        }
        return value;
    }
    public object? ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture) => throw new NotImplementedException();
}
