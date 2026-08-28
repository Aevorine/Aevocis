namespace OpenSuperWhisper.Core;

/// <summary>
/// Case-insensitive lookup for the "process name -> per-app override" maps in AppSettings
/// (AppSpecificPrompts for F06, AppSpecificHotkeys for F12). Matching is done here at lookup
/// time - rather than by giving the dictionary a StringComparer.OrdinalIgnoreCase comparer -
/// because System.Text.Json deserializes a Dictionary&lt;TKey,TValue&gt; property into a brand
/// new dictionary with the default (case-sensitive, ordinal) comparer on every settings.json
/// load, silently dropping whatever comparer the in-memory default was constructed with. Process
/// names (Process.ProcessName) also aren't guaranteed to match the exact casing a user typed
/// into Settings, so case-insensitive matching is the correct behavior anyway, not just a
/// workaround.
/// </summary>
public static class AppSpecificLookup
{
    public static bool TryGet<T>(IReadOnlyDictionary<string, T>? map, string? processName, out T value)
    {
        value = default!;
        if (map is null || map.Count == 0 || string.IsNullOrEmpty(processName)) return false;

        foreach (var (key, v) in map)
        {
            if (string.Equals(key, processName, StringComparison.OrdinalIgnoreCase))
            {
                value = v;
                return true;
            }
        }
        return false;
    }
}
