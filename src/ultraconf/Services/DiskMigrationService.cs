using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace NubeZero.UltraConf.Services
{
    public class MigrationProgress
    {
        public long TotalBytes { get; set; }
        public long BytesCopied { get; set; }
        public int TotalFiles { get; set; }
        public int FilesCopied { get; set; }
        public string CurrentFile { get; set; } = string.Empty;
        public double Percentage => TotalBytes > 0 ? (double)BytesCopied / TotalBytes * 100.0 : 0.0;
        public double SpeedMBs { get; set; }
    }

    public class DiskMigrationService
    {
        public async Task<long> CalculateTotalBytesAsync(string sourcePath, CancellationToken ct)
        {
            return await Task.Run(() =>
            {
                long total = 0;
                var dir = new DirectoryInfo(sourcePath);
                if (!dir.Exists) return 0;

                foreach (var file in dir.EnumerateFiles("*", SearchOption.AllDirectories))
                {
                    if (ct.IsCancellationRequested) break;
                    total += file.Length;
                }
                return total;
            }, ct);
        }

        public async Task CopyStorageAsync(
            string sourcePath,
            string targetPath,
            IProgress<MigrationProgress> progress,
            CancellationToken ct)
        {
            await Task.Run(async () =>
            {
                var sourceDir = new DirectoryInfo(sourcePath);
                var targetDir = new DirectoryInfo(targetPath);

                if (!sourceDir.Exists)
                    throw new DirectoryNotFoundException($"El directorio origen no existe: {sourcePath}");

                if (!targetDir.Exists)
                    targetDir.Create();

                var allFiles = sourceDir.GetFiles("*", SearchOption.AllDirectories);
                long totalBytes = 0;
                foreach (var f in allFiles) totalBytes += f.Length;

                long bytesCopied = 0;
                int filesCopied = 0;
                var buffer = new byte[1024 * 1024]; // 1 MB buffer para copia rápida en PC
                var sw = System.Diagnostics.Stopwatch.StartNew();

                foreach (var file in allFiles)
                {
                    if (ct.IsCancellationRequested) break;

                    string relative = Path.GetRelativePath(sourceDir.FullName, file.FullName);
                    string destFilePath = Path.Combine(targetDir.FullName, relative);
                    string? destFileDir = Path.GetDirectoryName(destFilePath);

                    if (!string.IsNullOrEmpty(destFileDir) && !Directory.Exists(destFileDir))
                    {
                        Directory.CreateDirectory(destFileDir);
                    }

                    using (var sourceStream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, true))
                    using (var targetStream = new FileStream(destFilePath, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 1024, true))
                    {
                        int read;
                        while ((read = await sourceStream.ReadAsync(buffer, 0, buffer.Length, ct)) > 0)
                        {
                            await targetStream.WriteAsync(buffer, 0, read, ct);
                            bytesCopied += read;

                            double elapsedSec = sw.Elapsed.TotalSeconds;
                            double speed = elapsedSec > 0 ? (bytesCopied / 1024.0 / 1024.0) / elapsedSec : 0;

                            progress?.Report(new MigrationProgress
                            {
                                TotalBytes = totalBytes,
                                BytesCopied = bytesCopied,
                                TotalFiles = allFiles.Length,
                                FilesCopied = filesCopied,
                                CurrentFile = relative,
                                SpeedMBs = Math.Round(speed, 2)
                            });
                        }
                    }

                    // Preservar timestamps
                    File.SetCreationTimeUtc(destFilePath, file.CreationTimeUtc);
                    File.SetLastWriteTimeUtc(destFilePath, file.LastWriteTimeUtc);
                    filesCopied++;
                }

                // Escribir archivo de sello de migración
                string metaPath = Path.Combine(targetDir.FullName, "nubezero.meta");
                string metaContent = $"NUBE_ZERO_MIGRATION\nVersion={NubeZero.Shared.AppVersion.Texto}\nDate={DateTime.UtcNow:O}\nStatus=SUCCESS\nFiles={filesCopied}\nBytes={bytesCopied}";
                await File.WriteAllTextAsync(metaPath, metaContent, ct);

            }, ct);
        }
    }
}
