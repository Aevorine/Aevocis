namespace OpenSuperWhisper.Core.Models;

/// <summary>One entry in the user's professional-vocabulary dictionary: whenever <see cref="Wrong"/>
/// shows up in a transcript, it's replaced with <see cref="Correct"/> - e.g. Whisper mishearing a
/// product name ("克劳德" -> "Claude").</summary>
public sealed record TermCorrection(string Wrong, string Correct);
