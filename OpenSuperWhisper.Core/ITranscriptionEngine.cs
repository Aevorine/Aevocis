namespace OpenSuperWhisper.Core;

public interface ITranscriptionEngine : IDisposable
{
    Task InitializeAsync(string modelPath);

    /// <param name="prompt">F06: an optional Whisper initial prompt (whisper.cpp's
    /// "prompt tokens" mechanism, exposed by Whisper.net as WhisperProcessorBuilder.WithPrompt)
    /// used to bias recognition toward the focused app's preferred style/vocabulary. Null or
    /// whitespace means "no prompt", identical to pre-F06 behavior.</param>
    Task<string> TranscribeAsync(float[] samples16kMono, string language, string? prompt = null, CancellationToken ct = default);
}
