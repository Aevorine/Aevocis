namespace OpenSuperWhisper.Core;

public interface ITranscriptionEngine : IDisposable
{
    Task InitializeAsync(string modelPath);

    /// <summary>
    /// Transcribes a complete, already-recorded utterance. This is NOT real-time
    /// speech-to-text (the audio must already be fully captured before this is called) - it's a
    /// one-shot batch transcription of the whole clip. Whisper.net's underlying ProcessAsync,
    /// however, yields recognized segments one at a time as it works through the clip rather than
    /// only at the very end, so <paramref name="onPartialResult"/> (when supplied) is invoked
    /// with the transcript accumulated so far after each segment lands - letting a caller display
    /// the text building up progressively during the "recognizing" phase instead of only once
    /// transcription is fully done. It is still called only during recognition of one finished
    /// recording, never while the user is still speaking.
    /// </summary>
    Task<string> TranscribeAsync(float[] samples16kMono, string language, CancellationToken ct = default, Action<string>? onPartialResult = null);
}
