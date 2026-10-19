using System.Text.Json;

namespace AC.Infrastructure;

public sealed record UpdateInfo(Version Version, string PageUrl, string DownloadUrl);

/// <summary>
/// Finds a newer release on GitHub. The zip is downloaded by the browser: an unsigned exe that downloads and swaps
/// in its own replacement looks like a dropper to behavior-based antivirus. AC_UPDATE_URL points the check at
/// another "latest release" JSON (a local test server).
/// </summary>
public sealed class UpdateService(HttpClient http)
{
    public const string Repository = "UsusCimex/AchievementCompanion";
    private const string AssetSuffix = "-win-x64.zip";

    public static Version Current { get; } = Trim(typeof(UpdateService).Assembly.GetName().Version);

    private static string LatestUrl =>
        Environment.GetEnvironmentVariable("AC_UPDATE_URL") is { Length: > 0 } url ? url
            : $"https://api.github.com/repos/{Repository}/releases/latest";

    /// <summary>A newer release with a Windows zip, or null (none, offline, or the repository is private).</summary>
    public async Task<UpdateInfo?> CheckAsync(CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, LatestUrl);
        request.Headers.UserAgent.ParseAdd($"AchievementCompanion/{Current}");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var response = await http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode) return null;
        return Parse(await response.Content.ReadAsStringAsync(ct), Current);
    }

    /// <summary>The release from GitHub's "latest release" JSON if it is newer than <paramref name="current"/>.</summary>
    public static UpdateInfo? Parse(string json, Version current)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.TryGetProperty("draft", out var draft) && draft.GetBoolean()) return null;
        if (root.TryGetProperty("prerelease", out var pre) && pre.GetBoolean()) return null;
        var tag = root.TryGetProperty("tag_name", out var t) ? t.GetString() ?? "" : "";
        if (!Version.TryParse(tag.TrimStart('v', 'V'), out var version) || Trim(version) <= current) return null;
        if (!root.TryGetProperty("assets", out var assets)) return null;

        foreach (var asset in assets.EnumerateArray())
        {
            var name = asset.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
            if (!name.EndsWith(AssetSuffix, StringComparison.OrdinalIgnoreCase)) continue;
            return new UpdateInfo(
                Trim(version),
                root.TryGetProperty("html_url", out var page) ? page.GetString() ?? "" : "",
                asset.GetProperty("browser_download_url").GetString() ?? "");
        }
        return null;
    }

    private static Version Trim(Version? v) => v is null ? new Version(0, 0, 0) : new Version(v.Major, v.Minor, Math.Max(0, v.Build));
}
