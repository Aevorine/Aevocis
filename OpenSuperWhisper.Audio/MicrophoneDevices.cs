using NAudio.CoreAudioApi;

namespace OpenSuperWhisper.Audio;

/// <summary>Lists the microphones Settings can offer to record from.</summary>
public static class MicrophoneDevices
{
    public sealed record Info(string Id, string Name);

    /// <summary>Every capture device Windows currently knows about - active ones first, then
    /// disconnected/disabled ones labeled as such (a Bluetooth headset the user picked earlier
    /// but isn't wearing right now should still show up as their saved choice, not silently
    /// vanish from Settings and get swapped back to "follow system default" the moment they hit
    /// Save without noticing). Best-effort: an empty list just means Settings offers no
    /// alternatives besides "follow the system default", never an error.</summary>
    public static IReadOnlyList<Info> List()
    {
        var result = new List<Info>();
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            var devices = enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.All).ToList();
            foreach (var device in devices.OrderByDescending(d => d.State == DeviceState.Active))
            {
                var suffix = device.State switch
                {
                    DeviceState.Active => "",
                    DeviceState.Disabled => "（已禁用）",
                    DeviceState.Unplugged => "（未连接）",
                    _ => "（不可用）",
                };
                result.Add(new Info(device.ID, device.FriendlyName + suffix));
                device.Dispose();
            }
        }
        catch
        {
            // Best effort - Settings just falls back to offering only "system default".
        }
        return result;
    }
}
