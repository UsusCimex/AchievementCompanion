using System.Diagnostics;
using System.Text;
using System.Text.Json;
using AC.Core;

namespace AC.Infrastructure;

// DTOs mirror the AC.SteamBridge output (snake_case). The helper is an x86 exe, so it cannot be referenced directly.
public sealed class BridgeAchievementDto
{
    public string ApiName { get; set; } = "";
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public bool Hidden { get; set; }
    public bool Protected { get; set; }
    public string Icon { get; set; } = "";
    public string IconGray { get; set; } = "";
    public bool Achieved { get; set; }
    public long UnlockTime { get; set; }
}

public sealed class BridgeChangeDto
{
    public string ApiName { get; set; } = "";
    public bool Ok { get; set; }
    public string? Error { get; set; }
}

public sealed class BridgeResultDto
{
    public bool Ok { get; set; }
    public string? Error { get; set; }
    public uint AppId { get; set; }
    public ulong SteamId { get; set; }
    public string? Language { get; set; }
    public List<BridgeAchievementDto>? Achievements { get; set; }
    public List<BridgeChangeDto>? Changes { get; set; }
}

/// <summary>Runs AC.SteamBridge.exe and parses its answer.</summary>
public sealed class BridgeClient
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
    private static readonly SemaphoreSlim OneAtATime = new(1, 1);

    public string ExePath { get; }

    public BridgeClient(string? exePath = null)
    {
        ExePath = exePath ?? FindExe();
    }

    public bool Exists => File.Exists(ExePath);

    public Task<BridgeResultDto> ReadAsync(uint appId, string language, CancellationToken ct = default) =>
        RunAsync(new[] { "read", appId.ToString(), "--lang", language }, ct);

    public Task<BridgeResultDto> UnlockAsync(uint appId, IEnumerable<string> apiNames, CancellationToken ct = default) =>
        RunAsync(new[] { "unlock", appId.ToString() }.Concat(apiNames).ToArray(), ct);

    public Task<BridgeResultDto> LockAsync(uint appId, IEnumerable<string> apiNames, CancellationToken ct = default) =>
        RunAsync(new[] { "lock", appId.ToString() }.Concat(apiNames).ToArray(), ct);

    private async Task<BridgeResultDto> RunAsync(string[] args, CancellationToken ct)
    {
        if (!Exists)
            return new BridgeResultDto { Ok = false, Error = Loc.T("error.bridgeMissing", ExePath) };

        // Steam takes the process for a game: no point in two helpers at once.
        await OneAtATime.WaitAsync(ct);
        try
        {
            var psi = new ProcessStartInfo(ExePath)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                WorkingDirectory = Path.GetDirectoryName(ExePath)!,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);

            using var p = Process.Start(psi) ?? throw new InvalidOperationException(Loc.T("error.bridgeStart"));
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(45));
            var stdoutTask = p.StandardOutput.ReadToEndAsync(timeout.Token);
            var stderrTask = p.StandardError.ReadToEndAsync(timeout.Token);
            try
            {
                await p.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                try { p.Kill(entireProcessTree: true); } catch { }
                return new BridgeResultDto { Ok = false, Error = Loc.T("error.bridgeTimeout") };
            }

            var stdout = (await stdoutTask).Trim();
            var line = stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? "";
            if (line.Length == 0)
            {
                var err = (await stderrTask).Trim();
                return new BridgeResultDto { Ok = false, Error = err.Length > 0 ? Loc.T("error.bridgeEmptyWith", err) : Loc.T("error.bridgeEmpty") };
            }
            return JsonSerializer.Deserialize<BridgeResultDto>(line, Json)
                   ?? new BridgeResultDto { Ok = false, Error = Loc.T("error.bridgeParse") };
        }
        catch (JsonException e)
        {
            return new BridgeResultDto { Ok = false, Error = Loc.T("error.bridgeJson", e.Message) };
        }
        finally
        {
            OneAtATime.Release();
        }
    }

    private static string FindExe()
    {
        var baseDir = AppContext.BaseDirectory;
        foreach (var candidate in new[]
                 {
                     Path.Combine(baseDir, "bridge", "AC.SteamBridge.exe"),
                     Path.Combine(baseDir, "AC.SteamBridge.exe"),
                 })
        {
            if (File.Exists(candidate)) return candidate;
        }
        return Path.Combine(baseDir, "bridge", "AC.SteamBridge.exe");
    }
}
