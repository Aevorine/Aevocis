using System.Text;
using OpenSuperWhisper.Core;
using Whisper.net;

namespace OpenSuperWhisper.Recognition;

/// <summary>Wraps Whisper.net (a whisper.cpp binding) — same recognition engine the macOS app used.
/// F01: InitializeAsync is safe to call more than once (e.g. switching models from Settings while
/// the app keeps running) - it disposes the previous WhisperFactory instead of leaking it, and a
/// single async lock serializes it against TranscribeAsync so a model swap can never run
/// concurrently with an in-flight transcription (the swap simply waits for the transcription
/// already in progress to finish, then disposes-and-replaces the factory it was using).</summary>
public sealed class WhisperTranscriptionEngine : ITranscriptionEngine
{
    private readonly SemaphoreSlim _factoryLock = new(1, 1);
    private WhisperFactory? _factory;

    public async Task InitializeAsync(string modelPath)
    {
        if (!File.Exists(modelPath))
            throw new FileNotFoundException("找不到语音识别模型文件", modelPath);

        var newFactory = WhisperFactory.FromPath(modelPath);
        await _factoryLock.WaitAsync().ConfigureAwait(false);
        try
        {
            var old = _factory;
            _factory = newFactory;
            old?.Dispose();
        }
        finally
        {
            _factoryLock.Release();
        }
    }

    public async Task<string> TranscribeAsync(float[] samples16kMono, string language, CancellationToken ct = default)
    {
        // Held for the *entire* transcription, not just to snapshot _factory: releasing it early
        // would let a concurrent InitializeAsync dispose the very factory this call is still
        // using mid-ProcessAsync (Whisper.net wraps a native whisper.cpp context - using it after
        // Dispose is a use-after-free, not just a .NET exception). Serializing the two here means
        // a model switch requested mid-transcription simply waits for this call to finish first,
        // which is the correct behavior (also belt-and-suspenders with DictationController's own
        // "don't start a new recording while a switch is in flight" gate).
        await _factoryLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_factory is null)
                throw new InvalidOperationException("识别引擎尚未初始化，先调用 InitializeAsync");

            using var processor = _factory.CreateBuilder()
                .WithLanguage(string.IsNullOrWhiteSpace(language) ? "auto" : language)
                .Build();

            var sb = new StringBuilder();
            await foreach (var segment in processor.ProcessAsync(samples16kMono))
            {
                ct.ThrowIfCancellationRequested();
                sb.Append(segment.Text);
            }
            return sb.ToString().Trim();
        }
        finally
        {
            _factoryLock.Release();
        }
    }

    public void Dispose()
    {
        _factory?.Dispose();
        _factoryLock.Dispose();
    }
}
