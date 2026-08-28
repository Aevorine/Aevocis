using System.Windows.Threading;
using OpenSuperWhisper.Core;

namespace OpenSuperWhisper.App;

/// <summary>
/// WPF-backed implementation of IDraftConfirmation (F11): shows a DraftConfirmWindow on the UI
/// thread and resolves once the user acts on it. Lives in the App project (not Core) because it
/// needs WPF/Dispatcher - DictationController only ever sees the IDraftConfirmation abstraction,
/// so it stays framework-agnostic and headlessly testable.
/// </summary>
public sealed class DraftConfirmationService : IDraftConfirmation
{
    private readonly Dispatcher _dispatcher;

    public DraftConfirmationService(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
    }

    public async Task<string?> ConfirmAsync(string text)
    {
        DraftConfirmWindow window = null!;
        // Creating/showing the window must happen on the UI thread; InvokeAsync (not Invoke) so
        // this never blocks whatever thread called ConfirmAsync from.
        await _dispatcher.InvokeAsync(() =>
        {
            window = new DraftConfirmWindow(text);
            window.Show();
        });
        return await window.WaitForResultAsync();
    }
}
