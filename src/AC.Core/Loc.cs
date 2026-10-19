using System.ComponentModel;
using System.Globalization;
using System.Text.Json;

namespace AC.Core;

/// <summary>
/// Interface texts from Lang/&lt;language&gt;.json embedded in the assembly. A key missing in the current language
/// falls back to English, then to the key itself. A phrase with a number is an object with plural forms
/// (one, few, many, other). The language switches at runtime: indexer bindings and Changed subscribers update.
/// </summary>
public sealed class Loc : INotifyPropertyChanged
{
    public const string Fallback = "en";
    private const string Prefix = "AC.Core.Lang.";

    private static readonly Dictionary<string, Dictionary<string, string>> Cache = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, string> _fallback = Load(Fallback);
    private static Dictionary<string, string> _strings = _fallback;

    /// <summary>Every language with a file in Lang/, English first.</summary>
    public static IReadOnlyList<string> Languages { get; } =
    [
        .. typeof(Loc).Assembly.GetManifestResourceNames()
            .Where(n => n.StartsWith(Prefix, StringComparison.Ordinal) && n.EndsWith(".json", StringComparison.Ordinal))
            .Select(n => n[Prefix.Length..^".json".Length])
            .OrderBy(l => l != Fallback).ThenBy(l => l, StringComparer.Ordinal),
    ];

    public static Loc Instance { get; } = new();
    public static string Language { get; private set; } = Fallback;
    public static event Action? Changed;
    public event PropertyChangedEventHandler? PropertyChanged;

    private Loc() { }

    /// <summary>For WPF bindings: {Binding [key], Source={x:Static core:Loc.Instance}}.</summary>
    public string this[string key] => T(key);

    /// <summary>A supported language as is; empty or unknown means the Windows language if translated, else English.</summary>
    public static string Resolve(string? code)
    {
        if (!string.IsNullOrEmpty(code) && Languages.Contains(code)) return code;
        var system = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
        return Languages.Contains(system) ? system : Fallback;
    }

    public static void SetLanguage(string? code)
    {
        var lang = Resolve(code);
        if (lang == Language) return;
        _strings = Load(lang);
        Language = lang;
        Instance.PropertyChanged?.Invoke(Instance, new PropertyChangedEventArgs("Item[]"));
        Changed?.Invoke();
    }

    public static string T(string key) =>
        _strings.TryGetValue(key, out var s) || _fallback.TryGetValue(key, out s) ? s : key;

    public static string T(string key, params object?[] args) => string.Format(CultureInfo.CurrentCulture, T(key), args);

    /// <summary>A phrase with a number: the form follows the language's rules, {0} is the number, then the other arguments.</summary>
    public static string Plural(string key, long n, params object?[] args)
    {
        var form = Form(_strings, key, Category(Language, n)) ?? Form(_fallback, key, Category(Fallback, n)) ?? key;
        return string.Format(CultureInfo.CurrentCulture, form, [n, .. args]);
    }

    /// <summary>CLDR plural category for integers: Russian has one, few and many; the others here have one and other.</summary>
    public static string Category(string lang, long n)
    {
        n = Math.Abs(n);
        return lang switch
        {
            "ru" => n % 10 == 1 && n % 100 != 11 ? "one"
                : n % 10 is >= 2 and <= 4 && n % 100 is not (>= 12 and <= 14) ? "few"
                : "many",
            _ => n == 1 ? "one" : "other",
        };
    }

    /// <summary>The language's name in itself, for the language picker.</summary>
    public static string NativeName(string lang) => Load(lang).GetValueOrDefault("language.name", lang);

    /// <summary>Steam's name for the language (english, russian...), used for achievement names and descriptions.</summary>
    public static string SteamLanguage(string lang) => Load(lang).GetValueOrDefault("language.steam", "english");

    /// <summary>All texts of a language; a phrase with a number has a key per form: "key#form".</summary>
    internal static IReadOnlyDictionary<string, string> Strings(string lang) => Load(lang);

    private static string? Form(Dictionary<string, string> strings, string key, string category) =>
        strings.TryGetValue(key + "#" + category, out var s) || strings.TryGetValue(key + "#other", out s) ? s : null;

    private static Dictionary<string, string> Load(string lang)
    {
        lock (Cache)
        {
            if (Cache.TryGetValue(lang, out var cached)) return cached;
            using var stream = typeof(Loc).Assembly.GetManifestResourceStream(Prefix + lang + ".json")
                               ?? throw new InvalidOperationException($"No translation file for {lang}");
            using var doc = JsonDocument.Parse(stream);
            var strings = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var p in doc.RootElement.EnumerateObject())
            {
                if (p.Value.ValueKind == JsonValueKind.Object)
                    foreach (var form in p.Value.EnumerateObject()) strings[p.Name + "#" + form.Name] = form.Value.GetString() ?? "";
                else
                    strings[p.Name] = p.Value.GetString() ?? "";
            }
            return Cache[lang] = strings;
        }
    }
}
