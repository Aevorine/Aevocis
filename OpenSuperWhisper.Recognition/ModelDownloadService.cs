using OpenSuperWhisper.Core;
using Whisper.net.Ggml;

namespace OpenSuperWhisper.Recognition;

/// <summary>Reported while a model is downloading. <see cref="TotalBytesApprox"/> is the catalog's
/// approximate size (the HTTP response doesn't reliably expose Content-Length through
/// Whisper.net's downloader), so <see cref="PercentApprox"/> is an estimate for UI purposes only -
/// never let it exceed 99% until the download actually finishes, since the true size can differ
/// slightly from the catalog constant.</summary>
public readonly record struct ModelDownloadProgress(long BytesDownloaded, long TotalBytesApprox)
{
    public double PercentApprox => TotalBytesApprox <= 0
        ? 0
        : Math.Min(99.0, BytesDownloaded * 100.0 / TotalBytesApprox);
}

/// <summary>
/// F01: downloads a non-bundled recognition model (Medium/LargeV3Turbo) from Hugging Face via
/// Whisper.net's own <see cref="WhisperGgmlDownloader"/> - so the exact download URL/host is
/// whatever that library resolves internally, not hand-guessed - and caches it under
/// %LOCALAPPDATA%\OpenSuperWhisper\Models. Safe to call repeatedly: a model already on disk is
/// never re-downloaded.
/// </summary>
public sealed class ModelDownloadService
{
    /// <summary>
    /// Ensures <paramref name="option"/>'s .bin file exists locally, downloading it first if
    /// needed, and returns its path. Downloads to a ".part" sibling file and only renames it into
    /// place once fully written, so a crash/network-drop/cancel mid-download can never leave a
    /// truncated file at the real path masquerading as a usable model (which WhisperFactory would
    /// then fail - or worse, silently mis-transcribe - on next use).
    /// </summary>
    public async Task<string> EnsureLocalAsync(
        ModelOption option,
        string bundledModelsDir,
        IProgress<ModelDownloadProgress>? progress = null,
        CancellationToken ct = default)
    {
        var finalPath = ModelCatalog.GetLocalPath(option, bundledModelsDir);
        if (File.Exists(finalPath))
            return finalPath;

        if (option.Bundled)
        {
            // Small ships inside the installer next to the exe - if it's missing, that's a
            // packaging bug, not something this service can fix by downloading.
            throw new FileNotFoundException($"内置模型文件缺失：{finalPath}（这是安装包问题，不是需要下载的模型）", finalPath);
        }

        var partPath = finalPath + ".part";
        Log.Info($"开始下载识别模型 {option.Key}（{option.ApproxSizeDisplay}）到 {finalPath}");
        try
        {
            using var modelStream = await WhisperGgmlDownloader.Default.GetGgmlModelAsync(option.GgmlType, cancellationToken: ct);
            await using (var file = File.Create(partPath))
            {
                var buffer = new byte[1 << 20]; // 1 MB chunks
                long total = 0;
                int read;
                while ((read = await modelStream.ReadAsync(buffer, ct)) > 0)
                {
                    await file.WriteAsync(buffer.AsMemory(0, read), ct);
                    total += read;
                    progress?.Report(new ModelDownloadProgress(total, option.ApproxSizeBytes));
                }
            }

            File.Move(partPath, finalPath, overwrite: true);
            Log.Info($"识别模型 {option.Key} 下载完成：{finalPath}");
            return finalPath;
        }
        catch (Exception ex)
        {
            Log.Error($"识别模型 {option.Key} 下载失败", ex);
            try { if (File.Exists(partPath)) File.Delete(partPath); }
            catch { /* best-effort cleanup, the real error is already logged/propagated below */ }
            throw;
        }
    }
}
