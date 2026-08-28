namespace OpenSuperWhisper.Core;

/// <summary>Captures microphone audio as 16kHz mono float32 PCM (the format whisper.cpp expects).</summary>
public interface IAudioRecorder : IDisposable
{
    /// <param name="microphoneDeviceId">WASAPI endpoint ID of the microphone to use, or ""/null
    /// to follow the system's default input device.</param>
    void Start(string? microphoneDeviceId);
    float[] Stop();
}
