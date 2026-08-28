namespace OpenSuperWhisper.Core;

public interface ITranscriptionEngine : IDisposable
{
    Task InitializeAsync(string modelPath);
    Task<string> TranscribeAsync(float[] samples16kMono, string language, CancellationToken ct = default);
}
