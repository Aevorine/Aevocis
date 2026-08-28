using System.Text;
using OpenSuperWhisper.Core;
using Whisper.net;

namespace OpenSuperWhisper.Recognition;

/// <summary>Wraps Whisper.net (a whisper.cpp binding) — same recognition engine the macOS app used.</summary>
public sealed class WhisperTranscriptionEngine : ITranscriptionEngine
{
    private WhisperFactory? _factory;

    public Task InitializeAsync(string modelPath)
    {
        if (!File.Exists(modelPath))
            throw new FileNotFoundException("找不到语音识别模型文件", modelPath);

        _factory = WhisperFactory.FromPath(modelPath);
        return Task.CompletedTask;
    }

    public async Task<string> TranscribeAsync(float[] samples16kMono, string language, string? prompt = null, CancellationToken ct = default)
    {
        if (_factory is null)
            throw new InvalidOperationException("识别引擎尚未初始化，先调用 InitializeAsync");

        var builder = _factory.CreateBuilder()
            .WithLanguage(string.IsNullOrWhiteSpace(language) ? "auto" : language);

        // F06: an app-specific prompt (see AppSettings.AppSpecificPrompts) biases recognition
        // toward that app's preferred style/vocabulary, e.g. keeping English identifiers
        // untranslated in an editor. WithPrompt (Whisper.net 1.9.1, confirmed via its docs) sets
        // whisper.cpp's initial-prompt tokens; skipped entirely when there's nothing to add.
        if (!string.IsNullOrWhiteSpace(prompt))
            builder = builder.WithPrompt(prompt);

        using var processor = builder.Build();

        var sb = new StringBuilder();
        await foreach (var segment in processor.ProcessAsync(samples16kMono))
        {
            ct.ThrowIfCancellationRequested();
            sb.Append(segment.Text);
        }
        return sb.ToString().Trim();
    }

    public void Dispose() => _factory?.Dispose();
}
