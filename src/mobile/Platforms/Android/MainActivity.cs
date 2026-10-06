using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace NubeZero.Mobile;

[Activity(Theme = "@style/Maui.SplashTheme", MainLauncher = true, LaunchMode = LaunchMode.SingleTop, ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
[IntentFilter(new[] { Intent.ActionSend }, Categories = new[] { Intent.CategoryDefault }, DataMimeType = "*/*")]
[IntentFilter(new[] { Intent.ActionSendMultiple }, Categories = new[] { Intent.CategoryDefault }, DataMimeType = "*/*")]
public class MainActivity : MauiAppCompatActivity
{
    private const int SaveDocumentRequestCode = 5021;
    private TaskCompletionSource<Android.Net.Uri?>? _saveDestinationCompletion;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        ProcessIntent(Intent);
    }

    protected override void OnNewIntent(Intent? intent)
    {
        base.OnNewIntent(intent);
        ProcessIntent(intent);
    }

    public Task<Android.Net.Uri?> PickSaveDestinationAsync(string fileName)
    {
        if (_saveDestinationCompletion != null)
            throw new InvalidOperationException("Ya hay una selección de destino en curso.");

        var completion = new TaskCompletionSource<Android.Net.Uri?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _saveDestinationCompletion = completion;

        var intent = new Intent(Intent.ActionCreateDocument);
        intent.AddCategory(Intent.CategoryOpenable);
        intent.SetType("*/*");
        intent.PutExtra(Intent.ExtraTitle, fileName);

        try
        {
            StartActivityForResult(intent, SaveDocumentRequestCode);
        }
        catch
        {
            _saveDestinationCompletion = null;
            throw;
        }

        return completion.Task;
    }

    public async Task CopyDecryptedToSaveDestinationAsync(Android.Net.Uri destination, System.IO.Stream encryptedSource, byte[] key, long total, Action<long, long> progress)
    {
        try
        {
            using var output = ContentResolver?.OpenOutputStream(destination, "w")
                ?? throw new InvalidOperationException("Android no pudo abrir el destino seleccionado.");
            await NubeZero.Shared.FileEncryptionService.DecryptAsync(encryptedSource, output, key).ConfigureAwait(false);
            await output.FlushAsync().ConfigureAwait(false);
            progress?.Invoke(total, total);
        }
        catch
        {
            try { Android.Provider.DocumentsContract.DeleteDocument(ContentResolver!, destination); } catch { }
            throw;
        }
    }

    protected override void OnActivityResult(int requestCode, Result resultCode, Intent? data)
    {
        base.OnActivityResult(requestCode, resultCode, data);

        if (requestCode != SaveDocumentRequestCode) return;

        var completion = _saveDestinationCompletion;
        _saveDestinationCompletion = null;
        completion?.TrySetResult(resultCode == Result.Ok ? data?.Data : null);
    }

    private void ProcessIntent(Intent? intent)
    {
        if (intent == null) return;

        if (intent.Action == Intent.ActionSend && intent.HasExtra(Intent.ExtraStream))
        {
            var uri = (Android.Net.Uri)intent.GetParcelableExtra(Intent.ExtraStream)!;
            HandleSharedFileAsync(uri);
        }
        else if (intent.Action == Intent.ActionSendMultiple && intent.HasExtra(Intent.ExtraStream))
        {
            var uris = intent.GetParcelableArrayListExtra(Intent.ExtraStream);
            if (uris != null)
            {
                foreach (Android.Net.Uri uri in uris)
                {
                    HandleSharedFileAsync(uri);
                }
            }
        }
    }

    private async void HandleSharedFileAsync(Android.Net.Uri uri)
    {
        string sharedFilePath = string.Empty;
        bool isTemporaryFile = false;
        try
        {
            // Necesitamos esperar que la UI esté lista
            await Task.Delay(1500);

            (sharedFilePath, isTemporaryFile) = GetPathFromUri(uri);
            var filename = System.IO.Path.GetFileName(GetFileNameFromUri(uri).Replace('\\', '/'));
            if (string.IsNullOrWhiteSpace(filename))
                filename = "shared-file";

            if (!string.IsNullOrEmpty(sharedFilePath))
            {
                var mainPage = App.Current?.MainPage as MainPage;
                if (mainPage != null)
                {
                    await mainPage.UploadFileAsync(sharedFilePath, filename);
                }
            }
        }
        catch (System.Exception ex)
        {
            System.Console.WriteLine($"Error al procesar archivo compartido: {ex.Message}");
        }
        finally
        {
            if (isTemporaryFile && System.IO.File.Exists(sharedFilePath))
            {
                try
                {
                    System.IO.File.Delete(sharedFilePath);
                }
                catch (System.Exception ex)
                {
                    System.Console.Error.WriteLine($"No se pudo eliminar la copia temporal sin cifrar: {ex.Message}");
                }
            }
        }
    }

    private (string Path, bool IsTemporary) GetPathFromUri(Android.Net.Uri uri)
    {
        if (uri.Scheme == "file") return (uri.Path ?? string.Empty, false);

        using var cursor = ContentResolver?.Query(uri, null, null, null, null);
        if (cursor != null && cursor.MoveToFirst())
        {
            int index = cursor.GetColumnIndex(Android.Provider.MediaStore.MediaColumns.Data);
            if (index != -1)
            {
                string? path = cursor.GetString(index);
                if (!string.IsNullOrWhiteSpace(path))
                    return (path, false);
            }
        }

        // Si es un content:// que no expone Data, hay que copiarlo a Cache (Workaround clásico en Android)
        string fileName = System.IO.Path.GetFileName(GetFileNameFromUri(uri));
        if (string.IsNullOrWhiteSpace(fileName))
            fileName = "shared-file";
        string tempFile = System.IO.Path.Combine(CacheDir!.AbsolutePath,
            "nubezero-share-" + System.Guid.NewGuid().ToString("N") + "-" + fileName);
        try
        {
            using var inStream = ContentResolver?.OpenInputStream(uri)
                ?? throw new System.IO.IOException("Android no pudo abrir el archivo compartido.");
            using var outStream = new System.IO.FileStream(tempFile, System.IO.FileMode.CreateNew, System.IO.FileAccess.Write);
            inStream.CopyTo(outStream);
            return (tempFile, true);
        }
        catch
        {
            if (System.IO.File.Exists(tempFile))
                System.IO.File.Delete(tempFile);
            throw;
        }
    }

    private string GetFileNameFromUri(Android.Net.Uri uri)
    {
        string name = "ArchivoCompartido_" + System.Guid.NewGuid().ToString().Substring(0,8);
        using var cursor = ContentResolver?.Query(uri, null, null, null, null);
        if (cursor != null && cursor.MoveToFirst())
        {
            int index = cursor.GetColumnIndex(Android.Provider.OpenableColumns.DisplayName);
            if (index != -1) name = cursor.GetString(index) ?? name;
        }
        return name;
    }
}
