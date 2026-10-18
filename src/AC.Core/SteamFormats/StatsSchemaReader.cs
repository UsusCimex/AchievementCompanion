// Чтение UserGameStatsSchema_<appid>.bin. Логика разбора повторяет
// SAM.Game/Manager.cs (LoadUserGameStatsSchema) из Steam Achievement Manager,
// (c) 2024 Rick (gibbed), zlib license. Изменённая версия.

using System.Globalization;

namespace AC.Core.SteamFormats;

public sealed class SchemaAchievement
{
    public string ApiName { get; init; } = "";
    public string Name { get; init; } = "";
    public string Description { get; init; } = "";
    public bool Hidden { get; init; }
    public string Icon { get; init; } = "";
    public string IconGray { get; init; } = "";
    public int Permission { get; init; }
    /// <summary>Ставится только доверенным сервером, клиентом не выставить.</summary>
    public bool Protected => (Permission & 3) != 0;
}

public static class StatsSchemaReader
{
    // Типы из SAM.API.Types.UserStatType
    private const int TypeAchievements = 4;
    private const int TypeGroupAchievements = 5;

    public static IReadOnlyList<SchemaAchievement> Read(KvNode root, uint appId, string language)
    {
        var result = new List<SchemaAchievement>();
        var stats = root[appId.ToString(CultureInfo.InvariantCulture)]["stats"];
        if (!stats.Valid || stats.Children is null) return result;

        foreach (var stat in stats.Children)
        {
            if (!stat.Valid) continue;
            int type = StatType(stat);
            if (type != TypeAchievements && type != TypeGroupAchievements) continue;
            if (stat.Children is null) continue;

            foreach (var bits in stat.Children.Where(b => string.Equals(b.Name, "bits", StringComparison.OrdinalIgnoreCase)))
            {
                if (bits.Children is null) continue;
                foreach (var bit in bits.Children)
                {
                    var id = bit["name"].AsString();
                    if (string.IsNullOrEmpty(id)) continue;
                    var display = bit["display"];
                    result.Add(new SchemaAchievement
                    {
                        ApiName = id,
                        Name = Localized(display["name"], language, id),
                        Description = Localized(display["desc"], language, ""),
                        Hidden = display["hidden"].AsBool(),
                        Icon = display["icon"].AsString(),
                        IconGray = display["icon_gray"].AsString(),
                        Permission = bit["permission"].AsInt(),
                    });
                }
            }
        }
        return result;
    }

    private static int StatType(KvNode stat)
    {
        var typeNode = stat["type"];
        if (typeNode.Valid && typeNode.Type == KvType.String)
        {
            var s = typeNode.AsString();
            if (int.TryParse(s, out var n)) return n;
            return s.ToLowerInvariant() switch
            {
                "int" or "integer" => 1,
                "float" => 2,
                "avgrate" or "averagerate" => 3,
                "achievements" or "bits" => TypeAchievements,
                "groupachievements" => TypeGroupAchievements,
                _ => 0,
            };
        }
        var typeInt = stat["type_int"];
        return typeInt.Valid ? typeInt.AsInt() : typeNode.AsInt();
    }

    private static string Localized(KvNode node, string language, string fallback)
    {
        var v = node[language].AsString();
        if (!string.IsNullOrEmpty(v)) return v;
        if (language != "english")
        {
            v = node["english"].AsString();
            if (!string.IsNullOrEmpty(v)) return v;
        }
        v = node.AsString();
        return string.IsNullOrEmpty(v) ? fallback : v;
    }

    /// <summary>Ссылка на иконку из имени файла в схеме.</summary>
    public static string IconUrl(uint appId, string file) =>
        string.IsNullOrEmpty(file) ? "" :
        file.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? file :
        $"https://cdn.steamstatic.com/steamcommunity/public/images/apps/{appId}/{file}";
}
