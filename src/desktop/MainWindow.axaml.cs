using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using NubeZero.Shared;

namespace NubeZero.Desktop;

public partial class MainWindow : Window
{
    private HttpClient _httpClient = new HttpClient { BaseAddress = new Uri("http://localhost:8080") };
    private string _token = string.Empty;
    private string _username = string.Empty;
    private string _role = "Estandar";

    public MainWindow()
    {
        InitializeComponent();
        
        // Agregar recursos de conversores dinámicamente
        Resources.Add("BoolToIconConverter", new BoolToIconConverter());
        Resources.Add("BytesToSizeConverter", new BytesToSizeConverter());
        
        // Evento Global de Drag & Drop
        AddHandler(DragDrop.DropEvent, OnDrop);
    }

    private async void BtnLogin_Click(object? sender, RoutedEventArgs e)
    {
        string user = TxtUser.Text?.Trim() ?? "";
        string pass = TxtPassword.Text?.Trim() ?? "";
        string serverUrl = TxtServerUrl.Text?.Trim() ?? "http://localhost:8080";

        if (string.IsNullOrEmpty(user) || string.IsNullOrEmpty(pass))
        {
            ShowLoginError("Por favor, ingresa credenciales.");
            return;
        }

        try
        {
            if (!serverUrl.StartsWith("http://") && !serverUrl.StartsWith("https://"))
                serverUrl = "http://" + serverUrl;
                
            _httpClient = new HttpClient { BaseAddress = new Uri(serverUrl) };
            
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
                
                _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _token);
                
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
                ShowLoginError("Usuario o contraseña incorrectos.");
            }
        }
        catch (Exception ex)
        {
            ShowLoginError($"Error de conexión: {ex.Message}");
        }
    }

    private void ShowLoginError(string msg)
    {
        TxtLoginError.Text = msg;
        TxtLoginError.IsVisible = true;
    }

    private async void BtnRefresh_Click(object? sender, RoutedEventArgs e)
    {
        await LoadFilesAsync();
    }

    private async System.Threading.Tasks.Task LoadFilesAsync()
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
                return;
            }
            
            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync();
            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            var files = JsonSerializer.Deserialize<List<ArchivoDTO>>(json, options);
            
            LstFiles.ItemsSource = files;
            TxtStatus.Text = $"{files?.Count ?? 0} elementos";
            TxtDragHint.IsVisible = (files == null || files.Count == 0);
        }
        catch (Exception ex)
        {
            TxtStatus.Text = $"Error: {ex.Message}";
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
            TxtStatus.Text = $"Subiendo {fileName}...";
            
            using var fs = new FileStream(localPath, FileMode.Open, FileAccess.Read);
            var content = new StreamContent(fs);
            
            string relativePath = $"/{Uri.EscapeDataString(fileName)}";
            var response = await _httpClient.PostAsync($"/api/upload?path={relativePath}", content);
            response.EnsureSuccessStatusCode();
            TxtStatus.Text = "Subida exitosa";
        }
        catch (Exception ex)
        {
            TxtStatus.Text = $"Error al subir: {ex.Message}";
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
                TxtStatus.Text = $"Descargando {dto.Nombre}...";
                
                try
                {
                    string relativePath = $"/{Uri.EscapeDataString(dto.Nombre)}";
                    var response = await _httpClient.GetAsync($"/api/download?path={relativePath}", HttpCompletionOption.ResponseHeadersRead);
                    response.EnsureSuccessStatusCode();
                    
                    using var fs = new FileStream(destPath, FileMode.Create, FileAccess.Write);
                    await response.Content.CopyToAsync(fs);
                    TxtStatus.Text = "Descarga completada";
                }
                catch (Exception ex)
                {
                    TxtStatus.Text = $"Error descarga: {ex.Message}";
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
                string relativePath = $"/{Uri.EscapeDataString(dto.Nombre)}";
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

    private void BtnLogout_Click(object? sender, RoutedEventArgs e)
    {
        _token = string.Empty;
        _username = string.Empty;
        _role = "Estandar";

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
            string[] suf = { "B", "KB", "MB", "GB", "TB" };
            if (bytes == 0) return "0 B";
            long bytesCopy = Math.Abs(bytes);
            int place = System.Convert.ToInt32(Math.Floor(Math.Log(bytesCopy, 1024)));
            double num = Math.Round(bytesCopy / Math.Pow(1024, place), 1);
            return (Math.Sign(bytes) * num).ToString() + " " + suf[place];
        }
        return value;
    }
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotImplementedException();
}