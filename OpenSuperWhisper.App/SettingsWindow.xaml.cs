using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Win32;
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
    private sealed record RetentionOption(string Display, int Days);

    private static readonly LanguageOption[] LanguageOptions =
    {
        new("自动检测", "auto"),
        new("中文", "zh"),
        new("英文", "en"),
    };

    private static readonly RetentionOption[] RetentionOptions =
    {
        new("永久保留", 0),
        new("保留 7 天", 7),
        new("保留 30 天", 30),
        new("保留 90 天", 90),
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
    private readonly DispatcherTimer _resourceUsageTimer;
    private TimeSpan _lastCpuTime;
    private DateTime _lastCpuSampleAt;

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

        AutoStartCheckBox.IsChecked = AutoStart.IsEnabled();
        AutocorrectPunctuationCheckBox.IsChecked = settings.AutocorrectPunctuation;

        HistoryRetentionComboBox.ItemsSource = RetentionOptions;
        HistoryRetentionComboBox.SelectedItem = RetentionOptions.FirstOrDefault(o => o.Days == settings.HistoryRetentionDays)
                                                 ?? RetentionOptions[0];

        // F20 内存占用面板: sampled while Settings is open only - no point spending a timer's
        // worth of wakeups on a window the user isn't looking at.
        using (var proc = Process.GetCurrentProcess())
        {
            _lastCpuTime = proc.TotalProcessorTime;
        }
        _lastCpuSampleAt = DateTime.UtcNow;
        _resourceUsageTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _resourceUsageTimer.Tick += (_, _) => UpdateResourceUsage();
        _resourceUsageTimer.Start();
        UpdateResourceUsage();
        Closed += (_, _) => _resourceUsageTimer.Stop();
    }

    private void UpdateResourceUsage()
    {
        using var proc = Process.GetCurrentProcess();
        var memoryMb = proc.WorkingSet64 / (1024.0 * 1024.0);

        var now = DateTime.UtcNow;
        var cpuNow = proc.TotalProcessorTime;
        var wallElapsed = (now - _lastCpuSampleAt).TotalMilliseconds;
        var cpuElapsed = (cpuNow - _lastCpuTime).TotalMilliseconds;
        var cpuPercent = wallElapsed > 0 ? (cpuElapsed / wallElapsed / Environment.ProcessorCount) * 100.0 : 0.0;
        _lastCpuTime = cpuNow;
        _lastCpuSampleAt = now;

        ResourceUsageTextBlock.Text = $"占用：内存 {memoryMb:F0} MB · CPU {cpuPercent:F1}%";
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
        _settings.AutoStartWithWindows = AutoStartCheckBox.IsChecked == true;
        _settings.AutocorrectPunctuation = AutocorrectPunctuationCheckBox.IsChecked == true;
        _settings.HistoryRetentionDays = ((RetentionOption)HistoryRetentionComboBox.SelectedItem).Days;
        _settingsStore.Save(_settings);
        _applyHotkeyLive(_pendingVkCode);
        AutoStart.SetEnabled(_settings.AutoStartWithWindows);
        Close();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void EditTermDictionaryButton_Click(object sender, RoutedEventArgs e)
    {
        var window = new TermDictionaryWindow(new TermDictionaryStore());
        window.ShowDialog();
    }

    /// <summary>F31: exports the currently-loaded settings plus the professional-vocabulary
    /// dictionary (re-read fresh from disk, same as TermDictionaryWindow does, since this window
    /// never caches it) into one JSON file via SettingsPortability.</summary>
    private void ExportSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = "导出设置",
            Filter = "JSON 文件 (*.json)|*.json",
            FileName = "OpenSuperWhisper-settings.json",
        };
        if (dialog.ShowDialog() != true) return;

        try
        {
            var terms = new TermDictionaryStore().Load();
            SettingsPortability.Export(dialog.FileName, _settings, terms);
            MessageBox.Show("设置已导出。", "超语音", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"导出失败：{ex.Message}", "超语音", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>F31: reads the bundle back and writes both halves to disk. Mutates the shared
    /// <see cref="_settings"/> instance in place (rather than swapping in the deserialized
    /// object) and re-applies the hotkey/autostart live, same as Save - so most of the imported
    /// settings take effect immediately, matching what SaveButton_Click already does. What
    /// doesn't take effect until restart (recognition model path/engine, history retention purge
    /// timing) is called out honestly in the confirmation message rather than silently claimed to
    /// "just work".</summary>
    private void ImportSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "导入设置",
            Filter = "JSON 文件 (*.json)|*.json",
        };
        if (dialog.ShowDialog() != true) return;

        try
        {
            var bundle = SettingsPortability.Import(dialog.FileName);
            CopySettings(bundle.Settings, _settings);
            _settingsStore.Save(_settings);
            new TermDictionaryStore().Save(bundle.Terms);
            _applyHotkeyLive(_settings.PushToTalkVirtualKeyCode);
            AutoStart.SetEnabled(_settings.AutoStartWithWindows);
            MessageBox.Show(
                "设置已导入，热键/语言/麦克风等已立即生效。识别模型路径、历史保留期等少数设置需要重启程序才能完全生效。",
                "超语音", MessageBoxButton.OK, MessageBoxImage.Information);
            Close();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"导入失败：{ex.Message}", "超语音", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private static void CopySettings(AppSettings from, AppSettings to)
    {
        to.ModelPath = from.ModelPath;
        to.Language = from.Language;
        to.MicrophoneDeviceId = from.MicrophoneDeviceId;
        to.PushToTalkVirtualKeyCode = from.PushToTalkVirtualKeyCode;
        to.AutoStartWithWindows = from.AutoStartWithWindows;
        to.AutocorrectPunctuation = from.AutocorrectPunctuation;
        to.HistoryRetentionDays = from.HistoryRetentionDays;
        to.HasSeenOnboarding = from.HasSeenOnboarding;
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
