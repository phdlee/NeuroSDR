namespace NeuroSDR.Plugins.Caption;

internal readonly record struct CaptionLanguage(string Code, string Label)
{
    public override string ToString() => Label;
}

/// <summary>Speech/translate picker labels (ISO code + region). API calls still use the ISO code.</summary>
internal static class CaptionLanguages
{
    public static readonly CaptionLanguage[] Speech =
    [
        new("auto", "auto-Detect"),
        new("ko", "ko-Korea"),
        new("en", "en-US"),
        new("ja", "ja-Japan"),
        new("zh", "zh-China"),
        new("de", "de-Germany"),
        new("fr", "fr-France"),
        new("es", "es-Spain"),
        new("pt", "pt-Brazil"),
        new("it", "it-Italy"),
        new("ru", "ru-Russia"),
        new("uk", "uk-Ukraine"),
        new("pl", "pl-Poland"),
        new("nl", "nl-Netherlands"),
        new("sv", "sv-Sweden"),
        new("no", "no-Norway"),
        new("da", "da-Denmark"),
        new("fi", "fi-Finland"),
        new("cs", "cs-Czech"),
        new("sk", "sk-Slovakia"),
        new("hu", "hu-Hungary"),
        new("ro", "ro-Romania"),
        new("bg", "bg-Bulgaria"),
        new("hr", "hr-Croatia"),
        new("sr", "sr-Serbia"),
        new("sl", "sl-Slovenia"),
        new("el", "el-Greece"),
        new("tr", "tr-Turkey"),
        new("ar", "ar-Arabic"),
        new("he", "he-Israel"),
        new("fa", "fa-Iran"),
        new("hi", "hi-India"),
        new("bn", "bn-Bangladesh"),
        new("ur", "ur-Pakistan"),
        new("ta", "ta-Tamil"),
        new("th", "th-Thailand"),
        new("vi", "vi-Vietnam"),
        new("id", "id-Indonesia"),
        new("ms", "ms-Malaysia"),
        new("tl", "tl-Philippines"),
        new("km", "km-Cambodia"),
        new("lo", "lo-Laos"),
        new("my", "my-Myanmar"),
        new("ne", "ne-Nepal"),
        new("si", "si-SriLanka"),
        new("mn", "mn-Mongolia"),
        new("ka", "ka-Georgia"),
        new("hy", "hy-Armenia"),
        new("az", "az-Azerbaijan"),
        new("kk", "kk-Kazakhstan"),
        new("uz", "uz-Uzbekistan"),
        new("lt", "lt-Lithuania"),
        new("lv", "lv-Latvia"),
        new("et", "et-Estonia"),
        new("is", "is-Iceland"),
        new("ga", "ga-Ireland"),
        new("cy", "cy-Wales"),
        new("ca", "ca-Catalan"),
        new("eu", "eu-Basque"),
        new("gl", "gl-Galicia"),
        new("af", "af-Afrikaans"),
        new("sw", "sw-Swahili"),
        new("am", "am-Ethiopia"),
        new("sq", "sq-Albania"),
        new("mk", "mk-Macedonia"),
        new("be", "be-Belarus"),
        new("bs", "bs-Bosnia")
    ];

    public static readonly CaptionLanguage[] Translate = Speech
        .Where(item => !item.Code.Equals("auto", StringComparison.OrdinalIgnoreCase))
        .OrderBy(item => item.Code.Equals("en", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
        .ThenBy(item => item.Label, StringComparer.OrdinalIgnoreCase)
        .ToArray();

    public static string EnglishName(string? code) => GroqWhisperClient.NormalizeLang(code) switch
    {
        "ko" => "Korean",
        "en" => "English",
        "ja" => "Japanese",
        "zh" => "Chinese",
        "de" => "German",
        "fr" => "French",
        "es" => "Spanish",
        "pt" => "Portuguese",
        "it" => "Italian",
        "ru" => "Russian",
        "uk" => "Ukrainian",
        "pl" => "Polish",
        "nl" => "Dutch",
        "sv" => "Swedish",
        "no" => "Norwegian",
        "da" => "Danish",
        "fi" => "Finnish",
        "cs" => "Czech",
        "sk" => "Slovak",
        "hu" => "Hungarian",
        "ro" => "Romanian",
        "bg" => "Bulgarian",
        "hr" => "Croatian",
        "sr" => "Serbian",
        "sl" => "Slovenian",
        "el" => "Greek",
        "tr" => "Turkish",
        "ar" => "Arabic",
        "he" => "Hebrew",
        "fa" => "Persian",
        "hi" => "Hindi",
        "bn" => "Bengali",
        "ur" => "Urdu",
        "ta" => "Tamil",
        "th" => "Thai",
        "vi" => "Vietnamese",
        "id" => "Indonesian",
        "ms" => "Malay",
        "tl" => "Filipino",
        "km" => "Khmer",
        "lo" => "Lao",
        "my" => "Burmese",
        "ne" => "Nepali",
        "si" => "Sinhala",
        "mn" => "Mongolian",
        "ka" => "Georgian",
        "hy" => "Armenian",
        "az" => "Azerbaijani",
        "kk" => "Kazakh",
        "uz" => "Uzbek",
        "lt" => "Lithuanian",
        "lv" => "Latvian",
        "et" => "Estonian",
        "is" => "Icelandic",
        "ga" => "Irish",
        "cy" => "Welsh",
        "ca" => "Catalan",
        "eu" => "Basque",
        "gl" => "Galician",
        "af" => "Afrikaans",
        "sw" => "Swahili",
        "am" => "Amharic",
        "sq" => "Albanian",
        "mk" => "Macedonian",
        "be" => "Belarusian",
        "bs" => "Bosnian",
        var other => other
    };

    public static string Canonical(string? code)
    {
        var value = (code ?? "").Trim().ToLowerInvariant();
        if (value.Length == 0 || value is "auto" or "auto-detect" or "autodetect") return "auto";
        var dash = value.IndexOf('-');
        if (dash > 0) value = value[..dash];
        if (Names.TryGetValue(value, out var mapped)) return mapped;
        if (value.StartsWith("zh", StringComparison.Ordinal)) return "zh";
        if (value.StartsWith("fil", StringComparison.Ordinal) || value.StartsWith("tl", StringComparison.Ordinal))
            return "tl";
        if (value.Length > 3) value = value[..3];
        return Names.TryGetValue(value, out mapped) ? mapped : value;
    }

    public static string GuessFromText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var hangul = 0;
        var kana = 0;
        var cjk = 0;
        var arabic = 0;
        var cyrillic = 0;
        var thai = 0;
        foreach (var ch in text)
        {
            if (ch is >= '\uAC00' and <= '\uD7A3') hangul++;
            else if (ch is (>= '\u3040' and <= '\u30FF') or (>= '\u31F0' and <= '\u31FF')) kana++;
            else if (ch is >= '\u4E00' and <= '\u9FFF') cjk++;
            else if (ch is >= '\u0600' and <= '\u06FF') arabic++;
            else if (ch is >= '\u0400' and <= '\u04FF') cyrillic++;
            else if (ch is >= '\u0E00' and <= '\u0E7F') thai++;
        }
        if (hangul >= 2) return "ko";
        if (kana >= 2) return "ja";
        if (cjk >= 4) return "zh";
        if (arabic >= 2) return "ar";
        if (cyrillic >= 4) return "ru";
        if (thai >= 2) return "th";
        return "";
    }

    public static void Fill(ComboBox box, IReadOnlyList<CaptionLanguage> languages)
    {
        box.Items.Clear();
        box.DropDownWidth = Math.Max(box.DropDownWidth, 168);
        foreach (var language in languages)
            box.Items.Add(language);
    }

    public static string SelectedCode(ComboBox box, string fallback)
    {
        if (box.SelectedItem is CaptionLanguage language)
            return language.Code;
        var text = box.SelectedItem?.ToString() ?? box.Text;
        var code = GroqWhisperClient.NormalizeLang(text);
        return string.IsNullOrWhiteSpace(code) ? fallback : code;
    }

    public static void SelectCode(ComboBox box, string? value, string fallback)
    {
        var wanted = GroqWhisperClient.NormalizeLang(string.IsNullOrWhiteSpace(value) ? fallback : value);
        for (var i = 0; i < box.Items.Count; i++)
        {
            if (box.Items[i] is CaptionLanguage language &&
                language.Code.Equals(wanted, StringComparison.OrdinalIgnoreCase))
            {
                box.SelectedIndex = i;
                return;
            }
        }

        var fallbackCode = GroqWhisperClient.NormalizeLang(fallback);
        for (var i = 0; i < box.Items.Count; i++)
        {
            if (box.Items[i] is CaptionLanguage language &&
                language.Code.Equals(fallbackCode, StringComparison.OrdinalIgnoreCase))
            {
                box.SelectedIndex = i;
                return;
            }
        }

        if (box.Items.Count > 0) box.SelectedIndex = 0;
    }

    private static readonly Dictionary<string, string> Names = new(StringComparer.OrdinalIgnoreCase)
    {
        ["korean"] = "ko", ["kor"] = "ko", ["ko"] = "ko",
        ["english"] = "en", ["eng"] = "en", ["en"] = "en",
        ["japanese"] = "ja", ["jpn"] = "ja", ["ja"] = "ja",
        ["chinese"] = "zh", ["chi"] = "zh", ["zho"] = "zh", ["zh"] = "zh",
        ["german"] = "de", ["ger"] = "de", ["deu"] = "de", ["de"] = "de",
        ["french"] = "fr", ["fre"] = "fr", ["fra"] = "fr", ["fr"] = "fr",
        ["spanish"] = "es", ["spa"] = "es", ["es"] = "es",
        ["portuguese"] = "pt", ["por"] = "pt", ["pt"] = "pt",
        ["italian"] = "it", ["ita"] = "it", ["it"] = "it",
        ["russian"] = "ru", ["rus"] = "ru", ["ru"] = "ru",
        ["ukrainian"] = "uk", ["ukr"] = "uk", ["uk"] = "uk",
        ["arabic"] = "ar", ["ara"] = "ar", ["ar"] = "ar",
        ["hindi"] = "hi", ["hin"] = "hi", ["hi"] = "hi",
        ["thai"] = "th", ["tha"] = "th", ["th"] = "th",
        ["vietnamese"] = "vi", ["vie"] = "vi", ["vi"] = "vi",
        ["indonesian"] = "id", ["ind"] = "id", ["id"] = "id",
        ["dutch"] = "nl", ["nld"] = "nl", ["dut"] = "nl", ["nl"] = "nl",
        ["polish"] = "pl", ["pol"] = "pl", ["pl"] = "pl",
        ["turkish"] = "tr", ["tur"] = "tr", ["tr"] = "tr",
        ["persian"] = "fa", ["fas"] = "fa", ["per"] = "fa", ["fa"] = "fa",
        ["hebrew"] = "he", ["heb"] = "he", ["he"] = "he"
    };
}

internal readonly record struct SttClip(string Text, string Language);
