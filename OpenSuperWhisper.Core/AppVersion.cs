namespace OpenSuperWhisper.Core;

/// <summary>
/// The app's own version number. Update-checking itself is handled by Velopack's own
/// UpdateManager (see App.xaml.cs) - this constant is just for display purposes (crash reports,
/// diagnostics), so it doesn't need to match Velopack's packVersion exactly, but should be kept
/// in sync at each release for anyone reading a crash report to make sense of it.
/// </summary>
public static class AppVersion
{
    public const string Current = "1.2.0";
}
