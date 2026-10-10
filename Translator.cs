using System.Globalization;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace Skein;

/// <summary>
/// Translates post and comment text through a public service: MyMemory (free, no account) or a
/// LibreTranslate server of your choosing. Only used when you turn translation on.
/// </summary>
public sealed class Translator(AppSettings settings)
{
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    /// <summary>MyMemory takes at most 500 bytes per request.</summary>
    const int MaxBytes = 450;

    public string TargetLanguage =>
        settings.TranslateTo.Trim() is { Length: 2 } chosen ? chosen.ToLowerInvariant() : CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;

    /// <summary>The translation, and the language it was detected as (empty if the service didn't say).</summary>
    public async Task<(string Text, string From)> TranslateAsync(string text, CancellationToken ct)
    {
        if (!settings.TranslateEnabled) throw new InvalidOperationException("Translation is off. Turn it on in Preferences.");
        text = text.Trim();
        if (text.Length == 0) return ("", "");
        if (text.Length > 20000) text = text[..20000];
        var target = TargetLanguage;
        if (settings.TranslateServer.Trim().Length > 0) return await LibreAsync(text, target, ct);

        var output = new StringBuilder();
        var from = "";
        foreach (var piece in Pieces(text))
        {
            if (piece.Trim().Length == 0)
            {
                output.Append(piece);
                continue;
            }
            var url = $"https://api.mymemory.translated.net/get?q={Uri.EscapeDataString(piece.Trim())}&langpair=autodetect|{target}";
            using var doc = JsonDocument.Parse(await Http.GetStringAsync(url, ct));
            var data = doc.RootElement.GetProperty("responseData");
            if (doc.RootElement.TryGetProperty("quotaFinished", out var quota) && quota.ValueKind == JsonValueKind.True)
                throw new InvalidOperationException("The free translation service's daily limit is used up. Try again tomorrow.");
            var status = doc.RootElement.TryGetProperty("responseStatus", out var s) && s.ValueKind == JsonValueKind.Number ? s.GetInt32() : 200;
            if (status != 200) throw new InvalidOperationException(data.Str("translatedText") is { Length: > 0 } why ? why : $"Translation failed ({status}).");
            if (from.Length == 0) from = data.Str("detectedLanguage");
            output.Append(System.Net.WebUtility.HtmlDecode(data.Str("translatedText")));
            // Keep the piece's own line breaks.
            output.Append(piece[piece.TrimEnd().Length..]);
        }
        return (output.ToString().Trim(), from);
    }

    async Task<(string, string)> LibreAsync(string text, string target, CancellationToken ct)
    {
        var server = settings.TranslateServer.Trim().TrimEnd('/');
        if (!Uri.TryCreate(server + "/translate", UriKind.Absolute, out var url) || (url.Scheme != "https" && url.Scheme != "http"))
            throw new InvalidOperationException("The translation server in Preferences isn't a web address.");
        var body = JsonSerializer.Serialize(new
        {
            q = text,
            source = "auto",
            target,
            format = "text",
            api_key = settings.TranslateKey.Trim().Length > 0 ? settings.TranslateKey.Trim() : null,
        });
        using var res = await Http.PostAsync(url, new StringContent(body, Encoding.UTF8, "application/json"), ct);
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
        if (!res.IsSuccessStatusCode)
            throw new InvalidOperationException(doc.RootElement.Str("error") is { Length: > 0 } e ? e : $"The translation server said HTTP {(int)res.StatusCode}.");
        var from = doc.RootElement.TryGetProperty("detectedLanguage", out var d) ? d.Str("language") : "";
        return (doc.RootElement.Str("translatedText"), from);
    }

    /// <summary>
    /// Splits text into pieces under the size limit: by paragraph, then sentence, then word.
    /// Each piece keeps the whitespace that followed it, so joining them gives back the text.
    /// </summary>
    public static List<string> Pieces(string text, int maxBytes = MaxBytes)
    {
        var pieces = new List<string>();
        foreach (var paragraph in SplitKeeping(text, "\n"))
        {
            if (Encoding.UTF8.GetByteCount(paragraph) <= maxBytes)
            {
                pieces.Add(paragraph);
                continue;
            }
            var current = "";
            foreach (var sentence in SplitSentences(paragraph))
            {
                var parts = Encoding.UTF8.GetByteCount(sentence) <= maxBytes ? new[] { sentence } : SplitWords(sentence, maxBytes).ToArray();
                foreach (var part in parts)
                {
                    if (current.Length > 0 && Encoding.UTF8.GetByteCount(current + part) > maxBytes)
                    {
                        pieces.Add(current);
                        current = "";
                    }
                    current += part;
                }
            }
            if (current.Length > 0) pieces.Add(current);
        }
        return pieces;
    }

    static IEnumerable<string> SplitKeeping(string text, string separator)
    {
        var start = 0;
        while (start < text.Length)
        {
            var at = text.IndexOf(separator, start, StringComparison.Ordinal);
            if (at < 0)
            {
                yield return text[start..];
                yield break;
            }
            yield return text[start..(at + separator.Length)];
            start = at + separator.Length;
        }
    }

    static IEnumerable<string> SplitSentences(string text) =>
        System.Text.RegularExpressions.Regex.Matches(text, @".+?(?:[.!?。！？]+\s*|$)", System.Text.RegularExpressions.RegexOptions.Singleline).Select(m => m.Value).Where(s => s.Length > 0);

    static IEnumerable<string> SplitWords(string text, int maxBytes)
    {
        var current = "";
        foreach (var word in SplitKeeping(text, " "))
        {
            // A single "word" over the limit (a long URL) is cut by characters.
            var w = word;
            while (Encoding.UTF8.GetByteCount(w) > maxBytes)
            {
                var cut = w.Length / 2;
                while (Encoding.UTF8.GetByteCount(w[..cut]) > maxBytes) cut /= 2;
                if (current.Length > 0) { yield return current; current = ""; }
                yield return w[..cut];
                w = w[cut..];
            }
            if (current.Length > 0 && Encoding.UTF8.GetByteCount(current + w) > maxBytes)
            {
                yield return current;
                current = "";
            }
            current += w;
        }
        if (current.Length > 0) yield return current;
    }
}
