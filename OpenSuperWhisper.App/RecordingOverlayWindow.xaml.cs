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
        StatusDot.Visibility = Visibility.Visible;
        if (!IsVisible) Show();
    }

    public void ShowTranscribing()
    {
        StatusText.Text = "识别中...";
        StatusDot.Visibility = Visibility.Collapsed;
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
        if (!IsVisible) Show();
        PositionNearBottomCenter();
    }

    public void HideOverlay()
    {
        if (IsVisible) Hide();
    }
}
