using NeuroSDR.Core;
using NeuroSDR.Dsp;
using NeuroSDR.Recording;
using System.Text;

namespace NeuroSDR.Diagnostics;

/// <summary>
/// Offline DCS analysis of an NeuroSDR IQ WAV using the same NFM demod + DCS
/// path as the UI (no live PTT / no separate probe hardware path).
/// </summary>
internal static class DcsIqOffline
{
    public static int Run(string path, int bandwidthHz = 12_500, long? tuneOverrideHz = null, int? expectCode = null)
    {
        if (!File.Exists(path))
        {
            Console.WriteLine("File not found: " + path);
            return 1;
        }

        if (!TryReadMeta(path, out var sampleRate, out var centerHz, out var tuneHz, out var dataOffset, out var dataBytes, out var error))
        {
            Console.WriteLine(error);
            return 2;
        }

        if (tuneOverrideHz is { } over) tuneHz = over;
        else if (tuneHz <= 0) tuneHz = centerHz > 0 ? centerHz : GuessTuneHz(path);
        expectCode ??= GuessExpectCode(path);
        var offsetHz = tuneHz - centerHz;

        Console.WriteLine($"IQ: {path}");
        Console.WriteLine($"SR={sampleRate}  center={centerHz / 1e6:0.000000} MHz  tune={tuneHz / 1e6:0.000000} MHz  offset={offsetHz:+#;-#;0} Hz");
        Console.WriteLine($"Demod: NFM BW={bandwidthHz}  expect DCS {(expectCode >= 0 ? expectCode.Value.ToString("D3") : "?")}");
        Console.WriteLine();

        var demod = new AudioDemodulator
        {
            Mode = RadioMode.NFM,
            Bandwidth = bandwidthHz,
            FrequencyOffset = offsetHz
        };
        var dcs = new DcsToneDecoder { DeepSearch = true };
        var ctcss = new CtcssToneDecoder();

        var report = new StringBuilder();
        void Log(string line)
        {
            Console.WriteLine(line);
            report.AppendLine(line);
        }

        long iqSamples = 0;
        long pcmSamples = 0;
        double afEnergy = 0;
        long afWindow = 0;
        var lastLogPcm = 0L;
        var logEvery = AudioDemodulator.AudioSampleRate; // ~1 s of AF
        string? lastLabel = null;
        var lockCounts = new Dictionary<string, int>(StringComparer.Ordinal);

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 20, FileOptions.SequentialScan);
        using var reader = new BinaryReader(stream);
        stream.Position = dataOffset;
        var remaining = dataBytes;
        var block = new Complex32[16_384];

        while (remaining >= 4)
        {
            var count = (int)Math.Min(block.Length, remaining / 4);
            for (var i = 0; i < count; i++)
                block[i] = new Complex32(reader.ReadInt16() / 32768f, reader.ReadInt16() / 32768f);
            remaining -= count * 4L;
            iqSamples += count;

            Complex32[] chunk;
            if (count == block.Length) chunk = block;
            else
            {
                chunk = new Complex32[count];
                Array.Copy(block, chunk, count);
            }
            var audio = demod.Process(chunk, sampleRate);
            if (audio.Length == 0) continue;
            pcmSamples += audio.Length;
            foreach (var v in audio) afEnergy += v * v;
            afWindow += audio.Length;

            // Same order as frmNeuroSDR.ProcessDemodulatedAudio
            dcs.Process(audio, AudioDemodulator.AudioSampleRate);
            if (dcs.HasActivity) ctcss.Reset();
            else ctcss.Process(audio, AudioDemodulator.AudioSampleRate);

            if (pcmSamples - lastLogPcm < logEvery) continue;
            lastLogPcm = pcmSamples;
            var afRms = afWindow > 0 ? Math.Sqrt(afEnergy / afWindow) : 0;
            afEnergy = 0;
            afWindow = 0;
            var t = pcmSamples / (double)AudioDemodulator.AudioSampleRate;
            var label = dcs.DetectedLabel;
            if (label.Length == 0 && dcs.HasActivity) label = $"… d{dcs.DebugBestDist}";
            if (label.Length == 0)
            {
                var tone = ctcss.DetectedToneHz;
                label = tone > 0 ? $"CTCSS {tone:0.0}" : "—";
            }
            else if (dcs.DetectedLabel.Length > 0)
            {
                lockCounts.TryGetValue(dcs.DetectedLabel, out var n);
                lockCounts[dcs.DetectedLabel] = n + 1;
            }

            if (label != lastLabel || pcmSamples % (logEvery * 5) < logEvery)
            {
                Log($"{t:0.0}s  {label,-12}  dist={dcs.DebugBestDist} votes={dcs.DebugVotes} raw=0x{dcs.DebugTopPattern:X6} afRms={afRms:0.0000}");
                lastLabel = label;
            }
        }

        Log("");
        Log($"DONE iq={iqSamples:N0} pcm={pcmSamples:N0} final='{dcs.DetectedLabel}' dist={dcs.DebugBestDist}");
        if (lockCounts.Count > 0)
        {
            Log("Lock histogram (seconds with label):");
            foreach (var kv in lockCounts.OrderByDescending(k => k.Value))
                Log($"  {kv.Key}: {kv.Value}s");
        }

        var outPath = Path.Combine(Path.GetDirectoryName(path) ?? ".", Path.GetFileNameWithoutExtension(path) + ".dcs.txt");
        try { File.WriteAllText(outPath, report.ToString()); Log("Report: " + outPath); } catch { }

        if (expectCode is >= 0)
        {
            var got = dcs.DetectedCode;
            var ok = got == expectCode || (InversePair.TryGetValue(expectCode.Value, out var pair) && got == pair);
            return ok ? 0 : 3;
        }
        return dcs.DetectedCode >= 0 ? 0 : 3;
    }

    private static long GuessTuneHz(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        // 448_dcs23N → 448.800 MHz (same convention as user filenames)
        if (name.Length >= 3 && char.IsDigit(name[0]) && char.IsDigit(name[1]) && char.IsDigit(name[2]))
        {
            var mhz = int.Parse(name.AsSpan(0, 3));
            return mhz * 1_000_000L + 800_000L;
        }
        return 448_800_000;
    }

    private static int? GuessExpectCode(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path).ToLowerInvariant();
        if (name.Contains("dcs23")) return 023;
        if (name.Contains("dcs32")) return 032;
        return null;
    }

    private static readonly Dictionary<int, int> InversePair = BuildInversePairs();

    private static Dictionary<int, int> BuildInversePairs()
    {
        int[][] pairs =
        [
            [023, 047], [025, 244], [026, 464], [031, 627], [032, 051], [036, 172],
            [043, 445], [047, 023], [051, 032], [053, 452], [054, 413], [065, 271],
            [466, 662], [662, 466]
        ];
        var map = new Dictionary<int, int>();
        foreach (var p in pairs) map[p[0]] = p[1];
        return map;
    }

    private static bool TryReadMeta(string path, out int sampleRate, out long centerHz, out long tuneHz, out long dataOffset, out long dataBytes, out string error)
    {
        sampleRate = 0;
        centerHz = 0;
        tuneHz = 0;
        dataOffset = 0;
        dataBytes = 0;
        error = "";
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new BinaryReader(stream, Encoding.ASCII, leaveOpen: true);
            if (Encoding.ASCII.GetString(reader.ReadBytes(4)) != "RIFF") { error = "Not RIFF"; return false; }
            reader.ReadUInt32();
            if (Encoding.ASCII.GetString(reader.ReadBytes(4)) != "WAVE") { error = "Not WAVE"; return false; }
            while (stream.Position + 8 <= stream.Length)
            {
                var id = Encoding.ASCII.GetString(reader.ReadBytes(4));
                var length = reader.ReadUInt32();
                var next = Math.Min(stream.Length, stream.Position + length + (length & 1));
                if (id == "fmt ")
                {
                    reader.ReadUInt16();
                    reader.ReadUInt16();
                    sampleRate = checked((int)reader.ReadUInt32());
                }
                else if (id == "enrf" && length >= 8)
                {
                    centerHz = reader.ReadInt64();
                    tuneHz = length >= 16 ? reader.ReadInt64() : centerHz;
                }
                else if (id == "data")
                {
                    dataOffset = stream.Position;
                    dataBytes = Math.Min(length, stream.Length - dataOffset);
                }
                stream.Position = next;
            }
            if (sampleRate <= 0 || dataBytes < 4) { error = "No IQ data"; return false; }
            if (tuneHz <= 0) tuneHz = centerHz;
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }
}
