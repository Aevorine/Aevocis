namespace OpenSuperWhisper.Core;

/// <summary>Captures microphone audio as 16kHz mono float32 PCM (the format whisper.cpp expects).</summary>
public interface IAudioRecorder : IDisposable
{
    /// <param name="microphoneDeviceId">WASAPI endpoint ID of the microphone to use, or ""/null
    /// to follow the system's default input device.</param>
    void Start(string? microphoneDeviceId);
    float[] Stop();

    /// <summary>F07: fires on a background capture thread (never the UI thread - subscribers must
    /// marshal their own dispatch) roughly every ~50ms while actively recording, carrying the RMS
    /// level (0..~1, occasionally slightly above on loud peaks - not hard-clamped) of the audio
    /// chunk just captured. Purely a live "is there sound right now" signal for a UI meter/waveform;
    /// it plays no part in transcription and is unrelated to the dual-track speech-activity scoring
    /// MicRecorder does at Stop() time. Never fires between recordings.</summary>
    event Action<float>? LevelChanged;
}
