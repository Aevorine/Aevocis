using System.Reflection;

namespace OpenSuperWhisper.Core;

/// <summary>
/// The app's own version number, for display purposes (crash reports, diagnostics). Previously a
/// hand-maintained string constant that fell out of sync across four releases in a row (stayed
/// "1.3.0" through v1.3.1-v1.3.4) - every crash report during that window showed the wrong
/// version, which is actively misleading when diagnosing which build a report came from. Reading
/// it from the entry assembly's own version metadata (set once, in the csproj's &lt;Version&gt;)
/// makes this a single source of truth instead of two numbers someone has to remember to keep
/// matched by hand.
/// </summary>
public static class AppVersion
{
    public static string Current { get; } =
        Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "0.0.0";
}
