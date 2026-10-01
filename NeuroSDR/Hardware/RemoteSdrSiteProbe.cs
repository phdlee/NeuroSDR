using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using WebSdr.Protocol;
using WebSdr.Protocol.Kiwi;
using WebSdr.Protocol.OpenWebRx;

namespace NeuroSDR.Hardware;

internal sealed class RemoteSdrProbeResult
{
    public bool Reachable { get; init; }
    public string Detail { get; init; } = "";
    public IReadOnlyList<RemoteSdrBandSpan>? Bands { get; init; }
    public double? Latitude { get; init; }
    public double? Longitude { get; init; }
    public double? AltitudeMeters { get; init; }
    public string? Location { get; init; }
    public int ReceiveScore { get; init; }
}

internal static class RemoteSdrSiteProbe
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(8) };

    public static async Task<RemoteSdrProbeResult> CheckAsync(RemoteSdrEntry entry, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(entry.Url, UriKind.Absolute, out var uri))
            return Fail("Invalid URL");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(25));
        var token = timeout.Token;
        try
        {
            return entry.Protocol switch
            {
                "KiwiSDR" => await CheckKiwiAsync(uri, token).ConfigureAwait(false),
                "OpenWebRX" => await CheckOpenWebRxAsync(uri, token).ConfigureAwait(false),
                _ => await CheckWebSdrAsync(uri, token).ConfigureAwait(false)
            };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Fail("Timed out");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            return Fail(exception.GetBaseException().Message);
        }
    }

    private static async Task<RemoteSdrProbeResult> CheckWebSdrAsync(Uri uri, CancellationToken token)
    {
        IReadOnlyList<RemoteSdrBandSpan>? bands = null;
        try
        {
            var info = await BandInfoParser.LoadAsync(uri, Http, token).ConfigureAwait(false);
            bands = ToSpans(info);
        }
        catch
        {
            // Audio can still prove the site is up when bandinfo.js is missing.
        }

        var sound = new SoundStreamClient(uri);
        var started = Environment.TickCount64;
        var pace = new AudioPace();
        var audio = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        sound.Decoder.SamplesAvailable += samples =>
        {
            pace.Add(samples.Length);
            audio.TrySetResult();
        };
        try
        {
            await sound.ConnectAsync(token).ConfigureAwait(false);
            var settings = new ReceiverSettings { Name = "NeuroSDR", FrequencyKhz = TuneKhz(bands, 7_074) };
            await sound.SendParamsAsync(settings, token).ConfigureAwait(false);
            var heard = await WaitAudioAsync(audio, sound.Decoder.SamplesDecoded > 0, token).ConfigureAwait(false);
            if (!heard) return Fail("No audio");
            await Task.Delay(TimeSpan.FromMilliseconds(1500), token).ConfigureAwait(false);
            var score = ScorePace(pace, started);
            return Ok($"Audio {score}", bands, null, null, null, null, score);
        }
        finally
        {
            await sound.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static async Task<RemoteSdrProbeResult> CheckKiwiAsync(Uri uri, CancellationToken token)
    {
        var place = await TryReadPlaceAsync(new Uri(uri, "status"), token).ConfigureAwait(false);
        var sound = new KiwiSoundClient { UserName = "NeuroSDR", UseCompression = true };
        var started = Environment.TickCount64;
        var pace = new AudioPace();
        var audio = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var failed = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        sound.SamplesAvailable += samples =>
        {
            pace.Add(samples.Length);
            audio.TrySetResult();
        };
        sound.ErrorOccurred += exception => failed.TrySetResult(exception.GetBaseException().Message);
        try
        {
            var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            sound.Ready += () => ready.TrySetResult();
            await sound.ConnectAsync(uri, token).ConfigureAwait(false);
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(12), token).ConfigureAwait(false);
            var tune = TuneKhz(null, 14_100);
            if (sound.BandwidthKhz > 0)
                tune = Math.Clamp(tune, sound.FreqOffsetKhz + 10, sound.FreqOffsetKhz + sound.BandwidthKhz - 10);
            await sound.SendInitialRxAsync(new ReceiverSettings { Name = "NeuroSDR", FrequencyKhz = tune }, token)
                .ConfigureAwait(false);
            var heard = await WaitAudioAsync(audio, sound.SamplesDecoded > 0, token, failed).ConfigureAwait(false);
            if (!heard)
                return Fail(failed.Task.IsCompleted ? failed.Task.Result : "No audio");
            await Task.Delay(TimeSpan.FromMilliseconds(1500), token).ConfigureAwait(false);
            var score = ScorePace(pace, started);
            IReadOnlyList<RemoteSdrBandSpan>? bands = null;
            if (sound.BandwidthKhz > 1)
            {
                var low = sound.FreqOffsetKhz / 1_000d;
                var high = (sound.FreqOffsetKhz + sound.BandwidthKhz) / 1_000d;
                if (high > low) bands = [new RemoteSdrBandSpan(low, high)];
            }
            return Ok($"Audio {score}", bands, place.Latitude, place.Longitude, place.AltitudeMeters, place.Location, score);
        }
        finally
        {
            await sound.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static async Task<RemoteSdrProbeResult> CheckOpenWebRxAsync(Uri uri, CancellationToken token)
    {
        var place = await TryReadPlaceAsync(new Uri(uri, "status"), token).ConfigureAwait(false);
        var client = new OpenWebRxClient { ClientName = "NeuroSDR" };
        var started = Environment.TickCount64;
        var pace = new AudioPace();
        var audio = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.SamplesAvailable += (samples, _) =>
        {
            pace.Add(samples.Length);
            audio.TrySetResult();
        };
        try
        {
            var settings = new ReceiverSettings { Name = "NeuroSDR", FrequencyKhz = 7_074 };
            ReceiverSettings.ApplyOpenWebRxHash(uri, settings);
            await client.ConnectAsync(uri, token).ConfigureAwait(false);
            await client.StartAndTuneAsync(settings, token).ConfigureAwait(false);
            var heard = await WaitAudioAsync(audio, client.SamplesDecoded > 0, token).ConfigureAwait(false);
            if (!heard) return Fail("No audio");
            await Task.Delay(TimeSpan.FromMilliseconds(1500), token).ConfigureAwait(false);
            var score = ScorePace(pace, started);
            IReadOnlyList<RemoteSdrBandSpan>? bands = null;
            if (client.SampleRateHz > 1_000)
            {
                var center = client.CenterFreqHz > 0 ? client.CenterFreqHz : client.StartFreqHz;
                var low = (center - client.SampleRateHz / 2) / 1_000_000d;
                var high = (center + client.SampleRateHz / 2) / 1_000_000d;
                if (high > low && high > 0) bands = [new RemoteSdrBandSpan(Math.Max(0, low), high)];
            }
            return Ok($"Audio {score}", bands, place.Latitude, place.Longitude, place.AltitudeMeters, place.Location, score);
        }
        finally
        {
            await client.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static int ScorePace(AudioPace pace, long startedTick)
    {
        if (pace.Bursts == 0 || pace.Samples <= 0) return 0;
        var wait = Math.Max(0, pace.FirstTick - startedTick);
        var window = Math.Max(400, pace.LastTick - pace.FirstTick);
        var rate = pace.Samples * 1000d / window;
        var ratePoints = (int)Math.Clamp(rate / 12_000d * 50d, 0, 50);
        var speedPoints = wait < 800 ? 30 : wait < 2_500 ? 18 : wait < 5_000 ? 8 : 0;
        var steadyPoints = Math.Max(0, 20 - pace.Gaps * 8);
        return Math.Clamp(ratePoints + speedPoints + steadyPoints, 1, 100);
    }

    private sealed class AudioPace
    {
        public int Samples;
        public int Bursts;
        public int Gaps;
        public long FirstTick;
        public long LastTick;

        public void Add(int count)
        {
            if (count <= 0) return;
            var now = Environment.TickCount64;
            if (Bursts == 0) FirstTick = now;
            else if (now - LastTick > 350) Gaps++;
            LastTick = now;
            Bursts++;
            Samples += count;
        }
    }

    private static async Task<bool> WaitAudioAsync(TaskCompletionSource audio, bool already,
        CancellationToken token, TaskCompletionSource<string>? failed = null)
    {
        if (already) return true;
        var wait = Task.Delay(TimeSpan.FromSeconds(8), token);
        var done = failed is null ? await Task.WhenAny(audio.Task, wait).ConfigureAwait(false)
            : await Task.WhenAny(audio.Task, failed.Task, wait).ConfigureAwait(false);
        return done == audio.Task;
    }

    private static async Task<Place> TryReadPlaceAsync(Uri statusUrl, CancellationToken token)
    {
        try
        {
            using var response = await Http.GetAsync(statusUrl, token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return default;
            var body = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
            return ParsePlace(body);
        }
        catch
        {
            return default;
        }
    }

    private static Place ParsePlace(string body)
    {
        double? lat = null, lon = null, alt = null;
        string? location = null;
        var gps = Regex.Match(body, @"(-?\d{1,3}\.\d+)\s*,\s*(-?\d{1,3}\.\d+)");
        if (gps.Success &&
            double.TryParse(gps.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var gLat) &&
            double.TryParse(gps.Groups[2].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var gLon))
        {
            lat = gLat;
            lon = gLon;
        }
        try
        {
            using var document = JsonDocument.Parse(body);
            Walk(document.RootElement, 0, ref lat, ref lon, ref alt, ref location);
        }
        catch
        {
            // Status pages are not always JSON.
        }
        if (lat is null || lon is null || Math.Abs(lat.Value) < 0.01 && Math.Abs(lon.Value) < 0.01)
            return new Place(null, null, alt, location);
        if (lat is < -90 or > 90 || lon is < -180 or > 180)
            return new Place(null, null, alt, location);
        return new Place(lat, lon, alt, location);
    }

    private static void Walk(JsonElement element, int depth, ref double? lat, ref double? lon, ref double? alt, ref string? location)
    {
        if (depth > 3) return;
        if (element.ValueKind != JsonValueKind.Object) return;
        foreach (var property in element.EnumerateObject())
        {
            var name = property.Name;
            if (property.Value.ValueKind == JsonValueKind.Object)
            {
                Walk(property.Value, depth + 1, ref lat, ref lon, ref alt, ref location);
                continue;
            }
            if (property.Value.ValueKind == JsonValueKind.String &&
                (name.Equals("loc", StringComparison.OrdinalIgnoreCase) ||
                 name.Equals("location", StringComparison.OrdinalIgnoreCase) ||
                 name.Equals("qth", StringComparison.OrdinalIgnoreCase)))
            {
                var text = property.Value.GetString();
                if (!string.IsNullOrWhiteSpace(text)) location = text.Trim();
            }
            if (property.Value.ValueKind is not (JsonValueKind.Number or JsonValueKind.String)) continue;
            if (!TryNumber(property.Value, out var number)) continue;
            if (name.Equals("lat", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("latitude", StringComparison.OrdinalIgnoreCase))
                lat = number;
            else if (name.Equals("lon", StringComparison.OrdinalIgnoreCase) ||
                     name.Equals("lng", StringComparison.OrdinalIgnoreCase) ||
                     name.Equals("longitude", StringComparison.OrdinalIgnoreCase))
                lon = number;
            else if (name.Equals("asl", StringComparison.OrdinalIgnoreCase) ||
                     name.Equals("alt", StringComparison.OrdinalIgnoreCase) ||
                     name.Equals("altitude", StringComparison.OrdinalIgnoreCase))
                alt = number;
        }
    }

    private static bool TryNumber(JsonElement element, out double number)
    {
        if (element.ValueKind == JsonValueKind.Number) return element.TryGetDouble(out number);
        return double.TryParse(element.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out number);
    }

    private static IReadOnlyList<RemoteSdrBandSpan> ToSpans(BandInfoDocument info)
    {
        var rows = new List<RemoteSdrBandSpan>();
        foreach (var band in info.FrequencyBands)
        {
            var low = band.Min / 1_000d;
            var high = band.Max / 1_000d;
            if (high > low && high > 0) rows.Add(new RemoteSdrBandSpan(low, high));
        }
        if (rows.Count > 0) return rows;
        foreach (var band in info.Bands)
        {
            var low = band.MinFreqKhz / 1_000d;
            var high = band.MaxFreqKhz / 1_000d;
            if (high > low && high > 0) rows.Add(new RemoteSdrBandSpan(low, high));
        }
        return rows;
    }

    private static double TuneKhz(IReadOnlyList<RemoteSdrBandSpan>? bands, double fallback)
    {
        if (bands is not { Count: > 0 }) return fallback;
        var mid = (bands[0].LowMhz + bands[0].HighMhz) / 2d * 1_000d;
        return mid > 0 ? mid : fallback;
    }

    private static RemoteSdrProbeResult Ok(string detail, IReadOnlyList<RemoteSdrBandSpan>? bands,
        double? lat, double? lon, double? alt, string? location, int receiveScore) => new()
    {
        Reachable = true,
        Detail = detail,
        Bands = bands is { Count: > 0 } ? bands : null,
        Latitude = lat,
        Longitude = lon,
        AltitudeMeters = alt,
        Location = location,
        ReceiveScore = receiveScore
    };

    private static RemoteSdrProbeResult Fail(string detail) => new() { Reachable = false, Detail = detail };

    private readonly record struct Place(double? Latitude, double? Longitude, double? AltitudeMeters, string? Location);
}
