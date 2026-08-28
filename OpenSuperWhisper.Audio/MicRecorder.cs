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
