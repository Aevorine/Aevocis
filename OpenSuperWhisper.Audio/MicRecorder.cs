using NAudio.CoreAudioApi;
using NAudio.Wave;
using OpenSuperWhisper.Core;

namespace OpenSuperWhisper.Audio;

/// <summary>Records the default microphone as 16kHz mono PCM and hands back normalized float32 samples.</summary>
public sealed class MicRecorder : IAudioRecorder
{
    private const int SampleRate = 16000;
    private const int MaxRecordingSeconds = 120;
    // 16-bit mono PCM: 2 bytes/sample. Caps the in-memory buffer so holding the hotkey down
    // indefinitely can't grow it unbounded.
    private const int MaxBufferBytes = SampleRate * 2 * MaxRecordingSeconds;

    private WaveInEvent? _waveIn;
    private readonly List<byte> _buffer = new();
    private readonly object _lock = new();

    public void Start(string? microphoneDeviceId)
    {
        lock (_lock) { _buffer.Clear(); }

        _waveIn = new WaveInEvent
        {
            DeviceNumber = ResolveDeviceIndex(microphoneDeviceId),
            WaveFormat = new WaveFormat(SampleRate, 16, 1),
            BufferMilliseconds = 50
        };
        _waveIn.DataAvailable += OnDataAvailable;
        _waveIn.StartRecording();
    }

    /// <summary>
    /// WaveInEvent's default constructor (DeviceNumber = -1, "WAVE_MAPPER") asks the legacy
    /// WinMM mapper to pick a device, and on machines with several capture endpoints - Bluetooth
    /// headset, a virtual/remote-desktop audio driver, the real built-in mic - that legacy
    /// mapper can silently resolve to one that isn't what "Settings > Sound > 输入设备" shows as
    /// the actual default (it's frequently a device bound to the separate "Communications" role
    /// instead, e.g. a Bluetooth headset). Recording then "succeeds" (no exception, samples come
    /// back) but can be near-silent, which downstream just looks like "nothing was said."
    ///
    /// Resolution order: (1) a specific device the user picked in Settings, matched by its
    /// stable WASAPI endpoint ID; (2) WASAPI's Multimedia-role default endpoint, which is the
    /// one that actually matches the user's visible default input device; (3) WAVE_MAPPER, the
    /// previous, least reliable behavior - only reached if both of the above fail.
    /// </summary>
    private static int ResolveDeviceIndex(string? microphoneDeviceId)
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();

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
                            return idx.Value;
                        }
                        Log.Info($"麦克风：手动选择的设备 \"{chosen.FriendlyName}\" 在旧版设备列表中未找到匹配项，退回系统默认");
                    }
                    else
                    {
                        Log.Info($"麦克风：手动选择的设备当前不可用（{chosen.FriendlyName}，状态 {chosen.State}），退回系统默认");
                    }
                }
                catch (Exception ex)
                {
                    Log.Info($"麦克风：手动选择的设备已找不到，退回系统默认 - {ex.Message}");
                }
            }

            using var defaultDevice = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Multimedia);
            var targetName = defaultDevice.FriendlyName;
            var defaultIdx = FindWaveInIndexByName(targetName);
            if (defaultIdx is not null)
            {
                Log.Info($"麦克风：已定位到系统默认输入设备 \"{targetName}\"（WaveIn 设备号 {defaultIdx}）");
                return defaultIdx.Value;
            }
            Log.Info($"麦克风：系统默认输入设备 \"{targetName}\" 在旧版设备列表中未找到匹配项，退回自动选择");
        }
        catch (Exception ex)
        {
            Log.Info($"麦克风：定位输入设备失败，退回自动选择 - {ex.Message}");
        }
        return -1; // WAVE_MAPPER - the previous, least reliable behavior.
    }

    /// <summary>Matches a WASAPI friendly name against the legacy WaveIn device list. The
    /// legacy WinMM product name is capped at 31 chars and can be truncated relative to the
    /// WASAPI friendly name, so match on whichever is the shorter prefix of the other rather
    /// than requiring exact equality.</summary>
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

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        lock (_lock)
        {
            if (_buffer.Count >= MaxBufferBytes) return; // cap reached - drop further audio.

            var count = Math.Min(e.BytesRecorded, MaxBufferBytes - _buffer.Count);
            for (int i = 0; i < count; i++)
                _buffer.Add(e.Buffer[i]);
        }
    }

    public float[] Stop()
    {
        if (_waveIn is null) return Array.Empty<float>();

        _waveIn.StopRecording();
        _waveIn.DataAvailable -= OnDataAvailable;
        _waveIn.Dispose();
        _waveIn = null;

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

    public void Dispose() => _waveIn?.Dispose();
}
