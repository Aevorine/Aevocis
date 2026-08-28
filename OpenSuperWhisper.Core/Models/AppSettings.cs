namespace OpenSuperWhisper.Core.Models;

public sealed class AppSettings
{
    public string ModelPath { get; set; } = "";
    public string Language { get; set; } = "auto";
    public int PushToTalkVirtualKeyCode { get; set; } = 0xA3; // VK_RCONTROL
    public bool AutoStartWithWindows { get; set; }
    public bool AutocorrectPunctuation { get; set; } = true;
}
