using System.Globalization;

namespace NeuroSDR.Plugins.Caption;

internal static class GroqCaptionQuota
{
    public const int FreeRpm = 20;
    public const int FreeRpd = 2_000;
    public const int FreeAsh = 7_200;
    public const int FreeAsd = 28_800;
    public const int DefaultChunkSeconds = 12;

    public static string Hint(int chunkSeconds, string translateEngine)
    {
        chunkSeconds = Math.Clamp(chunkSeconds, 6, 20);
        var rpm = 60.0 / chunkSeconds;
        var hoursRpd = FreeRpd * chunkSeconds / 3600.0;
        var audioHours = FreeAsd / 3600.0;
        var engine = (translateEngine ?? "off").Trim().ToLowerInvariant();
        var extra = engine switch
        {
            "groq" => " Groq translation is a separate API call from Whisper, so request count is almost doubled.",
            "mymemory" => " Translation uses MyMemory (free), so the Whisper quota is unchanged.",
            _ => " Translate off uses the least Whisper quota."
        };
        return string.Create(CultureInfo.InvariantCulture,
            $"Free Whisper quota ≈ {FreeRpm}/min · {FreeRpd}/day · {audioHours:0} h audio/day. " +
            $"Continuous speech sent every {chunkSeconds}s is about {rpm:0.0} req/min; " +
            $"the daily request cap lasts about {hoursRpd:0.0} hours. " +
            $"A 4-second interval lasts about 2.2 hours. Silence and noise are not sent.{extra} " +
            $"Multiple keys in the same Groq org share this quota.");
    }
}
