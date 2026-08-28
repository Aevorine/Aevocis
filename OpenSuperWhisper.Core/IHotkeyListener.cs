namespace OpenSuperWhisper.Core;

/// <summary>Push-to-talk style global hotkey: fires on physical key-down and key-up, system-wide.</summary>
public interface IHotkeyListener : IDisposable
{
    event Action? PressStarted;
    event Action? PressEnded;

    /// <summary>Registers the hotkey. Returns false if the underlying OS hook/registration
    /// failed - callers must not treat the app as armed when this returns false.</summary>
    bool Start();
    void Stop();
}
