/**
 * NeuroSDR desktop remote UI — TypeScript sources.
 * Runtime modules currently ship as ES modules under ../wwwroot/js/
 * (same API). Vite bundling can be added later without changing the hub contract.
 */

export interface RadioRemoteSnapshot {
  running: boolean;
  source: string;
  frequencyHz: number;
  rfCenterHz: number;
  viewCenterHz: number;
  viewBandwidthHz: number;
  mode: string;
  filterBandwidthHz: number;
  gainPercent: number;
  signalDb: number;
  volume1: number;
  volume2: number;
  squelch1Enabled: boolean;
  squelch1Threshold: number;
  squelch2Enabled: boolean;
  squelch2Threshold: number;
  squelch1Open: boolean;
  squelch2Open: boolean;
  audio2Enabled: boolean;
  activeAfPlugins: string[];
  availableSources: string[];
  availableModes: string[];
  status: string;
  selectedSceneId: string;
  selectedSceneName: string;
  selectedChannelId: string;
  scenes: { id: string; name: string }[];
  channels: {
    id: string;
    name: string;
    label: string;
    frequencyHz: number;
    mode: string;
    bandwidthHz: number;
    global: boolean;
  }[];
  subVfos: {
    id: string;
    name: string;
    frequencyHz: number;
    mode: string;
    bandwidthHz: number;
  }[];
  bands: { id: string; name: string; frequencyHz: number; mode: string }[];
  bandwidthPresets: number[];
}

export interface SpectrumRemoteFrame {
  centerHz: number;
  spanHz: number;
  tunedHz: number;
  filterHz: number;
  mode: string;
  levels: number[];
}

export interface AfPluginRemoteEvent {
  pluginId: string;
  pluginName: string;
  kind: string;
  text: string;
  frequencyHz: number;
  utcTicks: number;
}

/** Hub methods mirrored from RadioHub.cs */
export type RadioHubMethods =
  | "GetState"
  | "GetSources"
  | "GetModes"
  | "GetAfFeed"
  | "SetRunning"
  | "SetFrequency"
  | "NudgeFrequency"
  | "SetMode"
  | "SetBandwidth"
  | "SetGain"
  | "SetVolume"
  | "SetSquelch"
  | "SetSource"
  | "ApplyScene"
  | "SelectChannel"
  | "SelectBand"
  | "CenterViewOnTune"
  | "SetViewBandwidth";
