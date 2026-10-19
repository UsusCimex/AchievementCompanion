using System.ComponentModel;
using System.Globalization;
using System.Text.Json;

namespace AC.Core;

/// <summary>
/// Тексты интерфейса: Lang/&lt;язык&gt;.json, вшитые в сборку. Ключа нет в текущем языке - берётся из основного,
/// нет и там - показывается сам ключ. Фраза с числом - объект с формами по правилам языка (one, few, many, other).
/// Язык меняется на лету: привязки к индексатору и подписчики Changed обновляются сами.
/// </summary>
public sealed class Loc : INotifyPropertyChanged
{
    public const string Fallback = "ru";
    public static IReadOnlyList<string> Languages { get; } = ["ru"];
    public static Loc Instance { get; } = new();

    private static readonly Dictionary<string, string> _fallback = Load(Fallback);
    private static Dictionary<string, string> _strings = _fallback;

    public static string Language { get; private set; } = Fallback;
    public static event Action? Changed;
    public event PropertyChangedEventHandler? PropertyChanged;

    private Loc() { }

    /// <summary>Для привязок WPF: {Binding [ключ], Source={x:Static core:Loc.Instance}}.</summary>
    public string this[string key] => T(key);

    /// <summary>Пусто или неизвестный язык - язык Windows, если на него есть перевод, иначе основной.</summary>
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
        _strings = lang == Fallback ? _fallback : Load(lang);
        Language = lang;
        Instance.PropertyChanged?.Invoke(Instance, new PropertyChangedEventArgs("Item[]"));
        Changed?.Invoke();
    }

    public static string T(string key) =>
        _strings.TryGetValue(key, out var s) || _fallback.TryGetValue(key, out s) ? s : key;

    public static string T(string key, params object?[] args) => string.Format(CultureInfo.CurrentCulture, T(key), args);

    /// <summary>Фраза с числом: форма по правилам языка, {0} - само число, дальше - остальные аргументы.</summary>
    public static string Plural(string key, long n, params object?[] args)
    {
        var form = Form(_strings, key, Category(Language, n)) ?? Form(_fallback, key, Category(Fallback, n)) ?? key;
        return string.Format(CultureInfo.CurrentCulture, form, [n, .. args]);
    }

    /// <summary>Форма числа по правилам CLDR для целых: в русском one, few, many, в остальных one и other.</summary>
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

    /// <summary>Все ключи языка; у фраз с числом - по ключу на форму: "ключ#форма".</summary>
    public static IReadOnlyCollection<string> Keys(string lang) => [.. Load(lang).Keys];

    private static string? Form(Dictionary<string, string> strings, string key, string category) =>
        strings.TryGetValue(key + "#" + category, out var s) || strings.TryGetValue(key + "#other", out s) ? s : null;

    private static Dictionary<string, string> Load(string lang)
    {
        using var stream = typeof(Loc).Assembly.GetManifestResourceStream($"AC.Core.Lang.{lang}.json")
                           ?? throw new InvalidOperationException($"Нет файла перевода {lang}");
        using var doc = JsonDocument.Parse(stream);
        var strings = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var p in doc.RootElement.EnumerateObject())
        {
            if (p.Value.ValueKind == JsonValueKind.Object)
                foreach (var form in p.Value.EnumerateObject()) strings[p.Name + "#" + form.Name] = form.Value.GetString() ?? "";
            else
                strings[p.Name] = p.Value.GetString() ?? "";
        }
        return strings;
    }
}
