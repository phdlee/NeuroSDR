using System.Text;
using System.Text.RegularExpressions;

namespace NeuroSDR.Plugins.Caption;

internal static class CaptionText
{
    private static readonly string[] StockPhrases =
    [
        "Thanks for watching",
        "Thank you for watching",
        "Thanks for watching!",
        "Please subscribe",
        "Subscribe",
        "ご視聴ありがとうございました",
        "チャンネル登録よろしくお願いします"
    ];

    public static string FormatDisplay(string original, string? translation, string mode)
    {
        original = SanitizeStt(original);
        translation = SanitizeStt(translation);
        mode = (mode ?? "original").Trim().ToLowerInvariant();
        if (mode is "translation" or "translated" && translation.Length > 0)
            return translation;
        if (mode is "both" or "original+translation" && translation.Length > 0 &&
            !translation.Equals(original, StringComparison.OrdinalIgnoreCase))
            return $"{original}  /  {translation}";
        return original;
    }

    /// <summary>Drops known caption hallucination loops.</summary>
    public static string SanitizeStt(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var s = text.Trim();
        if (IsStockHallucination(s) || IsDegenerateLoop(s)) return "";
        s = CollapseIdenticalCharRuns(s);
        s = CollapseRepeatedTokens(s);
        s = CollapseRepeatedPhrases(s);
        s = Regex.Replace(s, @"\s{2,}", " ").Trim(' ', ',', '/', '·');
        if (s.Length == 0 || IsStockHallucination(s) || IsDegenerateLoop(s)) return "";
        return s;
    }

    public static bool SameCaption(string a, string b)
        => string.Equals(NormalizeCaption(a), NormalizeCaption(b), StringComparison.OrdinalIgnoreCase);

    public static string NormalizeCaption(string text)
        => Regex.Replace((text ?? "").Trim(), @"\s+", " ");

    public static void AppendToLog(StringBuilder log, string text, bool showTime, bool autoWrap, DateTime utc)
    {
        text = SanitizeStt(text);
        if (text.Length == 0) return;
        if (showTime)
        {
            if (log.Length > 0 && log[^1] != '\n') log.AppendLine();
            log.Append(utc.ToLocalTime().ToString("HH:mm:ss"));
            log.Append("  ");
            log.Append(text);
            log.AppendLine();
            return;
        }

        if (log.Length > 0 && log[^1] != '\n' && !char.IsWhiteSpace(log[^1]))
            log.Append(' ');
        log.Append(text);
        if (!autoWrap) return;
        BreakSentences(log);
    }

    internal static void BreakSentences(StringBuilder log)
    {
        var text = log.ToString();
        var rebuilt = new StringBuilder(text.Length + 8);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            rebuilt.Append(c);
            if (c is not ('.' or '。' or '!' or '?' or '！' or '？')) continue;
            var next = i + 1 < text.Length ? text[i + 1] : '\0';
            if (next is '\r' or '\n' or '\0') continue;
            if (char.IsDigit(next) || char.IsLower(next)) continue;
            if (!char.IsWhiteSpace(next)) rebuilt.Append(' ');
            while (i + 1 < text.Length && char.IsWhiteSpace(text[i + 1])) i++;
            rebuilt.AppendLine();
        }
        log.Clear();
        log.Append(rebuilt);
    }

    private static bool IsStockHallucination(string s)
    {
        var n = s.Trim().Trim('.', '!', '。', '！');
        if (n.Length == 0) return true;
        if (StockPhrases.Any(p => n.Equals(p, StringComparison.OrdinalIgnoreCase))) return true;
        if (n.StartsWith("Thanks for watching", StringComparison.OrdinalIgnoreCase)) return true;
        if (n.Contains("ご視聴ありがとうございました", StringComparison.Ordinal)) return true;
        return false;
    }

    private static string CollapseIdenticalCharRuns(string s)
    {
        var sb = new StringBuilder(s.Length);
        var run = 0;
        var prev = '\0';
        foreach (var ch in s)
        {
            if (ch == prev && !char.IsWhiteSpace(ch) && ch is not '.' and not ',' and not '!' and not '?')
            {
                run++;
                if (run <= 3) sb.Append(ch);
                continue;
            }
            prev = ch;
            run = 1;
            sb.Append(ch);
        }
        return sb.ToString();
    }

    private static string CollapseRepeatedTokens(string s)
    {
        var tokens = Regex.Split(s, @"[\s,./·]+")
            .Where(p => p.Length > 0)
            .ToArray();
        if (tokens.Length < 6) return s;
        var top = tokens.GroupBy(t => t, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count())
            .First();
        if (top.Count() < 8 || top.Count() * 2 < tokens.Length) return s;
        return top.Key;
    }

    private static string CollapseRepeatedPhrases(string s)
    {
        var n = NormalizeCaption(s);
        for (var len = Math.Min(40, n.Length / 4); len >= 4; len--)
        {
            var unit = n[..len].Trim();
            if (unit.Length < 4) continue;
            var repeats = 0;
            var i = 0;
            while (i + unit.Length <= n.Length &&
                   n.AsSpan(i, unit.Length).Equals(unit, StringComparison.OrdinalIgnoreCase))
            {
                repeats++;
                i += unit.Length;
                while (i < n.Length && n[i] is ' ' or ',' or '/') i++;
            }
            if (repeats >= 6 && i >= n.Length - 2) return unit;
        }
        return s;
    }

    private static bool IsDegenerateLoop(string s)
    {
        var compact = Regex.Replace(s, @"[\s,./]+", "");
        if (compact.Length >= 16)
        {
            var unique = compact.Distinct().Count();
            if (unique <= 3) return true;
        }
        var words = Regex.Split(s, @"[\s,./]+").Where(w => w.Length > 0).ToArray();
        if (words.Length >= 12)
        {
            var top = words.GroupBy(w => w, StringComparer.OrdinalIgnoreCase).Max(g => g.Count());
            if (top >= words.Length - 1) return true;
        }
        return false;
    }
}
