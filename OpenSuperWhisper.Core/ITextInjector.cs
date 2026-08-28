namespace OpenSuperWhisper.Core;

/// <summary>Types text into whatever window currently has OS focus.</summary>
public interface ITextInjector
{
    void InjectText(string text);
}
