using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using AC.Core;

namespace AC.Infrastructure;

public sealed record UpdateInfo(Version Version, string PageUrl, string AssetUrl, long Size, string? Sha256);

/// <summary>
/// Updates from GitHub releases: the files of the latest release's win-x64 zip replace those next to the exe. A running exe
/// cannot be overwritten but can be renamed, so the old files are moved aside as *.old and removed on the next start.
/// AC_UPDATE_URL points the check at another "latest release" JSON (a local test server).
/// </summary>
public sealed class UpdateService(HttpClient http)
{
    public const string Repository = "UsusCimex/AchievementCompanion";
    private const string OldSuffix = ".old";
    private const string AssetSuffix = "-win-x64.zip";

    public static Version Current { get; } = Trim(typeof(UpdateService).Assembly.GetName().Version);

    /// <summary>A published single-file exe; a build from source runs from its dll and is not replaced.</summary>
    public static bool CanInstall => string.IsNullOrEmpty(typeof(UpdateService).Assembly.Location);

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
            var digest = asset.TryGetProperty("digest", out var d) ? d.GetString() : null;
            return new UpdateInfo(
                Trim(version),
                root.TryGetProperty("html_url", out var page) ? page.GetString() ?? "" : "",
                asset.GetProperty("browser_download_url").GetString() ?? "",
                asset.TryGetProperty("size", out var size) ? size.GetInt64() : 0,
                digest is not null && digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) ? digest[7..] : null);
        }
        return null;
    }

    /// <summary>Downloads and checks the zip, then puts its files next to the running exe.</summary>
    public async Task InstallAsync(UpdateInfo update, CancellationToken ct = default)
    {
        var exe = Environment.ProcessPath ?? throw new InvalidOperationException("no process path");
        var work = Path.Combine(Path.GetTempPath(), "AchievementCompanion-update");
        if (Directory.Exists(work)) Directory.Delete(work, recursive: true);
        Directory.CreateDirectory(work);
        try
        {
            var zip = Path.Combine(work, "update.zip");
            using (var response = await http.GetAsync(update.AssetUrl, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                response.EnsureSuccessStatusCode();
                await using var file = File.Create(zip);
                await response.Content.CopyToAsync(file, ct);
            }
            Apply(zip, update, Path.GetDirectoryName(exe)!, Path.GetFileName(exe));
        }
        finally
        {
            try { Directory.Delete(work, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>
    /// Checks the zip against the release and replaces the files in <paramref name="dir"/>; on any failure
    /// the files already moved aside are put back.
    /// </summary>
    public static void Apply(string zip, UpdateInfo update, string dir, string exeName)
    {
        if (update.Size > 0 && new FileInfo(zip).Length != update.Size) throw new InvalidDataException(Loc.T("update.damaged"));
        if (update.Sha256 is { } expected)
        {
            using var stream = File.OpenRead(zip);
            if (!Convert.ToHexString(SHA256.HashData(stream)).Equals(expected, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException(Loc.T("update.damaged"));
        }

        var unpacked = Path.Combine(Path.GetDirectoryName(zip)!, "files");
        ZipFile.ExtractToDirectory(zip, unpacked, overwriteFiles: true);
        if (!File.Exists(Path.Combine(unpacked, exeName))) throw new InvalidDataException(Loc.T("update.noExe", exeName));

        var moved = new List<(string Target, string? Backup)>();
        try
        {
            foreach (var source in Directory.GetFiles(unpacked))
            {
                var target = Path.Combine(dir, Path.GetFileName(source));
                string? backup = null;
                if (File.Exists(target))
                {
                    backup = target + OldSuffix;
                    File.Move(target, backup, overwrite: true);
                }
                moved.Add((target, backup));
                File.Move(source, target);
            }
        }
        catch
        {
            foreach (var (target, backup) in Enumerable.Reverse(moved))
            {
                try
                {
                    if (backup is null) File.Delete(target);
                    else File.Move(backup, target, overwrite: true);
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            throw;
        }
    }

    /// <summary>Starts the new exe, which waits for this process to exit (one instance per data folder).</summary>
    public static void Restart()
    {
        var psi = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false };
        psi.ArgumentList.Add(WaitFlag);
        psi.ArgumentList.Add(Environment.ProcessId.ToString());
        Process.Start(psi);
    }

    public const string WaitFlag = "--wait-pid";

    /// <summary>Removes the files the last update moved aside.</summary>
    public static void CleanUp(string dir)
    {
        foreach (var old in Directory.EnumerateFiles(dir, "*" + OldSuffix))
        {
            try { File.Delete(old); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static Version Trim(Version? v) => v is null ? new Version(0, 0, 0) : new Version(v.Major, v.Minor, Math.Max(0, v.Build));
}
