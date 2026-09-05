using System.Diagnostics;
using OpenSuperWhisper.Core;
using SherpaOnnx;

namespace OpenSuperWhisper.Recognition;

/// <summary>
/// v1.2.0 默认识别引擎（设置里叫「闪电引擎」）：sherpa-onnx + SenseVoice-small int8。
/// 为什么替换 Whisper small 作为默认（本机 i5-1155G7 实测，2026-08-31，详见 TECH_ROADMAP.md）：
/// 6 秒中文音频 186ms vs 2344ms（快 12 倍）、识别峰值内存 341MB vs 1243MB（省 73%）、
/// 首次识别预热 200ms vs 10.2s、中文简体输出全对 vs 繁体漂移 + 错字。非自回归 CTC 架构
/// 也让它对垃圾/静音音频远不如 Whisper 那样容易产生"字幕組署名"式解码器幻觉。
///
/// 引擎特有的输出癖好在引擎内部消化，出门的文本与 Whisper 同契约，DictationController 链零改动：
/// 1. SenseVoice 输出无标点 -> ct-transformer int8 标点模型恢复（模型文件缺失时优雅降级，只影响标点）。
/// 2. 英文全大写（"COMMIT"）-> SenseVoiceCaseFixer 修正。
///
/// InitializeAsync 收到的是模型「目录」（含 model.int8.onnx + tokens.txt），不是单个文件——
/// 与 Whisper 引擎收 .bin 路径不同，由 App 装配层各自传对（见 App.xaml.cs 的 SwitchEngineAsync
/// 与启动路径）。
/// </summary>
public sealed class SenseVoiceTranscriptionEngine : ITranscriptionEngine
{
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly string? _punctuationModelPath;
    private OfflineRecognizer? _recognizer;
    private OfflinePunctuation? _punctuation;

    /// <param name="punctuationModelPath">ct-transformer 标点模型 .onnx 的路径；null 或文件不存在
    /// 时引擎照常工作，只是不恢复标点（后面 DictationController 的规则式句尾补标点仍在）。</param>
    public SenseVoiceTranscriptionEngine(string? punctuationModelPath = null)
    {
        _punctuationModelPath = punctuationModelPath;
    }

    public async Task InitializeAsync(string modelPath)
    {
        var modelFile = Path.Combine(modelPath, "model.int8.onnx");
        var tokensFile = Path.Combine(modelPath, "tokens.txt");
        if (!File.Exists(modelFile))
            throw new FileNotFoundException("找不到 SenseVoice 识别模型文件", modelFile);
        if (!File.Exists(tokensFile))
            throw new FileNotFoundException("找不到 SenseVoice 词表文件", tokensFile);

        // 构建/预热在线程池上做，避免占住调用方（App 启动路径）。
        var (recognizer, punctuation) = await Task.Run(() =>
        {
            var sw = Stopwatch.StartNew();
            var config = new OfflineRecognizerConfig();
            config.ModelConfig.SenseVoice.Model = modelFile;
            // ITN：数字/百分比等按书面形式输出（"百分之九十二点三"->"92.3%"）。
            config.ModelConfig.SenseVoice.UseInverseTextNormalization = 1;
            config.ModelConfig.Tokens = tokensFile;
            // 实测 4 线程时 6s 音频 186ms；核数更少的机器用核数本身。
            config.ModelConfig.NumThreads = Math.Min(4, Environment.ProcessorCount);
            var rec = new OfflineRecognizer(config);

            OfflinePunctuation? punct = null;
            if (!string.IsNullOrEmpty(_punctuationModelPath) && File.Exists(_punctuationModelPath))
            {
                var pc = new OfflinePunctuationConfig();
                pc.Model.CtTransformer = _punctuationModelPath;
                pc.Model.NumThreads = Math.Min(4, Environment.ProcessorCount);
                punct = new OfflinePunctuation(pc);
            }
            else
            {
                Log.Info($"标点模型未找到（{_punctuationModelPath}），本次不做标点恢复");
            }

            // 预热：首次 Decode 有一次性初始化开销（实测约 200ms），用 0.3 秒静音在加载阶段吃掉它，
            // 让用户的第一次真实听写就达到稳态速度。
            using (var s = rec.CreateStream())
            {
                s.AcceptWaveform(16000, new float[4800]);
                rec.Decode(s);
            }
            Log.Info($"SenseVoice 引擎就绪（含预热），耗时 {sw.ElapsedMilliseconds}ms，标点恢复：{(punct is null ? "关" : "开")}");
            return (rec, punct);
        }).ConfigureAwait(false);

        await _lock.WaitAsync().ConfigureAwait(false);
        try
        {
            var oldRec = _recognizer;
            var oldPunct = _punctuation;
            _recognizer = recognizer;
            _punctuation = punctuation;
            oldRec?.Dispose();
            oldPunct?.Dispose();
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>语言由 SenseVoice 自动检测（它逐句发语种 token，中英夹杂天然支持），所以
    /// <paramref name="language"/>（含 "mixed"）与 <paramref name="prompt"/>（Whisper 的
    /// initial prompt 机制，这里没有对应物）都被有意忽略——不是没接线，是引擎不需要。</summary>
    public async Task<string> TranscribeAsync(float[] samples16kMono, string language, string? prompt = null, Action<string>? onPartialResult = null, CancellationToken ct = default)
    {
        // 与 Whisper 引擎同样的持锁范围：整个识别期间持有，防止并发 InitializeAsync 换模型时
        // 把正在使用的原生对象 Dispose 掉（use-after-free）。SenseVoice 单次识别只有约 0.2 秒，
        // 锁的等待代价可忽略。
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_recognizer is null)
                throw new InvalidOperationException("识别引擎尚未初始化，先调用 InitializeAsync");

            return await Task.Run(() =>
            {
                using var stream = _recognizer.CreateStream();
                stream.AcceptWaveform(16000, samples16kMono);
                _recognizer.Decode(stream);
                var text = stream.Result.Text.Trim();
                if (text.Length == 0) return text;

                // 引擎特有后处理（顺序有讲究）：先把全大写英文降为小写，再恢复标点（标点模型
                // 是在常规大小写文本上训练的），最后按恢复出来的句读重新大写拉丁句首。
                text = SenseVoiceCaseFixer.LowercaseAllCapsWords(text);
                if (_punctuation is not null)
                    text = _punctuation.AddPunct(text).Trim();
                text = SenseVoiceCaseFixer.CapitalizeSentenceStarts(text);

                // 一次性把最终文本回报给进度回调：SenseVoice 是整段一次出结果（约 0.2s），没有
                // Whisper 那种逐段流式；调用方用它更新"识别中"浮层，拿到的直接就是完整结果。
                if (onPartialResult is not null && text.Length > 0) onPartialResult(text);
                return text;
            }, ct).ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
    }

    public void Dispose()
    {
        _recognizer?.Dispose();
        _punctuation?.Dispose();
        _lock.Dispose();
    }
}
