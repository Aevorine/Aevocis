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
    /// mapper can silently resolve to one that isn't what the user actually wants. Recording
    /// then "succeeds" (no exception, samples come back) but can be near-silent, which
    /// downstream just looks like "nothing was said."
    ///
    /// Resolution order, re-evaluated fresh on every recording (never cached), so plugging in or
    /// disconnecting a device between one dictation and the next is picked up automatically with
    /// no restart and no trip to Settings:
    /// 1. A specific device the user pinned in Settings, matched by its stable WASAPI endpoint
    ///    ID - always wins outright if it's currently active.
    /// 2. Otherwise, automatic: Windows separately tracks a "Communications"-role default
    ///    (auto-assigned to whichever headset/handsfree device was most recently connected -
    ///    confirmed on this project's dev machine: pairing a Bluetooth headset makes Windows
    ///    flag it here without touching the general default at all) and a "Multimedia"-role
    ///    default (the general "system default", which stays on the built-in mic array unless
    ///    the user manually changes it in Windows' own Sound settings). When they differ, that
    ///    difference itself *is* "a headset just got connected" - so prefer the
    ///    Communications-role device, unless its capture channel is muted (a muted device would
    ///    just reproduce the original bug), in which case fall back to Multimedia instead of
    ///    silently failing.
    /// 3. WAVE_MAPPER, the previous, least reliable behavior - only reached if everything above
    ///    fails outright (e.g. WASAPI enumeration itself throws).
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

            using var multimediaDefault = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Multimedia);
            MMDevice? preferred = null;
            try
            {
                using var commsDefault = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);
                if (commsDefault.ID != multimediaDefault.ID)
                {
                    if (!commsDefault.AudioEndpointVolume.Mute)
                    {
                        preferred = commsDefault;
                        Log.Info($"麦克风：检测到「默认通信设备」与「默认设备」不同（\"{commsDefault.FriendlyName}\"），当作刚接入的设备优先使用");
                    }
                    else
                    {
                        Log.Info($"麦克风：「默认通信设备」\"{commsDefault.FriendlyName}\" 当前处于静音状态，跳过，改用系统默认设备");
                    }
                }
            }
            catch
            {
                // No separate Communications-role endpoint (or querying it failed) - fine, just
                // use the Multimedia default below.
            }

            var target = preferred ?? multimediaDefault;
            var targetName = target.FriendlyName;
            var idx2 = FindWaveInIndexByName(targetName);
            if (idx2 is not null)
            {
                Log.Info($"麦克风：自动选中输入设备 \"{targetName}\"（WaveIn 设备号 {idx2}）");
                return idx2.Value;
            }
            Log.Info($"麦克风：自动选中的设备 \"{targetName}\" 在旧版设备列表中未找到匹配项，退回自动选择");
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
