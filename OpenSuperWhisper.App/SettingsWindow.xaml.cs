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
    private static readonly MicrophoneDevices.Info FollowSystemDefault = new("", "自动（优先刚连接的耳机等设备）");

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
    private readonly Action<int> _applyHotkeyLive;
    private readonly Action<Dictionary<string, int>> _applyAppHotkeysLive;

    private bool _capturingHotkey;
    private int _pendingVkCode;
    private readonly DispatcherTimer _resourceUsageTimer;
    private TimeSpan _lastCpuTime;
    private DateTime _lastCpuSampleAt;

    public SettingsWindow(
        AppSettings settings,
        SettingsStore settingsStore,
        Action<int> applyHotkeyLive,
        Action<Dictionary<string, int>> applyAppHotkeysLive)
    {
        InitializeComponent();
        _settings = settings;
        _settingsStore = settingsStore;
        _applyHotkeyLive = applyHotkeyLive;
        _applyAppHotkeysLive = applyAppHotkeysLive;

        _pendingVkCode = settings.PushToTalkVirtualKeyCode;
        HotkeyCaptureButton.Content = VkToDisplayName(_pendingVkCode);

        AppPromptsTextBox.Text = FormatAppSpecificPrompts(settings.AppSpecificPrompts);
        AppHotkeysTextBox.Text = FormatAppSpecificHotkeys(settings.AppSpecificHotkeys);

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
        _settings.AppSpecificPrompts = ParseAppSpecificPrompts(AppPromptsTextBox.Text);
        _settings.AppSpecificHotkeys = ParseAppSpecificHotkeys(AppHotkeysTextBox.Text);
        _settingsStore.Save(_settings);
        _applyHotkeyLive(_pendingVkCode);
        _applyAppHotkeysLive(_settings.AppSpecificHotkeys);
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
        to.AppSpecificPrompts = from.AppSpecificPrompts;
        to.AppSpecificHotkeys = from.AppSpecificHotkeys;
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
