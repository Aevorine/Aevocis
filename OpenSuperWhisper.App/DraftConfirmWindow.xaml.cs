using System.Windows;
using System.Windows.Input;

namespace OpenSuperWhisper.App;

/// <summary>
/// F11 "show draft first" window: displays the recognized text, editable, and lets the user
/// confirm (Enter) or cancel (Esc) before anything is actually typed into their focused app.
/// Deliberately not modal (Show(), not ShowDialog()) and has no auto-hide timer, unlike
/// RecordingOverlayWindow - unlike that status pill, losing this window's content would lose the
/// user's actual words, so it stays open until they explicitly act on it or close it. It still
/// can't make the app look stuck: it runs fully async (see WaitForResultAsync), so the hotkey
/// listener, tray icon, and every other window keep working normally while this one is up.
/// </summary>
public partial class DraftConfirmWindow : Window
{
    private readonly TaskCompletionSource<string?> _tcs = new();
    private bool _resolved;

    public DraftConfirmWindow(string draftText)
    {
        InitializeComponent();
        DraftTextBox.Text = draftText;
        Loaded += (_, _) =>
        {
            PositionNearBottomCenter();
            DraftTextBox.Focus();
            DraftTextBox.SelectAll();
        };
        // Any other way this window goes away (Alt+F4, taskbar-less close via code, app
        // shutdown) must still resolve the task - otherwise the caller's ConfirmAsync await would
        // hang forever and that dictation would silently vanish with no closure.
        Closed += (_, _) => Resolve(null);
    }

    /// <summary>Completes once the user presses Enter (returns the - possibly edited - text) or
    /// Esc / otherwise closes the window (returns null, meaning "cancelled").</summary>
    public Task<string?> WaitForResultAsync() => _tcs.Task;

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            Resolve(DraftTextBox.Text);
            Close();
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Resolve(null);
            Close();
        }
    }

    private void Resolve(string? result)
    {
        if (_resolved) return;
        _resolved = true;
        _tcs.TrySetResult(result);
    }

    private void PositionNearBottomCenter()
    {
        var workArea = SystemParameters.WorkArea;
        Left = workArea.Left + (workArea.Width - ActualWidth) / 2;
        Top = workArea.Bottom - ActualHeight - 90;
    }
}
