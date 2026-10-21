using System.Net;
using System.Text;
using System.Text.Json;

namespace AC.Infrastructure;

/// <summary>
/// Machine translation through Google Translate's web endpoint, without a key, as many desktop tools do.
/// It is not an official API and may stop answering: then the translator's page opens in the browser instead.
/// </summary>
public sealed class Translator(HttpClient http)
{
    /// <summary>The text in the target language (ISO code: en, ru...); null - it already is in that language.</summary>
    public async Task<string?> TranslateAsync(string text, string target, CancellationToken ct = default)
    {
        var url = $"https://translate.googleapis.com/translate_a/single?client=gtx&sl=auto&tl={Uri.EscapeDataString(target)}&dt=t";
        for (int attempt = 1; ; attempt++)
        {
            using var content = new FormUrlEncodedContent([new KeyValuePair<string, string>("q", text)]);
            using var resp = await http.PostAsync(url, content, ct);
            // The first request of a process often gets 429, the next one goes through.
            if (resp.StatusCode == HttpStatusCode.TooManyRequests && attempt == 1)
            {
                await Task.Delay(1000, ct);
                continue;
            }
            resp.EnsureSuccessStatusCode();
            return Parse(await resp.Content.ReadAsStringAsync(ct), target);
        }
    }

    /// <summary>The page that translates the text in the browser.</summary>
    public static string WebUrl(string text, string target) =>
        $"https://translate.google.com/?sl=auto&tl={Uri.EscapeDataString(target)}&text={Uri.EscapeDataString(text)}&op=translate";

    /// <summary>The answer is an array: the translated sentences first, the detected language third.</summary>
    public static string? Parse(string json, string target)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() == 0 || root[0].ValueKind != JsonValueKind.Array)
            throw new JsonException("no sentences");
        if (root.GetArrayLength() > 2 && root[2].ValueKind == JsonValueKind.String &&
            string.Equals(root[2].GetString(), target, StringComparison.OrdinalIgnoreCase))
            return null;
        var sb = new StringBuilder();
        foreach (var sentence in root[0].EnumerateArray())
            if (sentence.ValueKind == JsonValueKind.Array && sentence.GetArrayLength() > 0 && sentence[0].ValueKind == JsonValueKind.String)
                sb.Append(sentence[0].GetString());
        return sb.ToString();
    }
}
