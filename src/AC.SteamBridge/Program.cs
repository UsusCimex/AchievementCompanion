#nullable enable
// AC.SteamBridge - короткоживущий помощник: подключается к Steam-клиенту под AppID игры,
// читает или меняет достижения и сразу выходит. Ответ - одна строка JSON в stdout.
//
//   AC.SteamBridge.exe read   <appid> [--lang russian]
//   AC.SteamBridge.exe unlock <appid> <api_name> [<api_name> ...]
//   AC.SteamBridge.exe lock   <appid> <api_name> [<api_name> ...]
//
// Код возврата: 0 - успех, 1 - ошибка (подробности в поле "error").

using System.Diagnostics;
using System.Text.Json;
using AC.Core.SteamFormats;
using AC.SteamBridge;

var stdout = Console.OpenStandardOutput();
try
{
    var cmd = BridgeCommand.Parse(args);
    var result = BridgeRunner.Run(cmd);
    Write(result);
    return result.Ok ? 0 : 1;
}
catch (Exception e)
{
    Write(BridgeResult.Fail(e.Message));
    return 1;
}

void Write(BridgeResult r)
{
    var json = JsonSerializer.SerializeToUtf8Bytes(r, BridgeJson.Options);
    stdout.Write(json);
    stdout.WriteByte((byte)'\n');
    stdout.Flush();
}

namespace AC.SteamBridge
{
    public enum BridgeAction { Read, Unlock, Lock }

    public sealed record BridgeCommand(BridgeAction Action, uint AppId, IReadOnlyList<string> Names, string Language)
    {
        public static BridgeCommand Parse(string[] args)
        {
            if (args.Length < 2) throw new ArgumentException("использование: read|unlock|lock <appid> [имена...] [--lang язык]");
            var action = args[0].ToLowerInvariant() switch
            {
                "read" => BridgeAction.Read,
                "unlock" => BridgeAction.Unlock,
                "lock" => BridgeAction.Lock,
                _ => throw new ArgumentException($"неизвестная команда {args[0]}"),
            };
            if (!uint.TryParse(args[1], out var appId) || appId == 0) throw new ArgumentException($"плохой appid {args[1]}");
            var names = new List<string>();
            string lang = "";
            for (int i = 2; i < args.Length; i++)
            {
                if (args[i] == "--lang" && i + 1 < args.Length) { lang = args[++i]; continue; }
                names.Add(args[i]);
            }
            if (action != BridgeAction.Read && names.Count == 0) throw new ArgumentException("не указаны достижения");
            return new BridgeCommand(action, appId, names, lang);
        }
    }

    public sealed class BridgeAchievement
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

    public sealed class BridgeChange
    {
        public string ApiName { get; set; } = "";
        public bool Ok { get; set; }
        public string? Error { get; set; }
    }

    public sealed class BridgeResult
    {
        public bool Ok { get; set; }
        public string? Error { get; set; }
        public uint AppId { get; set; }
        public ulong SteamId { get; set; }
        public string? Language { get; set; }
        public List<BridgeAchievement>? Achievements { get; set; }
        public List<BridgeChange>? Changes { get; set; }

        public static BridgeResult Fail(string error) => new() { Ok = false, Error = error };
    }

    public static class BridgeJson
    {
        public static readonly JsonSerializerOptions Options = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        };
    }

    public static class BridgeRunner
    {
        private static readonly TimeSpan StatsTimeout = TimeSpan.FromSeconds(15);

        public static BridgeResult Run(BridgeCommand cmd)
        {
            using var client = new SAM.API.Client();
            try
            {
                client.Initialize(cmd.AppId);
            }
            catch (SAM.API.ClientInitializeException e)
            {
                return BridgeResult.Fail(e.Failure switch
                {
                    SAM.API.ClientInitializeFailure.GetInstallPath => "Steam не установлен (нет пути в реестре)",
                    SAM.API.ClientInitializeFailure.Load => "не удалось загрузить steamclient.dll",
                    SAM.API.ClientInitializeFailure.ConnectToGlobalUser => "Steam не запущен или не выполнен вход",
                    SAM.API.ClientInitializeFailure.AppIdMismatch => "Steam не принял AppID (игры нет в библиотеке?)",
                    _ => e.Message,
                });
            }

            if (client.SteamApps008 != null && !client.SteamApps008.IsSubscribedApp(cmd.AppId))
                return BridgeResult.Fail("игры нет в библиотеке этого аккаунта");

            var steamId = client.SteamUser.GetSteamId();
            if (!WaitForStats(client, steamId, out var statsError))
                return BridgeResult.Fail(statsError);

            var lang = !string.IsNullOrEmpty(cmd.Language) ? cmd.Language : client.SteamApps008?.GetCurrentGameLanguage() ?? "english";
            var schema = LoadSchema(cmd.AppId, lang);
            if (schema is null)
                return BridgeResult.Fail("нет файла схемы appcache/stats/UserGameStatsSchema_" + cmd.AppId + ".bin (запусти игру или SAM хотя бы раз)");

            var result = new BridgeResult { Ok = true, AppId = cmd.AppId, SteamId = steamId, Language = lang };

            if (cmd.Action == BridgeAction.Read)
            {
                result.Achievements = new List<BridgeAchievement>();
                foreach (var a in schema)
                {
                    if (!client.SteamUserStats.GetAchievementAndUnlockTime(a.ApiName, out bool achieved, out uint time))
                        continue;
                    result.Achievements.Add(new BridgeAchievement
                    {
                        ApiName = a.ApiName,
                        Name = a.Name,
                        Description = a.Description,
                        Hidden = a.Hidden,
                        Protected = a.Protected,
                        Icon = StatsSchemaReader.IconUrl(cmd.AppId, a.Icon),
                        IconGray = StatsSchemaReader.IconUrl(cmd.AppId, a.IconGray),
                        Achieved = achieved,
                        UnlockTime = achieved ? time : 0,
                    });
                }
                return result;
            }

            bool target = cmd.Action == BridgeAction.Unlock;
            var byName = schema.ToDictionary(a => a.ApiName, StringComparer.Ordinal);
            result.Changes = new List<BridgeChange>();
            bool anySet = false;
            foreach (var name in cmd.Names.Distinct(StringComparer.Ordinal))
            {
                if (!byName.TryGetValue(name, out var def))
                {
                    result.Changes.Add(new BridgeChange { ApiName = name, Ok = false, Error = "нет в схеме" });
                    continue;
                }
                if (def.Protected)
                {
                    result.Changes.Add(new BridgeChange { ApiName = name, Ok = false, Error = "защищено: ставится только сервером игры" });
                    continue;
                }
                if (!client.SteamUserStats.SetAchievement(name, target))
                {
                    result.Changes.Add(new BridgeChange { ApiName = name, Ok = false, Error = "SetAchievement вернул ошибку" });
                    continue;
                }
                anySet = true;
                result.Changes.Add(new BridgeChange { ApiName = name, Ok = true });
            }

            if (anySet && !client.SteamUserStats.StoreStats())
            {
                foreach (var c in result.Changes.Where(c => c.Ok))
                {
                    c.Ok = false;
                    c.Error = "StoreStats вернул ошибку, изменения не сохранены";
                }
            }

            // Даём клиенту отправить данные, прежде чем процесс закроется.
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < 1000)
            {
                client.RunCallbacks(false);
                Thread.Sleep(50);
            }

            result.Ok = result.Changes.All(c => c.Ok);
            if (!result.Ok) result.Error = "не все изменения применены";
            return result;
        }

        private static bool WaitForStats(SAM.API.Client client, ulong steamId, out string error)
        {
            int? statsResult = null;
            var cb = client.CreateAndRegisterCallback<SAM.API.Callbacks.UserStatsReceived>();
            cb.OnRun += p =>
            {
                if (p.SteamIdUser == steamId) statsResult = p.Result;
            };

            if (client.SteamUserStats.RequestUserStats(steamId) == SAM.API.CallHandle.Invalid)
            {
                error = "RequestUserStats не отправился";
                return false;
            }

            var sw = Stopwatch.StartNew();
            while (statsResult is null && sw.Elapsed < StatsTimeout)
            {
                client.RunCallbacks(false);
                Thread.Sleep(50);
            }

            if (statsResult is null)
            {
                error = "Steam не ответил на запрос статистики";
                return false;
            }
            if (statsResult != 1)
            {
                error = statsResult == 2
                    ? "Steam вернул ошибку 2 (обычно - игры нет в библиотеке)"
                    : $"Steam вернул ошибку {statsResult}";
                return false;
            }
            error = "";
            return true;
        }

        private static IReadOnlyList<SchemaAchievement>? LoadSchema(uint appId, string language)
        {
            var install = SAM.API.Steam.GetInstallPath();
            if (string.IsNullOrEmpty(install)) return null;
            var path = Path.Combine(install, "appcache", "stats", $"UserGameStatsSchema_{appId}.bin");
            var kv = BinaryKeyValue.Load(path);
            return kv is null ? null : StatsSchemaReader.Read(kv, appId, language);
        }
    }
}
