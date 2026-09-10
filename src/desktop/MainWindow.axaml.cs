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
    private static readonly HttpClient _httpClient = new HttpClient { BaseAddress = new Uri("http://localhost:8080") };
    private string _token = string.Empty;

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

        if (string.IsNullOrEmpty(user) || string.IsNullOrEmpty(pass))
        {
            ShowLoginError("Por favor, ingresa credenciales.");
            return;
        }

        try
        {
            var loginData = new { Username = user, Password = pass };
            var content = new StringContent(JsonSerializer.Serialize(loginData), Encoding.UTF8, "application/json");
            
            var response = await _httpClient.PostAsync("/api/login", content);
            if (response.IsSuccessStatusCode)
            {
                var json = await response.Content.ReadAsStringAsync();
                var result = JsonSerializer.Deserialize<JsonElement>(json);
                _token = result.GetProperty("token").GetString() ?? "";
                
                _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _token);
                
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