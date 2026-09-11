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

    public MainPage()
    {
        InitializeComponent();
        
        Resources.Add("BoolToIconConverter", new BoolToIconConverter());
        Resources.Add("BytesToSizeConverter", new BytesToSizeConverter());

        _ = CheckExistingSessionAsync();
    }

    private async Task CheckExistingSessionAsync()
    {
        var savedIp = await SecureStorage.Default.GetAsync("server_ip");
        var savedToken = await SecureStorage.Default.GetAsync("auth_token");

        if (!string.IsNullOrEmpty(savedIp) && !string.IsNullOrEmpty(savedToken))
        {
            _serverIp = savedIp;
            _token = savedToken;
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
                
                await SecureStorage.Default.SetAsync("server_ip", _serverIp);
                await SecureStorage.Default.SetAsync("auth_token", _token);
                
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

    private void BtnLogout_Clicked(object sender, EventArgs e)
    {
        SecureStorage.Default.Remove("auth_token");
        SecureStorage.Default.Remove("server_ip");
        _token = string.Empty;
        
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

    private async void BtnAddUser_Clicked(object sender, EventArgs e)
    {
        string newUser = await DisplayPromptAsync("Nuevo Usuario", "Ingresa el nombre del nuevo usuario:");
        if (string.IsNullOrWhiteSpace(newUser)) return;
        
        string newPass = await DisplayPromptAsync("Contraseña", $"Ingresa la contraseña para {newUser}:");
        if (string.IsNullOrWhiteSpace(newPass)) return;

        try
        {
            var registerData = new { Username = newUser.Trim(), Password = newPass.Trim() };
            var content = new StringContent(JsonSerializer.Serialize(registerData), Encoding.UTF8, "application/json");
            
            var response = await _httpClient.PostAsync("/api/users/add", content);
            if (response.IsSuccessStatusCode)
            {
                await DisplayAlert("Éxito", $"El usuario {newUser} ha sido creado correctamente.", "OK");
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

    private async void BtnUpload_Clicked(object sender, EventArgs e)
    {
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
        if (sender is ImageButton btn && btn.CommandParameter is ArchivoDTO dto)
        {
            string action = await DisplayActionSheet($"Opciones: {dto.Nombre}", "Cancelar", "Eliminar", "Descargar", "Detalles");

            if (action == "Descargar")
            {
                await DisplayAlert("Info", "En MAUI Android se requiere manejo especial para guardar archivos públicos. (Por implementar).", "OK");
            }
            else if (action == "Eliminar")
            {
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
