using NAudio.CoreAudioApi;
using NAudio.Wave;
using OpenSuperWhisper.Core;

namespace OpenSuperWhisper.Audio;

/// <summary>
/// Records 16kHz mono PCM and hands back normalized float32 samples.
///
/// v1.2.0「双路同录自动选优」（用户拍板方案，替代 v1.1.0 的「通信默认设备刚连接就单路优先」）：
/// 之前的策略在 2026-08-28 造成用户听写全废——蓝牙耳机只要连着（哪怕没戴、放在桌上）就被抢作
/// 唯一录音设备，蓝牙 A2DP→HFP 切换死区 + 远场收音让采到的音频内容是垃圾，识别端只能输出幻觉。
/// 现在：没有手动固定设备、且「通信默认」≠「多媒体默认」（Windows 自己对"刚接入了耳机"的信号）时，
/// **两路同时录**，停止时按录到的实际内容打分选优——谁真录到了人声用谁，不再赌哪个设备是对的。
///
/// 打分：20ms 帧 RMS 序列的 P90 − P10（帧能量动态差）。真实语音帧能量起伏大（说话段高、停顿段低）
/// 得分高；蓝牙切换死区/未佩戴的远场（近乎全静音）与恒定底噪（P90≈P10）得分都趋近 0。两路得分
/// 全部写日志，"为什么选了这个麦克风"永远可以从 log.txt 直接读出来，不用猜。
///
/// 设备解析优先级（每次录音重新评估，从不缓存）：
/// 1. 用户在设置里手动固定的设备——单路，永远最高优先（保活：与 v1.1.0 行为一致）。
/// 2. 自动：多媒体默认设备必录；通信默认设备与其不同且未静音时，作为第二路同录。
/// 3. 全部解析失败才退回 WAVE_MAPPER 单路。
/// </summary>
public sealed class MicRecorder : IAudioRecorder
{
    private const int SampleRate = 16000;
    private const int MaxRecordingSeconds = 120;
    private const int MaxBufferBytes = SampleRate * 2 * MaxRecordingSeconds;

    /// <summary>One WaveInEvent capture on one device. Each capture has its own buffer+lock:
    /// NAudio raises DataAvailable on a per-device callback thread.</summary>
    private sealed class SingleCapture : IDisposable
    {
        private readonly WaveInEvent _waveIn;
        private readonly List<byte> _buffer = new();
        private readonly object _lock = new();

        public string DeviceName { get; }

        public SingleCapture(int deviceIndex, string deviceName)
        {
            DeviceName = deviceName;
            _waveIn = new WaveInEvent
            {
                DeviceNumber = deviceIndex,
                WaveFormat = new WaveFormat(SampleRate, 16, 1),
                BufferMilliseconds = 50
            };
            _waveIn.DataAvailable += OnDataAvailable;
            _waveIn.StartRecording();
        }

        private void OnDataAvailable(object? sender, WaveInEventArgs e)
        {
            lock (_lock)
            {
                if (_buffer.Count >= MaxBufferBytes) return;
                var count = Math.Min(e.BytesRecorded, MaxBufferBytes - _buffer.Count);
                for (int i = 0; i < count; i++)
                    _buffer.Add(e.Buffer[i]);
            }
        }

        public float[] Stop()
        {
            _waveIn.StopRecording();
            _waveIn.DataAvailable -= OnDataAvailable;
            _waveIn.Dispose();

            byte[] bytes;
            lock (_lock) { bytes = _buffer.ToArray(); }

            var samples = new float[bytes.Length / 2];
            for (int i = 0; i < samples.Length; i++)
            {
                short s = (short)(bytes[i * 2] | (bytes[i * 2 + 1] << 8));
                samples[i] = s / 32768f;
            }
            return samples;
        }

        public void Dispose() => _waveIn.Dispose();
    }

    private readonly List<SingleCapture> _captures = new();

    public void Start(string? microphoneDeviceId)
    {
        _captures.Clear();

        foreach (var (index, name) in ResolveDevicesToRecord(microphoneDeviceId))
        {
            try
            {
                _captures.Add(new SingleCapture(index, name));
            }
            catch (Exception ex) when (_captures.Count > 0)
            {
                // 第二路打不开（设备被独占、蓝牙半掉线等）不致命——第一路还在录，降级为单路即可。
                // 第一路失败仍然向上抛，由 DictationController 走 RecordingFailed 提示用户。
                Log.Info($"麦克风：第二路 \"{name}\" 打开失败，本次单路录音 - {ex.Message}");
            }
        }

        if (_captures.Count == 0)
            throw new InvalidOperationException("没有可用的录音设备");
        if (_captures.Count > 1)
            Log.Info($"麦克风：双路同录（{string.Join(" + ", _captures.Select(c => $"\"{c.DeviceName}\""))}），停止时按语音能量自动选优");
    }

    /// <summary>Yields the WaveIn devices to record from this time, in priority order (first one
    /// is the "primary" whose open-failure is fatal). See class doc for the policy.</summary>
    private static IEnumerable<(int Index, string Name)> ResolveDevicesToRecord(string? microphoneDeviceId)
    {
        MMDeviceEnumerator enumerator;
        try
        {
            enumerator = new MMDeviceEnumerator();
        }
        catch (Exception ex)
        {
            Log.Info($"麦克风：WASAPI 枚举器创建失败，退回 WAVE_MAPPER - {ex.Message}");
            return new[] { (-1, "WAVE_MAPPER") };
        }

        using (enumerator)
        {
            // 1. 手动固定的设备：单路，最高优先（与 v1.1.0 行为一致）。
            if (!string.IsNullOrEmpty(microphoneDeviceId))
            {
                try
                {
                    using var chosen = enumerator.GetDevice(microphoneDeviceId);
                    if (chosen.State == DeviceState.Active)
                    {
                        var idx = FindWaveInIndexByName(chosen.FriendlyName);
                        if (idx is not null)
                        {
                            Log.Info($"麦克风：使用手动选择的输入设备 \"{chosen.FriendlyName}\"（WaveIn 设备号 {idx}）");
                            return new[] { (idx.Value, chosen.FriendlyName) };
                        }
                        Log.Info($"麦克风：手动选择的设备 \"{chosen.FriendlyName}\" 在旧版设备列表中未找到匹配项，退回自动选择");
                    }
                    else
                    {
                        Log.Info($"麦克风：手动选择的设备当前不可用（{chosen.FriendlyName}，状态 {chosen.State}），退回自动选择");
                    }
                }
                catch (Exception ex)
                {
                    Log.Info($"麦克风：手动选择的设备已找不到，退回自动选择 - {ex.Message}");
                }
            }

            // 2. 自动：多媒体默认必录；通信默认不同且未静音 -> 第二路同录。
            var devices = new List<(int Index, string Name)>();
            try
            {
                using var multimediaDefault = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Multimedia);
                var mmIdx = FindWaveInIndexByName(multimediaDefault.FriendlyName);
                if (mmIdx is not null)
                    devices.Add((mmIdx.Value, multimediaDefault.FriendlyName));
                else
                    Log.Info($"麦克风：系统默认设备 \"{multimediaDefault.FriendlyName}\" 在旧版设备列表中未找到匹配项");

                try
                {
                    using var commsDefault = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);
                    if (commsDefault.ID != multimediaDefault.ID && !commsDefault.AudioEndpointVolume.Mute)
                    {
                        var commIdx = FindWaveInIndexByName(commsDefault.FriendlyName);
                        if (commIdx is not null && (devices.Count == 0 || commIdx.Value != devices[0].Index))
                        {
                            Log.Info($"麦克风：检测到「默认通信设备」与「默认设备」不同（\"{commsDefault.FriendlyName}\"），作为第二路同录");
                            devices.Add((commIdx.Value, commsDefault.FriendlyName));
                        }
                    }
                }
                catch
                {
                    // 没有独立的通信默认设备（或查询失败）——单路录多媒体默认即可。
                }
            }
            catch (Exception ex)
            {
                Log.Info($"麦克风：定位默认输入设备失败 - {ex.Message}");
            }

            if (devices.Count > 0) return devices;

            // 3. 最后的兜底。
            Log.Info("麦克风：自动解析无可用设备，退回 WAVE_MAPPER");
            return new[] { (-1, "WAVE_MAPPER") };
        }
    }

    /// <summary>Matches a WASAPI friendly name against the legacy WaveIn device list. The legacy
    /// WinMM product name is capped at 31 chars and can be truncated relative to the WASAPI
    /// friendly name, so match on whichever is the shorter prefix of the other.</summary>
    private static int? FindWaveInIndexByName(string targetName)
    {
        for (int i = 0; i < WaveIn.DeviceCount; i++)
        {
            var name = WaveIn.GetCapabilities(i).ProductName;
            if (targetName.StartsWith(name, StringComparison.Ordinal) ||
                name.StartsWith(targetName, StringComparison.Ordinal))
                return i;
        }
        return null;
    }

    public float[] Stop()
    {
        if (_captures.Count == 0) return Array.Empty<float>();

        var tracks = new List<(string Name, float[] Samples, double Score)>();
        foreach (var capture in _captures)
        {
            var samples = capture.Stop();
            tracks.Add((capture.DeviceName, samples, SpeechActivityScore(samples)));
        }
        _captures.Clear();

        if (tracks.Count == 1) return tracks[0].Samples;

        var best = tracks.OrderByDescending(t => t.Score).First();
        Log.Info("麦克风：双路选优 " +
                 string.Join(" vs ", tracks.Select(t => $"\"{t.Name}\" 得分 {t.Score:F4}（{t.Samples.Length} 采样点）")) +
                 $" → 选用 \"{best.Name}\"");
        return best.Samples;
    }

    /// <summary>
    /// 语音活动度打分：20ms 帧 RMS 序列的 P90 − P10。
    /// 真实说话的帧能量有大起伏（语音段 P90 高、停顿段 P10 低）→ 得分高；
    /// 全静音/蓝牙死区（两者都≈0）与恒定底噪（P90≈P10）→ 得分≈0。
    /// 比"总能量"更稳：一路是恒定电流声、另一路是小声说话时，能量选错、动态差选对。
    /// </summary>
    internal static double SpeechActivityScore(float[] samples)
    {
        const int frameSize = SampleRate / 50; // 20ms = 320 samples
        if (samples.Length < frameSize * 5) return 0;

        var frameRms = new List<double>(samples.Length / frameSize);
        for (int start = 0; start + frameSize <= samples.Length; start += frameSize)
        {
            double sum = 0;
            for (int i = start; i < start + frameSize; i++)
                sum += (double)samples[i] * samples[i];
            frameRms.Add(Math.Sqrt(sum / frameSize));
        }
        frameRms.Sort();
        var p90 = frameRms[(int)(frameRms.Count * 0.9)];
        var p10 = frameRms[(int)(frameRms.Count * 0.1)];
        return p90 - p10;
    }

    public void Dispose()
    {
        foreach (var capture in _captures) capture.Dispose();
        _captures.Clear();
    }
}
