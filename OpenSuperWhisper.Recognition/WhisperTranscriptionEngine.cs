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

    public async Task<string> TranscribeAsync(float[] samples16kMono, string language, string? prompt = null, Action<string>? onPartialResult = null, CancellationToken ct = default)
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

        // F17: ProcessAsync already yields each recognized segment as soon as it's decoded
        // (it's not waiting for the whole clip to finish before producing anything) - we were
        // just throwing that away by only reading the final concatenated StringBuilder. Reporting
        // the running total after every segment lets the caller show the transcript building up
        // instead of a static "识别中..." for however long the whole clip takes.
        var sb = new StringBuilder();
        await foreach (var segment in processor.ProcessAsync(samples16kMono))
        {
            ct.ThrowIfCancellationRequested();
            sb.Append(segment.Text);
            if (onPartialResult is not null && !string.IsNullOrWhiteSpace(segment.Text))
                onPartialResult(sb.ToString().Trim());
        }
        return sb.ToString().Trim();
    }

    public void Dispose() => _factory?.Dispose();
}
