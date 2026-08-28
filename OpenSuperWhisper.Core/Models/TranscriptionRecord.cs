namespace OpenSuperWhisper.Core.Models;

public sealed class TranscriptionRecord
{
    public DateTimeOffset Timestamp { get; set; }
    public string Text { get; set; } = "";
}
