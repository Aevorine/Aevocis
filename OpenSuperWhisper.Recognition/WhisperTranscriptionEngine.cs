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

        // F16: 有兼容的 GPU（含核显）就用 GPU 解码，没有就退回 CPU - 全自动，不用用户操心。
        // UseGpu=true 其实是 WhisperFactoryOptions 的默认值；这里写明是为了让"为什么装了
        // Whisper.net.Runtime.Vulkan 这个包"有据可查。真正的探测/回退逻辑在 Whisper.net 的
        // 原生库加载器里：按 CUDA -> Vulkan -> CoreML -> OpenVino -> CPU 顺序探测这台机器上
        // 实际能跑起来的运行时，自动选第一个可用的 - 找不到任何 GPU 运行时就直接用 CPU 包，
        // 全程不抛错也不用应用层自己写探测代码。
        //
        // 下面这层 try/catch 是双保险：万一 GPU 运行时探测到了、但初始化仍然失败（比如显卡
        // 驱动损坏），捕获后显式回退到纯 CPU 选项，保证识别功能不会因为 GPU 问题被拖垮。
        try
        {
            _factory = WhisperFactory.FromPath(modelPath, new WhisperFactoryOptions { UseGpu = true });
        }
        catch (Exception ex)
        {
            Log.Error("GPU 运行时初始化失败，回退到纯 CPU 解码", ex);
            _factory = WhisperFactory.FromPath(modelPath, new WhisperFactoryOptions { UseGpu = false });
        }
        return Task.CompletedTask;
    }

    /// <summary>
    /// F03: sentinel AppSettings.Language value for "中英混合" - a single utterance that switches
    /// between Chinese and English mid-sentence ("帮我 commit 一下"). Plain "auto" already lets
    /// Whisper decode either language, but real testing (see WhisperTranscriptionEngine harness
    /// notes in the F03/F16 commit) showed it still mangles embedded English technical terms
    /// (e.g. "commit" -> "commap", "bug" -> "but", "pull request" -> "poor request") and
    /// sometimes drifts into Traditional Chinese script mid-transcript. WithPrompt primes the
    /// decoder with the vocabulary/script it should expect, which measurably fixes both.
    /// </summary>
    private const string MixedLanguageValue = "mixed";

    /// <summary>
    /// Initial prompt for the "mixed" mode: real Simplified Chinese sentences that switch to
    /// English mid-clause for common tech vocabulary, mirroring the kind of speech this mode
    /// targets ("帮我 commit 一下"). Whisper conditions its decoding on this text, which biases
    /// it toward Simplified Chinese script and toward recognizing these English words correctly
    /// instead of mishearing them as similar-sounding Chinese/English words.
    /// </summary>
    private const string MixedLanguagePrompt =
        "这是一段中英文混合的语音。帮我 commit 一下这段代码，然后 deploy 到 production 环境。" +
        "这个 bug 我已经 fix 了，麻烦你 review 一下这个 pull request。";

    public async Task<string> TranscribeAsync(float[] samples16kMono, string language, string? prompt = null, Action<string>? onPartialResult = null, CancellationToken ct = default)
    {
        if (_factory is null)
            throw new InvalidOperationException("识别引擎尚未初始化，先调用 InitializeAsync");

        var isMixed = string.Equals(language, MixedLanguageValue, StringComparison.OrdinalIgnoreCase);
        var builder = _factory.CreateBuilder()
            .WithLanguage(isMixed || string.IsNullOrWhiteSpace(language) ? "auto" : language);

        // F03（中英混合示例文本）与 F06（按软件的提示词）都是通过 WithPrompt 起作用 - 两个都
        // 配置了就拼在一起用而不是互相覆盖，不然同时开着这两个功能时会有一个悄悄失效。
        var effectivePrompt = isMixed
            ? (string.IsNullOrWhiteSpace(prompt) ? MixedLanguagePrompt : $"{MixedLanguagePrompt} {prompt}")
            : prompt;

        if (!string.IsNullOrWhiteSpace(effectivePrompt))
        {
            builder = builder.WithPrompt(effectivePrompt);
            // WithCarryInitialPrompt keeps re-priming every decode window (not just the first),
            // so the vocabulary/script hint stays in effect across longer recordings too.
            if (isMixed) builder = builder.WithCarryInitialPrompt(true);
        }

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
