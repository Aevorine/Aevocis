using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using OpenSuperWhisper.Core.Models;
using OpenSuperWhisper.Storage;

namespace OpenSuperWhisper.App;

/// <summary>
/// F29 新手引导: shown once on first launch (settings.HasSeenOnboarding == false) to explain the
/// three things a brand-new user needs to know - which key to hold, what to do while holding it,
/// and where the recognized text ends up - without making them read a README. Non-modal (Show(),
/// not ShowDialog(), from the caller in App.xaml.cs) so it never blocks model loading/hotkey
/// registration happening in the background. Closing it any way (「知道了」/「跳过」/Alt+F4/X)
/// marks onboarding seen exactly once, via the Closed event rather than only the button handlers.
/// </summary>
public partial class OnboardingWindow : Window
{
    private sealed record Step(string Title, string Body);

    private readonly AppSettings _settings;
    private readonly SettingsStore _settingsStore;
    private readonly Step[] _steps;
    private int _stepIndex;
    private bool _acknowledged;

    public OnboardingWindow(AppSettings settings, SettingsStore settingsStore)
    {
        InitializeComponent();
        _settings = settings;
        _settingsStore = settingsStore;

        var hotkeyName = VkToDisplayName(settings.PushToTalkVirtualKeyCode);
        _steps = new[]
        {
            new Step("第 1 步：按住热键", $"按住「{hotkeyName}」键不放，准备开始说话（可以在设置里换成别的键）。"),
            new Step("第 2 步：说话", "按住的同时正常说话，说完松开按键就行。"),
            new Step("第 3 步：文字自动出现", "识别出的文字会自动打在你当前光标所在的地方，不用手动复制粘贴。"),
        };

        UpdateStep();
        Closed += (_, _) => Acknowledge();
    }

    private void UpdateStep()
    {
        TitleText.Text = _steps[_stepIndex].Title;
        BodyText.Text = _steps[_stepIndex].Body;

        var activeBrush = (Brush)FindResource("PaperAccent");
        var inactiveBrush = (Brush)FindResource("PaperBorder");
        for (var i = 0; i < Dots.Children.Count; i++)
        {
            ((Ellipse)Dots.Children[i]).Fill = i == _stepIndex ? activeBrush : inactiveBrush;
        }

        BackButton.Visibility = _stepIndex == 0 ? Visibility.Hidden : Visibility.Visible;
        NextButton.Content = _stepIndex == _steps.Length - 1 ? "知道了，开始用" : "下一步";
    }

    private void BackButton_Click(object sender, RoutedEventArgs e)
    {
        if (_stepIndex == 0) return;
        _stepIndex--;
        UpdateStep();
    }

    private void NextButton_Click(object sender, RoutedEventArgs e)
    {
        if (_stepIndex < _steps.Length - 1)
        {
            _stepIndex++;
            UpdateStep();
            return;
        }
        Close();
    }

    private void SkipButton_Click(object sender, RoutedEventArgs e) => Close();

    private void Acknowledge()
    {
        if (_acknowledged) return;
        _acknowledged = true;
        _settings.HasSeenOnboarding = true;
        _settingsStore.Save(_settings);
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
