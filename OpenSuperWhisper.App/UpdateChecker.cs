using System.Net.Http;
using System.Text.Json;
using OpenSuperWhisper.Core;

namespace OpenSuperWhisper.App;

/// <summary>
/// Result of a successful update check: whether the latest published GitHub release is newer
/// than this app's own version, plus the version string and release page URL to show/open.
/// </summary>
internal sealed record UpdateCheckResult(bool IsNewer, string LatestVersion, string ReleaseHtmlUrl);

/// <summary>
/// Best-effort, fire-and-forget check against the public GitHub "latest release" API. Deliberately
/// does NOT download or install anything - it only tells the caller whether a newer version
/// exists and where its release page is, so the app can point the user at a one-click download
/// page rather than silently replacing itself. Any failure (no internet, GitHub down, rate
/// limited, malformed JSON, etc.) is swallowed and logged via Log.Info, never surfaced to the
/// user - this app is designed to work fully offline, so a failed update check is unremarkable.
/// </summary>
internal static class UpdateChecker
{
    private const string ReleasesApiUrl = "https://api.github.com/repos/Aevorine/OpenSuperWhisper_Windows/releases/latest";
    private const string WindowsTagSuffix = "-windows";

    public static async Task<UpdateCheckResult?> CheckAsync()
    {
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("OpenSuperWhisper-Windows");

            using var response = await client.GetAsync(ReleasesApiUrl);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var tagName = root.GetProperty("tag_name").GetString();
            var htmlUrl = root.GetProperty("html_url").GetString();
            if (string.IsNullOrWhiteSpace(tagName) || string.IsNullOrWhiteSpace(htmlUrl))
            {
                Log.Info($"更新检查：GitHub 返回的数据缺少 tag_name 或 html_url");
                return null;
            }

            var versionText = tagName.StartsWith('v') || tagName.StartsWith('V')
                ? tagName[1..]
                : tagName;
            if (versionText.EndsWith(WindowsTagSuffix, StringComparison.OrdinalIgnoreCase))
                versionText = versionText[..^WindowsTagSuffix.Length];

            if (!Version.TryParse(versionText, out var latestVersion))
            {
                Log.Info($"更新检查：无法解析版本号 '{tagName}'");
                return null;
            }
            if (!Version.TryParse(AppVersion.Current, out var currentVersion))
            {
                Log.Info($"更新检查：无法解析当前版本号 '{AppVersion.Current}'");
                return null;
            }

            return new UpdateCheckResult(latestVersion > currentVersion, versionText, htmlUrl);
        }
        catch (Exception ex)
        {
            // Not an application error - an update check failing (no network, GitHub
            // unreachable, rate limited, etc.) is expected and unremarkable.
            Log.Info($"更新检查失败：{ex.Message}");
            return null;
        }
    }
}
