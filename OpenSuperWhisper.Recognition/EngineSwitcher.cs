using OpenSuperWhisper.Core;

namespace OpenSuperWhisper.Recognition;

/// <summary>
/// v1.2.0 双引擎：让「闪电（SenseVoice）⇄ Whisper」运行时切换对 DictationController 完全透明——
/// 控制器拿到的始终是这一个 ITranscriptionEngine，内部指向哪个真实引擎由 App 的 SwitchEngineAsync
/// 通过 <see cref="SwapAsync"/> 更换。
///
/// 并发安全：TranscribeAsync 与 SwapAsync 用同一把锁串行。没有这把锁，切换时 Dispose 旧引擎会
/// 撞上还在跑的识别（两个引擎包的都是原生对象，use-after-free 不是 .NET 异常是进程崩溃）。
/// 识别单次最长约 2.5s（Whisper）/0.2s（SenseVoice），切换请求等一下正在跑的识别是正确行为——
/// 与 WhisperTranscriptionEngine 内部 factory 锁的既有约定一致。
/// </summary>
public sealed class EngineSwitcher : ITranscriptionEngine
{
    private readonly SemaphoreSlim _lock = new(1, 1);
    private ITranscriptionEngine _inner;

    public EngineSwitcher(ITranscriptionEngine initial) => _inner = initial;

    /// <summary>Initializes whichever engine is currently active. Only used on the startup
    /// path - engine switches arrive pre-initialized via <see cref="SwapAsync"/>.</summary>
    public async Task InitializeAsync(string modelPath)
    {
        await _lock.WaitAsync().ConfigureAwait(false);
        try { await _inner.InitializeAsync(modelPath).ConfigureAwait(false); }
        finally { _lock.Release(); }
    }

    public async Task<string> TranscribeAsync(float[] samples16kMono, string language, string? prompt = null, Action<string>? onPartialResult = null, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try { return await _inner.TranscribeAsync(samples16kMono, language, prompt, onPartialResult, ct).ConfigureAwait(false); }
        finally { _lock.Release(); }
    }

    /// <summary>Replaces the active engine with an already-initialized one and disposes the old
    /// engine - after waiting out any in-flight transcription.</summary>
    public async Task SwapAsync(ITranscriptionEngine newEngine)
    {
        await _lock.WaitAsync().ConfigureAwait(false);
        try
        {
            var old = _inner;
            _inner = newEngine;
            old.Dispose();
        }
        finally
        {
            _lock.Release();
        }
    }

    public void Dispose()
    {
        _inner.Dispose();
        _lock.Dispose();
    }
}
