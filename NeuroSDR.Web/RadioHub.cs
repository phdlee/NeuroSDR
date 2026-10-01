using Microsoft.AspNetCore.SignalR;

namespace NeuroSDR.Web;

public sealed class RadioHub : Hub
{
    private readonly INeuroSDRRemoteRadio _radio;

    public RadioHub(INeuroSDRRemoteRadio radio) => _radio = radio;

    public RadioRemoteSnapshot GetState() => _radio.GetSnapshot();

    public IReadOnlyList<string> GetSources() => _radio.GetSources();

    public IReadOnlyList<string> GetModes() => _radio.GetModes();

    public IReadOnlyList<AfPluginRemoteEvent> GetAfFeed(int maxItems = 80) => _radio.GetAfFeed(maxItems);

    public void SetRunning(bool running) => _radio.SetRunning(running);
    public void StartRx() => _radio.SetRunning(true);
    public void StopRx() => _radio.SetRunning(false);

    public void SetFrequency(long hz) => _radio.SetFrequency(hz);
    public void NudgeFrequency(long deltaHz) => _radio.NudgeFrequency(deltaHz);
    public void SetMode(string mode) => _radio.SetMode(mode);
    public void SetBandwidth(int hz) => _radio.SetBandwidth(hz);
    public void SetGain(int percent) => _radio.SetGain(percent);
    public void SetVolume(int channel, int percent) => _radio.SetVolume(channel, percent);
    public void SetSquelch(int channel, bool enabled, int thresholdDb) =>
        _radio.SetSquelch(channel, enabled, thresholdDb);
    public void SetSource(string name) => _radio.SetSource(name);
    public void ApplyScene(string sceneId) => _radio.ApplyScene(sceneId);
    public void SelectChannel(string channelId) => _radio.SelectChannel(channelId);
    public void SelectBand(string bandId) => _radio.SelectBand(bandId);
    public void CenterViewOnTune() => _radio.CenterViewOnTune();
    public void SetViewBandwidth(int hz) => _radio.SetViewBandwidth(hz);

    public IReadOnlyList<RemoteSiteRemoteInfo> GetSites(string query) => _radio.GetSites(query ?? "");
    public void SetSiteUrl(string url) => _radio.SetSiteUrl(url);
    public void SetAfDsp(bool agc, bool noiseReduction, int nrStrength, bool notch, bool afFilter) =>
        _radio.SetAfDsp(agc, noiseReduction, nrStrength, notch, afFilter);
    public void SetAfc(bool enabled, int speedIndex, int rangeHz) => _radio.SetAfc(enabled, speedIndex, rangeHz);
    public void SetWfm(bool stereo, bool hfSoft, string? eqPreset) => _radio.SetWfm(stereo, hfSoft, eqPreset);
    public void SetFreedv(string modem, string sideband) => _radio.SetFreedv(modem, sideband);
    public void SetDigitalFeed(int outputChannel, bool pcmAgc, int feedVolumePercent) =>
        _radio.SetDigitalFeed(outputChannel, pcmAgc, feedVolumePercent);
    public void SetCw(bool lowerSide, int afWidthHz) => _radio.SetCw(lowerSide, afWidthHz);
}
