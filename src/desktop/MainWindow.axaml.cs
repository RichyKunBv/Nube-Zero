using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using NubeZero.Shared;

namespace NubeZero.Desktop;

public partial class MainWindow : Window
{
    private static readonly HttpClient _httpClient = new HttpClient { BaseAddress = new Uri("http://localhost:8080") };

    public MainWindow()
    {
        InitializeComponent();
        
        // Evento de Drag & Drop
        AddHandler(DragDrop.DropEvent, OnDrop);
        
        // Cargar al iniciar
        _ = LoadFilesAsync();
    }

    private async void BtnRefresh_Click(object? sender, RoutedEventArgs e)
    {
        await LoadFilesAsync();
    }

    private async System.Threading.Tasks.Task LoadFilesAsync()
    {
        try
        {
            TxtStatus.Text = "Cargando...";
            var response = await _httpClient.GetAsync("/api/files?path=");
            response.EnsureSuccessStatusCode();
            
            var json = await response.Content.ReadAsStringAsync();
            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            var files = JsonSerializer.Deserialize<List<ArchivoDTO>>(json, options);
            
            LstFiles.ItemsSource = files;
            TxtStatus.Text = $"{files?.Count ?? 0} elementos.";
        }
        catch (Exception ex)
        {
            TxtStatus.Text = $"Error: {ex.Message}";
        }
    }

    private async void OnDrop(object? sender, DragEventArgs e)
    {
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

    private async System.Threading.Tasks.Task UploadFileAsync(string localPath)
    {
        try
        {
            string fileName = Path.GetFileName(localPath);
            TxtStatus.Text = $"Subiendo {fileName}...";
            
            using var fs = new FileStream(localPath, FileMode.Open, FileAccess.Read);
            var content = new StreamContent(fs);
            
            // Subida cruda usando Streams (igual que lo configuramos en el server)
            string relativePath = $"/{Uri.EscapeDataString(fileName)}";
            var response = await _httpClient.PostAsync($"/api/upload?path={relativePath}", content);
            
            response.EnsureSuccessStatusCode();
            TxtStatus.Text = "Subida completada.";
        }
        catch (Exception ex)
        {
            TxtStatus.Text = $"Error al subir: {ex.Message}";
        }
    }
}