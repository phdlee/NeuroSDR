using Microsoft.AspNetCore.SignalR;
using System.Net;

namespace NeuroSDR.Web;

public sealed class RemoteWebHostOptions
{
    public bool Enabled { get; set; } = true;
    public int Port { get; set; } = 8765;
    /// <summary>When false, bind loopback only. When true, listen on all interfaces.</summary>
    public bool BindAllInterfaces { get; set; }
    /// <summary>Optional shared secret; empty disables token check.</summary>
    public string AccessToken { get; set; } = "";
}

public sealed class RemoteWebHost : IAsyncDisposable
{
    private readonly INeuroSDRRemoteRadio _radio;
    private readonly RemoteWebHostOptions _options;
    private readonly object _lifetime = new();
    private WebApplication? _app;
    private CancellationTokenSource? _cts;
    private Action<RadioRemoteSnapshot>? _onState;
    private Action<RadioLiveUpdate>? _onLive;
    private Action<SpectrumRemoteFrame>? _onSpectrum;
    private Action<SpectrumRemoteFrame>? _onAfSpectrum;
    private Action<byte[]>? _onAudio;
    private Action<AfPluginRemoteEvent>? _onAf;
    private int _stopped;

    public Uri? BaseAddress { get; private set; }
    public string Status { get; private set; } = "stopped";

    public RemoteWebHost(INeuroSDRRemoteRadio radio, RemoteWebHostOptions options)
    {
        _radio = radio;
        _options = options;
    }

    public async Task StartAsync()
    {
        if (!_options.Enabled)
        {
            Status = "disabled";
            return;
        }

        await StopAsync().ConfigureAwait(false);
        _cts = new CancellationTokenSource();
        Volatile.Write(ref _stopped, 0);

        var contentRoot = Path.GetDirectoryName(typeof(RemoteWebHost).Assembly.Location)
            ?? AppContext.BaseDirectory;
        var webRoot = Path.Combine(contentRoot, "wwwroot");
        if (!Directory.Exists(webRoot))
            webRoot = Path.Combine(AppContext.BaseDirectory, "wwwroot");

        var host = _options.BindAllInterfaces ? "0.0.0.0" : "127.0.0.1";
        var url = $"http://{host}:{_options.Port}";

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ContentRootPath = contentRoot,
            WebRootPath = webRoot
        });
        builder.WebHost.UseUrls(url);
        builder.Logging.ClearProviders();
        builder.Logging.AddDebug();
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.Services.Configure<HostOptions>(options =>
        {
            options.ShutdownTimeout = TimeSpan.FromSeconds(1);
        });

        builder.Services.AddSingleton(_radio);
        builder.Services.AddSingleton(_options);
        builder.Services.AddSignalR(options =>
        {
            options.MaximumReceiveMessageSize = 256 * 1024;
            options.EnableDetailedErrors = true;
        });
        builder.Services.AddCors(options =>
        {
            options.AddDefaultPolicy(policy => policy
                .AllowAnyHeader()
                .AllowAnyMethod()
                .AllowCredentials()
                .SetIsOriginAllowed(_ => true));
        });

        _app = builder.Build();
        _app.UseCors();
        if (!string.IsNullOrWhiteSpace(_options.AccessToken))
            _app.Use(TokenGate);
        _app.UseDefaultFiles();
        _app.UseStaticFiles();
        _app.MapHub<RadioHub>("/hubs/radio");
        _app.MapGet("/api/state", (INeuroSDRRemoteRadio radio) => Results.Json(radio.GetSnapshot()));
        _app.MapGet("/api/sources", (INeuroSDRRemoteRadio radio) => Results.Json(radio.GetSources()));
        _app.MapGet("/api/modes", (INeuroSDRRemoteRadio radio) => Results.Json(radio.GetModes()));
        _app.MapGet("/api/health", () => Results.Ok(new { ok = true, app = "NeuroSDR Remote" }));
        _app.MapFallbackToFile("index.html");

        var hub = _app.Services.GetRequiredService<IHubContext<RadioHub>>();
        _onState = snapshot =>
        {
            if (Volatile.Read(ref _stopped) != 0) return;
            _ = hub.Clients.All.SendAsync("state", snapshot);
        };
        _onLive = live =>
        {
            if (Volatile.Read(ref _stopped) != 0) return;
            _ = hub.Clients.All.SendAsync("live", live);
        };
        _onSpectrum = frame =>
        {
            if (Volatile.Read(ref _stopped) != 0) return;
            _ = hub.Clients.All.SendAsync("spectrum", frame);
        };
        _onAfSpectrum = frame =>
        {
            if (Volatile.Read(ref _stopped) != 0) return;
            _ = hub.Clients.All.SendAsync("afSpectrum", frame);
        };
        _onAudio = pcm =>
        {
            if (Volatile.Read(ref _stopped) != 0) return;
            _ = hub.Clients.All.SendAsync("audio", new
            {
                sampleRate = 48_000,
                pcm16Base64 = Convert.ToBase64String(pcm)
            });
        };
        _onAf = evt =>
        {
            if (Volatile.Read(ref _stopped) != 0) return;
            _ = hub.Clients.All.SendAsync("af", evt);
        };
        _radio.StateChanged += _onState;
        _radio.LiveChanged += _onLive;
        _radio.SpectrumAvailable += _onSpectrum;
        _radio.AfSpectrumAvailable += _onAfSpectrum;
        _radio.AudioAvailable += _onAudio;
        _radio.AfPluginEvent += _onAf;

        await _app.StartAsync(_cts.Token).ConfigureAwait(false);
        BaseAddress = new Uri(_options.BindAllInterfaces
            ? $"http://{GetLanHint()}:{_options.Port}/"
            : $"http://127.0.0.1:{_options.Port}/");
        Status = $"listening · {BaseAddress}";
    }

    public async Task StopAsync()
    {
        if (Interlocked.Exchange(ref _stopped, 1) == 1 && _app is null) return;

        DetachRadioEvents();
        try { _cts?.Cancel(); } catch { }

        WebApplication? app;
        lock (_lifetime)
        {
            app = _app;
            _app = null;
        }

        if (app is not null)
        {
            try { await app.StopAsync(TimeSpan.FromMilliseconds(800)).ConfigureAwait(false); } catch { }
            try { await app.DisposeAsync().ConfigureAwait(false); } catch { }
        }

        try { _cts?.Dispose(); } catch { }
        _cts = null;
        BaseAddress = null;
        Status = "stopped";
    }

    /// <summary>Last-resort teardown when a graceful stop hangs (e.g. UI-thread deadlock).</summary>
    public void ForceAbort()
    {
        Volatile.Write(ref _stopped, 1);
        DetachRadioEvents();
        try { _cts?.Cancel(); } catch { }
        lock (_lifetime)
        {
            var app = _app;
            _app = null;
            if (app is null) return;
            // Do not wait — abandon the host so WinForms can exit.
            _ = Task.Run(async () =>
            {
                try { await app.StopAsync(TimeSpan.FromMilliseconds(200)).ConfigureAwait(false); } catch { }
                try { await app.DisposeAsync().ConfigureAwait(false); } catch { }
            });
        }
        Status = "aborted";
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);

    private void DetachRadioEvents()
    {
        if (_onState is not null) { try { _radio.StateChanged -= _onState; } catch { } _onState = null; }
        if (_onLive is not null) { try { _radio.LiveChanged -= _onLive; } catch { } _onLive = null; }
        if (_onSpectrum is not null) { try { _radio.SpectrumAvailable -= _onSpectrum; } catch { } _onSpectrum = null; }
        if (_onAfSpectrum is not null) { try { _radio.AfSpectrumAvailable -= _onAfSpectrum; } catch { } _onAfSpectrum = null; }
        if (_onAudio is not null) { try { _radio.AudioAvailable -= _onAudio; } catch { } _onAudio = null; }
        if (_onAf is not null) { try { _radio.AfPluginEvent -= _onAf; } catch { } _onAf = null; }
    }

    private async Task TokenGate(HttpContext context, RequestDelegate next)
    {
        if (context.Request.Path.StartsWithSegments("/api/health"))
        {
            await next(context);
            return;
        }

        var expected = _options.AccessToken;
        var provided = context.Request.Headers["X-NeuroSDR-Token"].FirstOrDefault()
            ?? context.Request.Query["token"].FirstOrDefault()
            ?? context.Request.Query["access_token"].FirstOrDefault()
            ?? "";
        if (!string.Equals(expected, provided, StringComparison.Ordinal))
        {
            context.Response.StatusCode = (int)HttpStatusCode.Unauthorized;
            await context.Response.WriteAsync("Unauthorized");
            return;
        }
        await next(context);
    }

    private static string GetLanHint()
    {
        try
        {
            foreach (var address in Dns.GetHostEntry(Dns.GetHostName()).AddressList)
            {
                if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork &&
                    !IPAddress.IsLoopback(address))
                    return address.ToString();
            }
        }
        catch { }
        return "127.0.0.1";
    }
}
