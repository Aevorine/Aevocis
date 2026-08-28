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

    public void Start()
    {
        lock (_lock) { _buffer.Clear(); }

        _waveIn = new WaveInEvent
        {
            DeviceNumber = ResolveMultimediaDefaultDeviceIndex(),
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
    /// mapper can silently resolve to one that's muted or otherwise not what "Settings > Sound >
    /// 输入设备" shows as the actual default. Recording then "succeeds" (no exception, samples
    /// come back) but is pure silence, which downstream just looks like "nothing was said."
    /// WASAPI's Multimedia-role default endpoint is the one that actually matches the user's
    /// visible default input device, so resolve to that device explicitly by matching its
    /// friendly name against the legacy WaveIn device list, instead of trusting WAVE_MAPPER.
    /// Falls back to -1 (WAVE_MAPPER) if anything here fails or no match is found - never worse
    /// than the previous behavior.
    /// </summary>
    private static int ResolveMultimediaDefaultDeviceIndex()
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            using var defaultDevice = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Multimedia);
            var targetName = defaultDevice.FriendlyName;

            for (int i = 0; i < WaveIn.DeviceCount; i++)
            {
                var name = WaveIn.GetCapabilities(i).ProductName;
                // The legacy WinMM product name is capped at 31 chars and can be truncated
                // relative to the WASAPI friendly name, so match on whichever is the shorter
                // prefix of the other rather than requiring exact equality.
                if (targetName.StartsWith(name, StringComparison.Ordinal) ||
                    name.StartsWith(targetName, StringComparison.Ordinal))
                {
                    Log.Info($"麦克风：已定位到默认输入设备 \"{targetName}\"（WaveIn 设备号 {i}）");
                    return i;
                }
            }
            Log.Info($"麦克风：默认输入设备 \"{targetName}\" 在旧版设备列表中未找到匹配项，退回自动选择");
        }
        catch (Exception ex)
        {
            Log.Info($"麦克风：定位默认输入设备失败，退回自动选择 - {ex.Message}");
        }
        return -1; // WAVE_MAPPER - the previous, less reliable default.
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
