namespace OpenSuperWhisper.Core;

/// <summary>Captures microphone audio as 16kHz mono float32 PCM (the format whisper.cpp expects).</summary>
public interface IAudioRecorder : IDisposable
{
    void Start();
    float[] Stop();
}
