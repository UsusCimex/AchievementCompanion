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
    /// <summary>Set by a game server (permission 1 or 2); Steam refuses it from the client.</summary>
    public bool Protected => (Permission & 3) != 0;
}

/// <summary>
/// Achievements from Steam's local schema, appcache/stats/UserGameStatsSchema_&lt;appid&gt;.bin. Every achievement is
/// a bit of a stat: &lt;appid&gt;/stats/&lt;stat&gt;/bits/&lt;n&gt; with its API name, permission and display texts.
/// </summary>
public static class StatsSchemaReader
{
    public static IReadOnlyList<SchemaAchievement> Read(KvNode root, uint appId, string language) =>
        root[appId.ToString(CultureInfo.InvariantCulture)]["stats"].Children
            .SelectMany(stat => stat["bits"].Children)
            .Where(bit => bit["name"].Text().Length > 0)
            .Select(bit => Achievement(bit, language))
            .ToList();

    private static SchemaAchievement Achievement(KvNode bit, string language)
    {
        var apiName = bit["name"].Text();
        var display = bit["display"];
        return new SchemaAchievement
        {
            ApiName = apiName,
            Name = DisplayText(display["name"], language) ?? apiName,
            Description = DisplayText(display["desc"], language) ?? "",
            Hidden = display["hidden"].Flag,
            Icon = display["icon"].Text(),
            IconGray = display["icon_gray"].Text(),
            Permission = (int)bit["permission"].Number(),
        };
    }

    /// <summary>A text keyed by Steam language names, or a plain string: the chosen language, else English.</summary>
    private static string? DisplayText(KvNode node, string language)
    {
        foreach (var text in (KvNode[])[node[language], node["english"], node])
            if (text.Text() is { Length: > 0 } s) return s;
        return null;
    }

    /// <summary>Icon URL from the file name in the schema.</summary>
    public static string IconUrl(uint appId, string file) =>
        string.IsNullOrEmpty(file) ? "" :
        file.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? file :
        $"https://cdn.steamstatic.com/steamcommunity/public/images/apps/{appId}/{file}";
}
