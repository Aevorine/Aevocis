using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace OpenSuperWhisper.App;

/// <summary>
/// Small always-on-top status pill shown near the bottom-center of the primary screen while
/// dictating. Must never steal keyboard focus from whatever app the user is dictating into:
/// besides the WPF-level ShowActivated="False"/Focusable="False", the window's native
/// extended style is patched with WS_EX_NOACTIVATE (belt-and-suspenders - some WPF/Windows
/// combinations still briefly activate a shown window without it) and WS_EX_TOOLWINDOW (keeps
/// it out of the taskbar and Alt+Tab list).
/// </summary>
public partial class RecordingOverlayWindow : Window
{
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int WS_EX_TOOLWINDOW = 0x00000080;

    private const double BarMinHeight = 3.0;
    private const double BarMaxHeight = 16.0;

    /// <summary>F07: rolling window of the last 5 level samples, oldest first - each bar shows
    /// one sample so the strip visibly scrolls as new samples arrive, rather than all 5 bars
    /// jumping to the same height in lockstep (which would look like one pulsing blob, not a
    /// waveform). Fixed-size by construction: Enqueue below always pairs with a Dequeue.</summary>
    private readonly Queue<double> _recentLevels = new(new[] { 0.0, 0.0, 0.0, 0.0, 0.0 });

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    public RecordingOverlayWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => PositionNearBottomCenter();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        var exStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
        SetWindowLong(hwnd, GWL_EXSTYLE, exStyle | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);
    }

    private void PositionNearBottomCenter()
    {
        var workArea = SystemParameters.WorkArea;
        Left = workArea.Left + (workArea.Width - ActualWidth) / 2;
        Top = workArea.Bottom - ActualHeight - 90;
    }

    public void ShowListening()
    {
        StatusText.Text = "正在听...";
        StatusDot.Visibility = Visibility.Collapsed;
        // F07: reset to resting (silent) height so a new recording never starts by flashing
        // whatever level the previous one ended on.
        _recentLevels.Clear();
        for (int i = 0; i < 5; i++) _recentLevels.Enqueue(0.0);
        ApplyBarHeights();
        WaveformBars.Visibility = Visibility.Visible;
        if (!IsVisible) Show();
    }

    /// <summary>F07: called (via App's IAudioRecorder.LevelChanged, already marshaled to the UI
    /// thread by the caller) with the RMS level of the most recently captured audio chunk while
    /// actively listening. No-ops harmlessly if called outside that window (e.g. a stray event
    /// racing HideOverlay) - it only ever touches bar heights, which simply aren't visible then.</summary>
    public void UpdateLevel(float level)
    {
        _recentLevels.Dequeue();
        _recentLevels.Enqueue(Math.Max(0.0, level));
        ApplyBarHeights();
    }

    private void ApplyBarHeights()
    {
        var bars = new[] { Bar0, Bar1, Bar2, Bar3, Bar4 };
        int i = 0;
        foreach (var sample in _recentLevels)
        {
            // Empirical perceptual scale: normalized PCM RMS for ordinary speaking volume sits
            // well under 1.0, so a raw linear map would barely move the bars off their resting
            // height. The x8 multiplier was tuned by ear/eye against a real mic, not measured -
            // adjust if bars read as maxed-out during normal speech or as flat during loud speech.
            var normalized = Math.Clamp(sample * 8.0, 0.0, 1.0);
            bars[i].Height = BarMinHeight + normalized * (BarMaxHeight - BarMinHeight);
            i++;
        }
    }

    public void ShowTranscribing()
    {
        StatusText.Text = "识别中...";
        StatusDot.Visibility = Visibility.Collapsed;
        WaveformBars.Visibility = Visibility.Collapsed;
        if (!IsVisible) Show();
    }

    /// <summary>
    /// F17: called as each Whisper segment comes in while recognizing a just-finished recording
    /// (see DictationController.PartialTranscriptionUpdated) - replaces the static "识别中..."
    /// label with the transcript recognized so far, so the user watches the result build up
    /// instead of staring at an unchanging label for however long recognition takes. The pill
    /// auto-sizes to its text (SizeToContent="WidthAndHeight"), so growing text shifts its width;
    /// re-centering after every update keeps it from drifting off from bottom-center.
    /// </summary>
    public void UpdatePartialText(string partialText)
    {
        if (string.IsNullOrWhiteSpace(partialText)) return;
        StatusText.Text = partialText;
        StatusDot.Visibility = Visibility.Collapsed;
        WaveformBars.Visibility = Visibility.Collapsed;
        if (!IsVisible) Show();
        PositionNearBottomCenter();
    }

    public void HideOverlay()
    {
        if (IsVisible) Hide();
    }
}
