using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using OpenSuperWhisper.Audio;
using OpenSuperWhisper.Core.Models;
using OpenSuperWhisper.Recognition;
using OpenSuperWhisper.Storage;

namespace OpenSuperWhisper.App;

/// <summary>
/// Real settings UI: rebind the push-to-talk key (captured live from a physical key press),
/// pick the recognition language, pick which microphone to record from, and pick which
/// recognition model to use. Save persists all of it via SettingsStore; the hotkey change applies
/// immediately to the running hook, the microphone/language choices apply on the next recording
/// (DictationController re-reads AppSettings fresh every time), and the model choice applies
/// immediately too (downloading it first if needed) via <see cref="_switchModel"/> - no app
/// restart needed for any of it.
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
    private readonly Func<ModelOption, IProgress<ModelDownloadProgress>?, Task<(bool Success, string? ErrorMessage)>> _switchModel;

    private bool _capturingHotkey;
    private int _pendingVkCode;
    private bool _switchingModel;
    private readonly DispatcherTimer _resourceUsageTimer;
    private TimeSpan _lastCpuTime;
    private DateTime _lastCpuSampleAt;

    public SettingsWindow(
        AppSettings settings,
        SettingsStore settingsStore,
        Action<int> applyHotkeyLive,
        Func<ModelOption, IProgress<ModelDownloadProgress>?, Task<(bool Success, string? ErrorMessage)>> switchModel)
    {
        InitializeComponent();
        _settings = settings;
        _settingsStore = settingsStore;
        _applyHotkeyLive = applyHotkeyLive;
        _switchModel = switchModel;

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

        ModelComboBox.ItemsSource = ModelCatalog.All;
        ModelComboBox.SelectedItem = ModelCatalog.Resolve(settings.ModelSize);

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

        // F01: block closing the window while a model switch (possibly still downloading) is in
        // flight - the switch itself isn't tied to this window's lifetime (it keeps running via
        // App even if the window went away), but letting the user close it mid-switch would hide
        // the only progress/failure feedback they have, with no way to tell later whether it
        // actually finished.
        Closing += (_, e) =>
        {
            if (!_switchingModel) return;
            e.Cancel = true;
            MessageBox.Show(this, "识别模型正在切换/下载中，请等它完成后再关闭设置窗口。", "超语音", MessageBoxButton.OK, MessageBoxImage.Information);
        };
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

    /// <summary>
    /// F01/F08: saves the non-model settings immediately as before, then - only if the selected
    /// model actually differs from the currently-active one - switches to it via
    /// <see cref="_switchModel"/> (which downloads it first if needed) before closing the window.
    /// Async so the UI thread is never blocked during a download; controls are disabled and a
    /// live progress line is shown for the duration so it can't look like the app hung. On
    /// failure the window stays open (with an error shown) so the user can retry or pick a
    /// different model instead of the failure being silently lost behind a closed window.
    /// </summary>
    private async void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        if (_switchingModel) return;

        _settings.PushToTalkVirtualKeyCode = _pendingVkCode;
        _settings.Language = ((LanguageOption)LanguageComboBox.SelectedItem).Value;
        _settings.MicrophoneDeviceId = ((MicrophoneDevices.Info)MicrophoneComboBox.SelectedItem).Id;
        _settings.AutoStartWithWindows = AutoStartCheckBox.IsChecked == true;
        _settings.AutocorrectPunctuation = AutocorrectPunctuationCheckBox.IsChecked == true;
        _settings.HistoryRetentionDays = ((RetentionOption)HistoryRetentionComboBox.SelectedItem).Days;

        var selectedModel = (ModelOption)ModelComboBox.SelectedItem;
        var modelChanged = selectedModel.Key != _settings.ModelSize;

        _settingsStore.Save(_settings);
        _applyHotkeyLive(_pendingVkCode);
        AutoStart.SetEnabled(_settings.AutoStartWithWindows);

        if (!modelChanged)
        {
            Close();
            return;
        }

        _switchingModel = true;
        SetControlsEnabled(false);
        ModelStatusTextBlock.Text = selectedModel.Bundled
            ? "正在切换模型..."
            : $"正在切换模型（本地没有的话会先下载，约 {selectedModel.ApproxSizeDisplay}，请勿关闭窗口）...";

        var progress = new Progress<ModelDownloadProgress>(p =>
        {
            ModelStatusTextBlock.Text = p.TotalBytesApprox > 0
                ? $"正在下载 {selectedModel.DisplayName}：{p.BytesDownloaded / (1024.0 * 1024):F0} MB / 约 {selectedModel.ApproxSizeDisplay}（{p.PercentApprox:F0}%）"
                : $"正在下载 {selectedModel.DisplayName}：{p.BytesDownloaded / (1024.0 * 1024):F0} MB";
        });

        (bool Success, string? ErrorMessage) result;
        try
        {
            result = await _switchModel(selectedModel, progress);
        }
        catch (Exception ex)
        {
            // _switchModel (App.SwitchModelAsync) already catches internally and should never
            // throw, but this UI-side call is the last line of defense against an unhandled
            // exception on the UI thread taking the whole app down over a model switch.
            result = (false, ex.Message);
        }

        _switchingModel = false;
        SetControlsEnabled(true);

        if (result.Success)
        {
            ModelStatusTextBlock.Text = "";
            Close();
        }
        else
        {
            ModelStatusTextBlock.Text = $"模型切换失败：{result.ErrorMessage}";
            MessageBox.Show(this,
                $"识别模型切换失败：{result.ErrorMessage}\n\n可以重新点击「保存」重试，或改选其他模型；当前使用的模型不受影响。",
                "超语音", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>Disables everything but the model-status text while a switch/download is in
    /// flight, so there's no way to e.g. start capturing a new hotkey or re-click Save mid-switch.</summary>
    private void SetControlsEnabled(bool enabled)
    {
        SaveButtonElement.IsEnabled = enabled;
        CancelButtonElement.IsEnabled = enabled;
        ModelComboBox.IsEnabled = enabled;
        LanguageComboBox.IsEnabled = enabled;
        MicrophoneComboBox.IsEnabled = enabled;
        HotkeyCaptureButton.IsEnabled = enabled;
        AutoStartCheckBox.IsEnabled = enabled;
        AutocorrectPunctuationCheckBox.IsEnabled = enabled;
        HistoryRetentionComboBox.IsEnabled = enabled;
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
