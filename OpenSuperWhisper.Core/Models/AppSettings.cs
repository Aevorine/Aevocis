namespace OpenSuperWhisper.Core.Models;

public sealed class AppSettings
{
    public string ModelPath { get; set; } = "";
    public string Language { get; set; } = "auto";
    /// <summary>WASAPI endpoint ID of the microphone to record from, or "" to follow whichever
    /// device Windows shows as the system's default input device.</summary>
    public string MicrophoneDeviceId { get; set; } = "";
    public int PushToTalkVirtualKeyCode { get; set; } = 0xA3; // VK_RCONTROL
    public bool AutoStartWithWindows { get; set; }
    public bool AutocorrectPunctuation { get; set; } = true;
}
