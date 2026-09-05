using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Win32;
using OpenSuperWhisper.Audio;
using OpenSuperWhisper.Core;
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
    private sealed record EngineOption(string Display, string Key);

    /// <summary>v1.2.0 双引擎。实测对比见 TECH_ROADMAP.md §2：闪电（SenseVoice int8）6 秒音频
    /// 约 0.2 秒出字、识别峰值内存约 340MB、中文更准；Whisper 支持 99 种语言但慢且吃内存，
    /// 模型按需下载。</summary>
    private static readonly EngineOption[] EngineOptions =
    {
        new("闪电（中文/中英混合，快，省内存，推荐）", "sensevoice"),
        new("Whisper（多语种，较慢，模型按需下载）", "whisper"),
    };

    private static readonly LanguageOption[] LanguageOptions =
    {
        new("自动检测", "auto"),
        new("中文", "zh"),
        new("英文", "en"),
        // F03: 一句话里中英文混说（"帮我 commit 一下"）。内部仍走 Whisper 的 auto 语言检测，
        // 但额外用 WithPrompt 给出中英混合示例文本引导解码 - 实测能显著减少英文技术词被错误
        // 识别成读音相近的中文/英文词、以及简繁体漂移的问题，见 WhisperTranscriptionEngine。
        new("中英混合", "mixed"),
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
    private static readonly MicrophoneDevices.Info FollowSystemDefault = new("", "自动（内置与新接耳机双路同录，自动选优）");

    /// <summary>F06: quick-add buttons for the recommended presets from the original ask
    /// (WeChat/VSCode/Claude Code) - offered in the UI as opt-in suggestions, not written into
    /// AppSettings' own default, so a fresh install still has an empty AppSpecificPrompts and
    /// zero behavior change until the user actually clicks one.</summary>
    private static readonly (string Process, string Prompt)[] PromptPresets =
    {
        ("WeChat", "口语化，日常聊天用语，不要太书面"),
        ("Code", "编程语境，专业术语优先，英文变量名、函数名、类名保持原文不要翻译"),
        ("Claude", "编程语境，专业术语优先，英文变量名、函数名、类名保持原文不要翻译"),
    };

    private readonly AppSettings _settings;
    private readonly SettingsStore _settingsStore;
    private readonly VoiceCommandStore _voiceCommandStore;
    private readonly MacroStore _macroStore;
    private readonly Action<int> _applyHotkeyLive;
    private readonly Action<Dictionary<string, int>> _applyAppHotkeysLive;
    private readonly Action<PushToTalkMode> _applyModeLive;
    private readonly Func<ModelOption, IProgress<ModelDownloadProgress>?, Task<(bool Success, string? ErrorMessage)>> _switchModel;
    private readonly Func<string, IProgress<ModelDownloadProgress>?, Task<(bool Success, string? ErrorMessage)>> _switchEngine;

    private bool _capturingHotkey;
    private int _pendingVkCode;
    private bool _switchingModel;
    private readonly DispatcherTimer _resourceUsageTimer;
    private TimeSpan _lastCpuTime;
    private DateTime _lastCpuSampleAt;

    public SettingsWindow(
        AppSettings settings,
        SettingsStore settingsStore,
        VoiceCommandStore voiceCommandStore,
        MacroStore macroStore,
        Action<int> applyHotkeyLive,
        Action<Dictionary<string, int>> applyAppHotkeysLive,
        Action<PushToTalkMode> applyModeLive,
        Func<ModelOption, IProgress<ModelDownloadProgress>?, Task<(bool Success, string? ErrorMessage)>> switchModel,
        Func<string, IProgress<ModelDownloadProgress>?, Task<(bool Success, string? ErrorMessage)>> switchEngine)
    {
        InitializeComponent();
        _settings = settings;
        _settingsStore = settingsStore;
        _voiceCommandStore = voiceCommandStore;
        _macroStore = macroStore;
        _applyHotkeyLive = applyHotkeyLive;
        _applyAppHotkeysLive = applyAppHotkeysLive;
        _applyModeLive = applyModeLive;
        _switchModel = switchModel;
        _switchEngine = switchEngine;

        _pendingVkCode = settings.PushToTalkVirtualKeyCode;
        HotkeyCaptureButton.Content = VkToDisplayName(_pendingVkCode);

        AppPromptsTextBox.Text = FormatAppSpecificPrompts(settings.AppSpecificPrompts);
        AppHotkeysTextBox.Text = FormatAppSpecificHotkeys(settings.AppSpecificHotkeys);

        // F09: default to Hold's radio button unless the saved setting is Toggle.
        ToggleModeRadioButton.IsChecked = settings.PushToTalkMode == PushToTalkMode.Toggle;
        HoldModeRadioButton.IsChecked = settings.PushToTalkMode != PushToTalkMode.Toggle;

        // F11
        ShowDraftBeforeInjectCheckBox.IsChecked = settings.ShowDraftBeforeInject;

        LanguageComboBox.ItemsSource = LanguageOptions;
        LanguageComboBox.SelectedItem = LanguageOptions.FirstOrDefault(o => o.Value == settings.Language)
                                         ?? LanguageOptions[0];

        // Re-enumerated fresh every time Settings opens - a Bluetooth headset that was
        // disconnected last time this window was open might be connected now, and vice versa.
        var micOptions = new[] { FollowSystemDefault }.Concat(MicrophoneDevices.List()).ToArray();
        MicrophoneComboBox.ItemsSource = micOptions;
        MicrophoneComboBox.SelectedItem = micOptions.FirstOrDefault(o => o.Id == settings.MicrophoneDeviceId)
                                           ?? FollowSystemDefault;

        EngineComboBox.ItemsSource = EngineOptions;
        EngineComboBox.SelectedItem = EngineOptions.FirstOrDefault(o => o.Key == settings.RecognitionEngine)
                                       ?? EngineOptions[0];

        ModelComboBox.ItemsSource = ModelCatalog.All;
        ModelComboBox.SelectedItem = ModelCatalog.Resolve(settings.ModelSize);
        UpdateWhisperModelRowVisibility();

        AutoStartCheckBox.IsChecked = AutoStart.IsEnabled();
        AutocorrectPunctuationCheckBox.IsChecked = settings.AutocorrectPunctuation;

        HistoryRetentionComboBox.ItemsSource = RetentionOptions;
        HistoryRetentionComboBox.SelectedItem = RetentionOptions.FirstOrDefault(o => o.Days == settings.HistoryRetentionDays)
                                                 ?? RetentionOptions[0];

        // F05/F13: plain-text editors, re-loaded fresh every time Settings opens (same reasoning
        // as the microphone list above - reflect whatever's actually on disk right now).
        VoiceCommandsTextBox.Text = VoiceCommandTextFormat.Format(voiceCommandStore.Load());
        MacrosTextBox.Text = VoiceMacroTextFormat.Format(macroStore.Load());

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

    /// <summary>Whisper 模型下拉只在选了 Whisper 引擎时展示——闪电引擎的模型随安装包捆绑，
    /// 没有可选项，露着只会让人疑惑"这个下拉对闪电有没有用"。</summary>
    private void UpdateWhisperModelRowVisibility()
    {
        if (WhisperModelRow is null || EngineComboBox.SelectedItem is not EngineOption option) return;
        WhisperModelRow.Visibility = option.Key == "whisper" ? Visibility.Visible : Visibility.Collapsed;
    }

    private void EngineComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        => UpdateWhisperModelRowVisibility();

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
        _settings.AppSpecificPrompts = ParseAppSpecificPrompts(AppPromptsTextBox.Text);
        _settings.AppSpecificHotkeys = ParseAppSpecificHotkeys(AppHotkeysTextBox.Text);
        _settings.PushToTalkMode = ToggleModeRadioButton.IsChecked == true ? PushToTalkMode.Toggle : PushToTalkMode.Hold;
        _settings.ShowDraftBeforeInject = ShowDraftBeforeInjectCheckBox.IsChecked == true;

        var selectedEngine = ((EngineOption)EngineComboBox.SelectedItem).Key;
        var engineChanged = selectedEngine != _settings.RecognitionEngine;
        var selectedModel = (ModelOption)ModelComboBox.SelectedItem;
        var modelChanged = selectedModel.Key != _settings.ModelSize;

        _settingsStore.Save(_settings);
        _voiceCommandStore.Save(VoiceCommandTextFormat.Parse(VoiceCommandsTextBox.Text));
        _macroStore.Save(VoiceMacroTextFormat.Parse(MacrosTextBox.Text));
        _applyHotkeyLive(_pendingVkCode);
        _applyAppHotkeysLive(_settings.AppSpecificHotkeys);
        _applyModeLive(_settings.PushToTalkMode);
        AutoStart.SetEnabled(_settings.AutoStartWithWindows);

        if (!engineChanged && !modelChanged)
        {
            Close();
            return;
        }

        _switchingModel = true;
        SetControlsEnabled(false);

        var progress = new Progress<ModelDownloadProgress>(p =>
        {
            ModelStatusTextBlock.Text = p.TotalBytesApprox > 0
                ? $"正在下载模型：{p.BytesDownloaded / (1024.0 * 1024):F0} MB / 约 {p.TotalBytesApprox / (1024.0 * 1024):F0} MB（{p.PercentApprox:F0}%）"
                : $"正在下载模型：{p.BytesDownloaded / (1024.0 * 1024):F0} MB";
        });

        (bool Success, string? ErrorMessage) result = (true, null);
        try
        {
            if (engineChanged && selectedEngine == "whisper")
            {
                // 引擎切到 Whisper 时把（可能也刚改过的）模型偏好先写进内存里，让 SwitchEngineAsync
                // 直接按新偏好下载/加载——避免"先按旧模型切引擎、再按新模型二次下载"的双重开销。
                // 失败时回滚内存值：SwitchEngineAsync 只在成功后才落盘。
                var oldModelSize = _settings.ModelSize;
                if (modelChanged) _settings.ModelSize = selectedModel.Key;
                ModelStatusTextBlock.Text = $"正在切换到 Whisper 引擎（{selectedModel.DisplayName}，本地没有的话会先下载，请勿关闭窗口）...";
                result = await _switchEngine("whisper", progress);
                if (!result.Success && modelChanged) _settings.ModelSize = oldModelSize;
            }
            else
            {
                if (engineChanged)
                {
                    ModelStatusTextBlock.Text = "正在切换到闪电引擎...";
                    result = await _switchEngine("sensevoice", progress);
                }
                if (result.Success && modelChanged)
                {
                    ModelStatusTextBlock.Text = selectedEngine == "whisper"
                        ? $"正在切换模型（本地没有的话会先下载，约 {selectedModel.ApproxSizeDisplay}，请勿关闭窗口）..."
                        : "正在记录 Whisper 模型偏好...";
                    result = await _switchModel(selectedModel, progress);
                }
            }
        }
        catch (Exception ex)
        {
            // _switchModel/_switchEngine (App 侧) already catch internally and should never
            // throw, but this UI-side call is the last line of defense against an unhandled
            // exception on the UI thread taking the whole app down over a switch.
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
            ModelStatusTextBlock.Text = $"切换失败：{result.ErrorMessage}";
            MessageBox.Show(this,
                $"识别引擎/模型切换失败：{result.ErrorMessage}\n\n可以重新点击「保存」重试，或改选其他选项；当前使用的引擎不受影响。",
                "超语音", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>Disables everything but the model-status text while a switch/download is in
    /// flight, so there's no way to e.g. start capturing a new hotkey or re-click Save mid-switch.</summary>
    private void SetControlsEnabled(bool enabled)
    {
        SaveButtonElement.IsEnabled = enabled;
        CancelButtonElement.IsEnabled = enabled;
        EngineComboBox.IsEnabled = enabled;
        ModelComboBox.IsEnabled = enabled;
        LanguageComboBox.IsEnabled = enabled;
        MicrophoneComboBox.IsEnabled = enabled;
        HotkeyCaptureButton.IsEnabled = enabled;
        AutoStartCheckBox.IsEnabled = enabled;
        AutocorrectPunctuationCheckBox.IsEnabled = enabled;
        HistoryRetentionComboBox.IsEnabled = enabled;
        // F06/F09/F11/F12/F05/F13 controls didn't exist yet when SetControlsEnabled was first
        // written (F01) - added here so a model switch/download disables everything the window
        // can save, not just the fields F01 itself introduced.
        HoldModeRadioButton.IsEnabled = enabled;
        ToggleModeRadioButton.IsEnabled = enabled;
        ShowDraftBeforeInjectCheckBox.IsEnabled = enabled;
        AppPromptsTextBox.IsEnabled = enabled;
        AppHotkeysTextBox.IsEnabled = enabled;
        VoiceCommandsTextBox.IsEnabled = enabled;
        MacrosTextBox.IsEnabled = enabled;
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
            _applyAppHotkeysLive(_settings.AppSpecificHotkeys);
            _applyModeLive(_settings.PushToTalkMode);
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
        to.AppSpecificPrompts = from.AppSpecificPrompts;
        to.AppSpecificHotkeys = from.AppSpecificHotkeys;
        to.PushToTalkMode = from.PushToTalkMode;
        to.ShowDraftBeforeInject = from.ShowDraftBeforeInject;
    }

    /// <summary>F06 quick-add: appends the tapped preset as a new "进程名|提示词" line, unless
    /// that process name is already present (so repeated clicks don't pile up duplicates).</summary>
    private void PromptPreset_Click(object sender, RoutedEventArgs e)
    {
        var index = int.Parse((string)((Button)sender).Tag);
        var (process, prompt) = PromptPresets[index];

        var existingLines = AppPromptsTextBox.Text.Split('\n');
        if (existingLines.Any(l => l.TrimStart().StartsWith(process + "|", StringComparison.OrdinalIgnoreCase)))
            return;

        var current = AppPromptsTextBox.Text.TrimEnd('\r', '\n');
        AppPromptsTextBox.Text = current.Length == 0 ? $"{process}|{prompt}" : $"{current}{Environment.NewLine}{process}|{prompt}";
        AppPromptsTextBox.CaretIndex = AppPromptsTextBox.Text.Length;
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

    /// <summary>Inverse of VkToDisplayName - accepts either a WPF Key name (e.g. "F13",
    /// case-insensitive) or the "VK 0xNN" hex fallback it produces for keys with no Key enum
    /// member, so anything the app itself ever displayed round-trips.</summary>
    private static bool TryParseVkCode(string text, out int vk)
    {
        text = text.Trim();
        if (text.StartsWith("VK 0x", StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(text.AsSpan(5), System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture, out var raw))
        {
            vk = raw;
            return true;
        }
        if (Enum.TryParse<Key>(text, ignoreCase: true, out var key) && key != Key.None)
        {
            vk = KeyInterop.VirtualKeyFromKey(key);
            return true;
        }
        vk = 0;
        return false;
    }

    private static string SingleLine(string s) => s.Replace("\r", " ").Replace("\n", " ");

    private static string FormatAppSpecificPrompts(Dictionary<string, string> map) =>
        string.Join(Environment.NewLine, map.Select(kv => $"{kv.Key}|{SingleLine(kv.Value)}"));

    private static string FormatAppSpecificHotkeys(Dictionary<string, int> map) =>
        string.Join(Environment.NewLine, map.Select(kv => $"{kv.Key}|{VkToDisplayName(kv.Value)}"));

    /// <summary>Parses the "进程名|提示词" textbox back into a map. Splits only on the first
    /// '|' per line, so a prompt is free to contain '|' itself. Lines with no '|', an empty
    /// process name, or an empty prompt are silently dropped - this is a plain-text convenience
    /// editor, not a strict format, and a stray blank line shouldn't block Save.</summary>
    private static Dictionary<string, string> ParseAppSpecificPrompts(string text)
    {
        var result = new Dictionary<string, string>();
        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r').Trim();
            if (line.Length == 0) continue;
            var sep = line.IndexOf('|');
            if (sep <= 0) continue;
            var processName = line[..sep].Trim();
            var prompt = line[(sep + 1)..].Trim();
            if (processName.Length == 0 || prompt.Length == 0) continue;
            result[processName] = prompt;
        }
        return result;
    }

    /// <summary>Parses the "进程名|按键名" textbox back into a map. A line whose key name
    /// doesn't parse (typo, unsupported key) is skipped and logged rather than blocking Save -
    /// consistent with ParseAppSpecificPrompts' leniency.</summary>
    private static Dictionary<string, int> ParseAppSpecificHotkeys(string text)
    {
        var result = new Dictionary<string, int>();
        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r').Trim();
            if (line.Length == 0) continue;
            var sep = line.IndexOf('|');
            if (sep <= 0) continue;
            var processName = line[..sep].Trim();
            var keyText = line[(sep + 1)..].Trim();
            if (processName.Length == 0 || keyText.Length == 0) continue;
            if (!TryParseVkCode(keyText, out var vk))
            {
                Log.Info($"设置：忽略无法识别的按软件快捷键（{processName}|{keyText}）");
                continue;
            }
            result[processName] = vk;
        }
        return result;
    }
}
