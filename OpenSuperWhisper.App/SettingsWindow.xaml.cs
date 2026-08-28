using System.Linq;
using System.Windows;
using System.Windows.Input;
using OpenSuperWhisper.Audio;
using OpenSuperWhisper.Core.Models;
using OpenSuperWhisper.Storage;

namespace OpenSuperWhisper.App;

/// <summary>
/// Real settings UI: rebind the push-to-talk key (captured live from a physical key press),
/// pick the recognition language, and pick which microphone to record from. Save persists all
/// three via SettingsStore; the hotkey change applies immediately to the running hook, and the
/// microphone/language choices apply on the next recording (DictationController re-reads
/// AppSettings fresh every time) - no app restart needed either way.
/// </summary>
public partial class SettingsWindow : Window
{
    private sealed record LanguageOption(string Display, string Value);

    private static readonly LanguageOption[] LanguageOptions =
    {
        new("自动检测", "auto"),
        new("中文", "zh"),
        new("英文", "en"),
    };

    /// <summary>Id "" is the sentinel for automatic selection (prefer whichever headset/etc.
    /// was most recently connected, fall back to the built-in mic) - not a real device, always
    /// the first option.</summary>
    private static readonly MicrophoneDevices.Info FollowSystemDefault = new("", "自动（优先刚连接的耳机等设备）");

    private readonly AppSettings _settings;
    private readonly SettingsStore _settingsStore;
    private readonly Action<int> _applyHotkeyLive;

    private bool _capturingHotkey;
    private int _pendingVkCode;

    public SettingsWindow(AppSettings settings, SettingsStore settingsStore, Action<int> applyHotkeyLive)
    {
        InitializeComponent();
        _settings = settings;
        _settingsStore = settingsStore;
        _applyHotkeyLive = applyHotkeyLive;

        _pendingVkCode = settings.PushToTalkVirtualKeyCode;
        HotkeyCaptureButton.Content = VkToDisplayName(_pendingVkCode);

        LanguageComboBox.ItemsSource = LanguageOptions;
        LanguageComboBox.SelectedItem = LanguageOptions.FirstOrDefault(o => o.Value == settings.Language)
                                         ?? LanguageOptions[0];

        // Re-enumerated fresh every time Settings opens - a Bluetooth headset that was
        // disconnected last time this window was open might be connected now, and vice versa.
        var micOptions = new[] { FollowSystemDefault }.Concat(MicrophoneDevices.List()).ToArray();
        MicrophoneComboBox.ItemsSource = micOptions;
        MicrophoneComboBox.SelectedItem = micOptions.FirstOrDefault(o => o.Id == settings.MicrophoneDeviceId)
                                           ?? FollowSystemDefault;
    }

    private void HotkeyCaptureButton_Click(object sender, RoutedEventArgs e)
    {
        _capturingHotkey = true;
        HotkeyCaptureButton.Content = "请按下按键...";
    }

    /// <summary>
    /// Handles the "press the key you want" capture. Wired at the window level (not just the
    /// button) so it tunnels down regardless of which control currently has keyboard focus.
    /// </summary>
    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!_capturingHotkey) return;
        e.Handled = true;

        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.Escape)
        {
            _capturingHotkey = false;
            HotkeyCaptureButton.Content = VkToDisplayName(_pendingVkCode);
            return;
        }

        _pendingVkCode = KeyInterop.VirtualKeyFromKey(key);
        _capturingHotkey = false;
        HotkeyCaptureButton.Content = VkToDisplayName(_pendingVkCode);
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        _settings.PushToTalkVirtualKeyCode = _pendingVkCode;
        _settings.Language = ((LanguageOption)LanguageComboBox.SelectedItem).Value;
        _settings.MicrophoneDeviceId = ((MicrophoneDevices.Info)MicrophoneComboBox.SelectedItem).Id;
        _settingsStore.Save(_settings);
        _applyHotkeyLive(_pendingVkCode);
        Close();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private static string VkToDisplayName(int vk)
    {
        try
        {
            var key = KeyInterop.KeyFromVirtualKey(vk);
            return key == Key.None ? $"VK 0x{vk:X2}" : key.ToString();
        }
        catch
        {
            return $"VK 0x{vk:X2}";
        }
    }
}
