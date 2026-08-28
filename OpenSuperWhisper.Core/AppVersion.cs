namespace OpenSuperWhisper.Core;

/// <summary>
/// The app's own version number, compared against GitHub releases by the update checker.
/// Plain string, parsed via <see cref="Version"/> - deliberately not a semver library, since
/// this app only needs "is the published tag newer than me" and nothing fancier.
/// </summary>
public static class AppVersion
{
    public const string Current = "1.0.0";
}
