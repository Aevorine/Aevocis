using Whisper.net.Ggml;

namespace OpenSuperWhisper.Recognition;

/// <summary>
/// F01: describes one selectable recognition model. <see cref="Key"/> is the stable identifier
/// persisted in AppSettings.ModelSize - never change an existing Key once shipped, or old users'
/// saved preference will silently fall back to Small.
/// </summary>
public sealed record ModelOption(
    string Key,
    string DisplayName,
    string FileName,
    GgmlType GgmlType,
    bool Bundled,
    long ApproxSizeBytes)
{
    public string ApproxSizeDisplay => ApproxSizeBytes >= 1024L * 1024 * 1024
        ? $"{ApproxSizeBytes / (1024.0 * 1024 * 1024):F1} GB"
        : $"{ApproxSizeBytes / (1024.0 * 1024):F0} MB";
}

/// <summary>
/// The fixed set of models the Settings UI lets the user pick from. Small is bundled with the
/// installer (unchanged from before F01); Medium and LargeV3Turbo are downloaded on first use
/// into %LOCALAPPDATA%\OpenSuperWhisper\Models via <see cref="ModelDownloadService"/>, so the
/// installer stays small. Sizes are the official ggml (non-quantized) file sizes from
/// https://huggingface.co/ggerganov/whisper.cpp - approximate, for UI display only.
/// </summary>
public static class ModelCatalog
{
    public static readonly ModelOption Small = new(
        Key: "small",
        DisplayName: "小（默认，快，约 488 MB，随程序安装）",
        FileName: "ggml-small.bin",
        GgmlType: GgmlType.Small,
        Bundled: true,
        ApproxSizeBytes: 488_000_000);

    public static readonly ModelOption Medium = new(
        Key: "medium",
        DisplayName: "中（更准，约 1.5 GB，首次选中时下载）",
        FileName: "ggml-medium.bin",
        GgmlType: GgmlType.Medium,
        Bundled: false,
        ApproxSizeBytes: 1_530_000_000);

    public static readonly ModelOption LargeV3Turbo = new(
        Key: "large-v3-turbo",
        DisplayName: "大（最准，约 1.6 GB，首次选中时下载）",
        FileName: "ggml-large-v3-turbo.bin",
        GgmlType: GgmlType.LargeV3Turbo,
        Bundled: false,
        ApproxSizeBytes: 1_620_000_000);

    public static readonly ModelOption[] All = { Small, Medium, LargeV3Turbo };

    /// <summary>Falls back to <see cref="Small"/> for an unknown/empty key, which covers both
    /// brand-new settings and any corrupted/foreign value - never throws.</summary>
    public static ModelOption Resolve(string? key)
    {
        foreach (var option in All)
        {
            if (option.Key == key) return option;
        }
        return Small;
    }

    /// <summary>Resolves where a model's .bin file lives on disk: alongside the exe for the
    /// bundled Small model (unchanged from pre-F01 behavior), or under %LOCALAPPDATA% for models
    /// that get downloaded on demand.</summary>
    public static string GetLocalPath(ModelOption option, string bundledModelsDir)
    {
        if (option.Bundled)
            return Path.Combine(bundledModelsDir, option.FileName);

        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "OpenSuperWhisper", "Models");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, option.FileName);
    }
}
