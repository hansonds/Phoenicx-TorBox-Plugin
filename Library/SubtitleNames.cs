using System.Text.RegularExpressions;

namespace PhoenicxTorBox.Library;

/// <summary>Reads the language and kind (forced / SDH) from a subtitle file name.</summary>
public static class SubtitleNames
{
    private static readonly Dictionary<string, string> Languages = new(StringComparer.OrdinalIgnoreCase)
    {
        ["en"] = "en", ["eng"] = "en", ["english"] = "en",
        ["ta"] = "ta", ["tam"] = "ta", ["tamil"] = "ta",
        ["hi"] = "hi", ["hin"] = "hi", ["hindi"] = "hi",
        ["ml"] = "ml", ["mal"] = "ml", ["malayalam"] = "ml",
        ["te"] = "te", ["tel"] = "te", ["telugu"] = "te",
        ["kn"] = "kn", ["kan"] = "kn", ["kannada"] = "kn",
        ["bn"] = "bn", ["ben"] = "bn", ["bengali"] = "bn",
        ["mr"] = "mr", ["mar"] = "mr", ["marathi"] = "mr",
        ["ur"] = "ur", ["urd"] = "ur", ["urdu"] = "ur",
        ["fr"] = "fr", ["fre"] = "fr", ["fra"] = "fr", ["french"] = "fr",
        ["de"] = "de", ["ger"] = "de", ["deu"] = "de", ["german"] = "de",
        ["es"] = "es", ["spa"] = "es", ["spanish"] = "es",
        ["it"] = "it", ["ita"] = "it", ["italian"] = "it",
        ["pt"] = "pt", ["por"] = "pt", ["portuguese"] = "pt",
        ["ru"] = "ru", ["rus"] = "ru", ["russian"] = "ru",
        ["ar"] = "ar", ["ara"] = "ar", ["arabic"] = "ar",
        ["zh"] = "zh", ["chi"] = "zh", ["zho"] = "zh", ["chinese"] = "zh", ["chs"] = "zh", ["cht"] = "zh",
        ["ja"] = "ja", ["jpn"] = "ja", ["japanese"] = "ja",
        ["ko"] = "ko", ["kor"] = "ko", ["korean"] = "ko",
        ["nl"] = "nl", ["dut"] = "nl", ["nld"] = "nl", ["dutch"] = "nl",
        ["sv"] = "sv", ["swe"] = "sv", ["swedish"] = "sv",
        ["tr"] = "tr", ["tur"] = "tr", ["turkish"] = "tr",
        ["pl"] = "pl", ["pol"] = "pl", ["polish"] = "pl",
        ["id"] = "id", ["ind"] = "id", ["indonesian"] = "id",
        ["ms"] = "ms", ["may"] = "ms", ["malay"] = "ms",
        ["th"] = "th", ["tha"] = "th", ["thai"] = "th",
        ["vi"] = "vi", ["vie"] = "vi", ["vietnamese"] = "vi",
    };

    private static readonly Regex Split = new(@"[ ._\-\[\]()]+", RegexOptions.Compiled);

    /// <summary>Language code and kind (forced / sdh), each null when not found.</summary>
    /// <param name="fileName">The subtitle's file name.</param>
    /// <param name="videoStem">The video's file name without extension, when the subtitle starts with it.</param>
    public static (string? Language, string? Kind) Read(string fileName, string? videoStem)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName);

        // "Movie.2023.1080p.en.srt" next to "Movie.2023.1080p.mkv": only ".en" says the language.
        var onlyRest = false;
        if (!string.IsNullOrEmpty(videoStem) && stem.StartsWith(videoStem, StringComparison.OrdinalIgnoreCase))
        {
            stem = stem[videoStem.Length..];
            onlyRest = true;
        }

        var tokens = Split.Split(stem).Where(t => t.Length > 0).ToList();

        // RARBG style "2_English": the number is just an index.
        tokens = tokens.Select(t => Regex.Replace(t, @"^\d+(?=[A-Za-z])", string.Empty)).ToList();

        string? lang = null;
        string? kind = null;

        // Look at the last few words, where releases put the language.
        for (var i = tokens.Count - 1; i >= Math.Max(0, tokens.Count - 4); i--)
        {
            var t = tokens[i];
            if (kind is null && t.Equals("forced", StringComparison.OrdinalIgnoreCase))
            {
                kind = "forced";
            }
            else if (kind is null && (t.Equals("sdh", StringComparison.OrdinalIgnoreCase) || t.Equals("cc", StringComparison.OrdinalIgnoreCase)))
            {
                kind = "sdh";
            }
            else if (lang is null && Languages.TryGetValue(t, out var code)
                     && (onlyRest || t.Length > 2 || tokens.Count == 1))
            {
                // Two-letter codes ("it", "hi", "id") are also normal words in titles, so in a
                // full release name only longer forms count.
                lang = code;
            }
        }

        // A file named only by its language ("English.srt") is covered above; a long
        // release name with a language word in the middle ("...Tamil.WEB-DL...") is not a
        // subtitle language, so the words further back are not read.
        return (lang, kind);
    }
}
