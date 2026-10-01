#include "nr_engine.h"

#include <math.h>
#include <stdlib.h>
#include <string.h>

#include "nr_cw.h"
#include "nr_stages.h"
#include "rnnoise.h"

struct NrEngine {
  DenoiseState *rnnoise;
  NrConfig cfg;
  NrStatus status;
  NrBlankerState blanker;
  NrNotchState notch;
  NrClassifierState classifier;
  NrWienerState wiener;
  NrAgcEqState agc_eq;
  NrCwState cw;
  NrAgcEqState cw_agc;
  float tmp_a[NR_FRAME];
  float tmp_b[NR_FRAME];
  float tmp_dry[NR_FRAME];
  int cfg_dirty;
};

static void nr_status_clear(NrStatus *st) {
  memset(st, 0, sizeof(*st));
  st->struct_size = (int32_t)sizeof(NrStatus);
  st->version = NR_STATUS_VERSION;
  st->blend_ai = 1.f;
  st->path_mode = NR_PATH_VOICE;
}

static void nr_config_fill_voice_params(NrConfig *cfg) {
  cfg->blanker_threshold = 8.0f;
  cfg->blanker_hold_samples = 12;
  cfg->blanker_avg_ms = 2.0f;
  cfg->notch_mu = 0.03f;
  cfg->notch_bw_hz = 40.0f;
  cfg->notch_min_tone_snr_db = 12.0f;
  cfg->notch_adapt_alpha = 0.12f;
  cfg->vad_threshold = 0.35f;
  cfg->snr_noise_tau_ms = 400.0f;
  cfg->snr_strong_db = 10.0f;
  cfg->snr_weak_db = 3.0f;
  cfg->wiener_strength = 0.55f;
  cfg->wiener_noise_floor_db = -6.0f;
  cfg->vss_mu_max = 0.05f;
  cfg->vss_mu_min = 0.005f;
  cfg->vss_taps = 8;
  cfg->agc_target_db = -18.0f;
  cfg->agc_max_gain_db = 24.0f;
  cfg->agc_attack_ms = 5.0f;
  cfg->agc_decay_ms = 200.0f;
  cfg->eq_low_cut_hz = 300.0f;
  cfg->eq_high_cut_hz = 2500.0f;
  cfg->eq_presence_hz = 1500.0f;
  cfg->eq_presence_gain_db = 3.0f;
  cfg->blend_silence_ratio = 0.08f;
  cfg->blend_dry_mix = 0.35f;
  cfg->blend_mode = NR_BLEND_MODE_VAD;
  cfg->blend_vad_low = 0.25f;
  cfg->blend_vad_high = 0.70f;
  cfg->blend_fixed_ai = 0.70f;
  cfg->blend_smooth = 0.15f;
}

static void nr_config_fill_cw_params(NrConfig *cfg) {
  cfg->cw_enable_blanker = 1;
  cfg->cw_enable_bpf = 1;
  cfg->cw_bpf_center_hz = 750.f;
  cfg->cw_bpf_bw_hz = 70.f;
  cfg->cw_enable_ale = 1;
  cfg->cw_ale_mu = 0.05f;
  cfg->cw_ale_delay = 8;
  cfg->cw_ale_taps = 12;
  cfg->cw_enable_apf = 1;
  cfg->cw_apf_q = 35.f;
  cfg->cw_apf_gain_db = 6.f;
  cfg->cw_enable_goertzel = 1;
  cfg->cw_goertzel_thr = 80.f;
  cfg->cw_enable_gate = 1;
  cfg->cw_gate_threshold = 400.f;
  cfg->cw_gate_attack_ms = 2.f;
  cfg->cw_gate_release_ms = 40.f;
  cfg->cw_enable_regen = 0;
  cfg->cw_regen_mix = 0.35f;
  cfg->cw_enable_agc = 1;
  cfg->cw_agc_target_db = -16.f;
  cfg->cw_agc_max_gain_db = 30.f;
  cfg->cw_auto_arm = 0;
  cfg->cw_auto_seconds = 2.f;
  cfg->cw_enable_afc = 0;
  cfg->cw_afc_range_hz = 80.f;
  cfg->cw_afc_smooth = 0.15f;
}

void nr_config_default(NrConfig *cfg) {
  nr_config_preset_voice(cfg);
}

void nr_config_preset_voice(NrConfig *cfg) {
  if (!cfg) return;
  memset(cfg, 0, sizeof(*cfg));
  cfg->struct_size = (int32_t)sizeof(NrConfig);
  cfg->version = NR_CONFIG_VERSION;
  cfg->path_mode = NR_PATH_VOICE;
  cfg->enable_blanker = 1;
  cfg->enable_notch = 0;
  cfg->enable_classifier = 1;
  cfg->enable_rnnoise = 1;
  cfg->enable_wiener = 0;
  cfg->enable_hybrid_blend = 0;
  cfg->enable_agc = 1;
  cfg->enable_eq = 1;
  nr_config_fill_voice_params(cfg);
  nr_config_fill_cw_params(cfg);
}

void nr_config_preset_cw(NrConfig *cfg) {
  if (!cfg) return;
  memset(cfg, 0, sizeof(*cfg));
  cfg->struct_size = (int32_t)sizeof(NrConfig);
  cfg->version = NR_CONFIG_VERSION;
  cfg->path_mode = NR_PATH_CW;
  /* voice stages unused but keep sane defaults */
  nr_config_fill_voice_params(cfg);
  nr_config_fill_cw_params(cfg);
}

static void nr_engine_apply_runtime(NrEngine *eng) {
  nr_agc_eq_prepare(&eng->agc_eq, &eng->cfg, (float)nr_sample_rate());
  nr_cw_prepare(&eng->cw, &eng->cfg, (float)nr_sample_rate());
  {
    NrConfig tmp = eng->cfg;
    tmp.eq_low_cut_hz = eng->cfg.cw_bpf_center_hz * 0.5f;
    if (tmp.eq_low_cut_hz < 100.f) tmp.eq_low_cut_hz = 100.f;
    tmp.eq_high_cut_hz = eng->cfg.cw_bpf_center_hz * 2.f;
    if (tmp.eq_high_cut_hz > 4000.f) tmp.eq_high_cut_hz = 4000.f;
    tmp.eq_presence_hz = eng->cfg.cw_bpf_center_hz;
    tmp.eq_presence_gain_db = 0.f;
    tmp.agc_target_db = eng->cfg.cw_agc_target_db;
    tmp.agc_max_gain_db = eng->cfg.cw_agc_max_gain_db;
    tmp.agc_attack_ms = 3.f;
    tmp.agc_decay_ms = 120.f;
    tmp.enable_eq = 0;
    tmp.enable_agc = eng->cfg.cw_enable_agc;
    nr_agc_eq_prepare(&eng->cw_agc, &tmp, (float)nr_sample_rate());
  }
  eng->cfg_dirty = 0;
}

NrEngine *nr_create(void) {
  NrEngine *eng = (NrEngine *)calloc(1, sizeof(*eng));
  if (!eng) return NULL;
  eng->rnnoise = rnnoise_create(NULL);
  if (!eng->rnnoise) {
    free(eng);
    return NULL;
  }
  nr_config_default(&eng->cfg);
  nr_status_clear(&eng->status);
  nr_blanker_reset(&eng->blanker);
  nr_notch_reset(&eng->notch);
  nr_classifier_reset(&eng->classifier);
  nr_wiener_reset(&eng->wiener);
  nr_agc_eq_reset(&eng->agc_eq);
  nr_cw_reset(&eng->cw);
  nr_agc_eq_reset(&eng->cw_agc);
  eng->cfg_dirty = 1;
  return eng;
}

void nr_destroy(NrEngine *eng) {
  if (!eng) return;
  if (eng->rnnoise) rnnoise_destroy(eng->rnnoise);
  free(eng);
}

int nr_set_config(NrEngine *eng, const NrConfig *cfg) {
  if (!eng || !cfg) return -1;
  if (cfg->struct_size < (int32_t)sizeof(int32_t) * 2) return -2;
  eng->cfg = *cfg;
  eng->cfg.struct_size = (int32_t)sizeof(NrConfig);
  eng->cfg.version = NR_CONFIG_VERSION;
  if (eng->cfg.path_mode != NR_PATH_CW) eng->cfg.path_mode = NR_PATH_VOICE;
  if (eng->cfg.vss_taps < 2) eng->cfg.vss_taps = 2;
  if (eng->cfg.vss_taps > NR_VSS_MAX_TAPS) eng->cfg.vss_taps = NR_VSS_MAX_TAPS;
  if (eng->cfg.cw_bpf_center_hz < 100.f) eng->cfg.cw_bpf_center_hz = 100.f;
  if (eng->cfg.cw_bpf_bw_hz < 20.f) eng->cfg.cw_bpf_bw_hz = 20.f;
  eng->cfg_dirty = 1;
  return 0;
}

void nr_get_config(const NrEngine *eng, NrConfig *cfg) {
  if (!eng || !cfg) return;
  *cfg = eng->cfg;
}

void nr_get_status(const NrEngine *eng, NrStatus *status) {
  if (!eng || !status) return;
  *status = eng->status;
  status->struct_size = (int32_t)sizeof(NrStatus);
  status->version = NR_STATUS_VERSION;
}

void nr_reset(NrEngine *eng) {
  if (!eng) return;
  nr_blanker_reset(&eng->blanker);
  nr_notch_reset(&eng->notch);
  nr_classifier_reset(&eng->classifier);
  nr_wiener_reset(&eng->wiener);
  nr_agc_eq_reset(&eng->agc_eq);
  nr_cw_reset(&eng->cw);
  nr_agc_eq_reset(&eng->cw_agc);
  nr_status_clear(&eng->status);
  eng->cfg_dirty = 1;
  if (eng->rnnoise) {
    rnnoise_destroy(eng->rnnoise);
    eng->rnnoise = rnnoise_create(NULL);
  }
}

int nr_frame_size(void) { return NR_FRAME; }
int nr_sample_rate(void) { return 48000; }

int nr_get_cw_spectrum(const NrEngine *eng, float *bins, int n_bins) {
  if (!eng || !bins || n_bins <= 0) return -1;
  nr_cw_copy_spectrum(&eng->cw, bins, n_bins);
  return 0;
}

void nr_cw_arm_auto(NrEngine *eng) {
  if (!eng) return;
  eng->cfg.cw_auto_arm = 1;
  nr_cw_state_arm_auto(&eng->cw, &eng->cfg, (float)nr_sample_rate());
}

static float nr_peak(const float *x, int n) {
  float p = 0.f;
  int i;
  for (i = 0; i < n; ++i) {
    float a = fabsf(x[i]);
    if (a > p) p = a;
  }
  return p;
}

static float nr_process_voice(NrEngine *eng, float *buf) {
  float vad_rn = -1.f;
  float blend = 1.f;
  float rms_in, rms_a, rms_b;
  int i;
  const float fs = 48000.f;

  rms_in = nr_frame_rms(eng->tmp_dry, NR_FRAME);

  if (eng->cfg.enable_blanker) nr_blanker_process(&eng->blanker, &eng->cfg, buf, NR_FRAME);
  else eng->blanker.hits = 0;

  if (eng->cfg.enable_notch) nr_notch_process(&eng->notch, &eng->cfg, buf, NR_FRAME, fs, 0);
  else eng->notch.notch_valid = 0;

  if (eng->cfg.enable_classifier) {
    nr_classifier_analyze(&eng->classifier, &eng->cfg, buf, NR_FRAME, fs);
  }

  memcpy(eng->tmp_a, buf, sizeof(float) * NR_FRAME);
  memcpy(eng->tmp_b, buf, sizeof(float) * NR_FRAME);

  if (eng->cfg.enable_rnnoise) {
    vad_rn = rnnoise_process_frame(eng->rnnoise, eng->tmp_a, eng->tmp_a);
  }
  if (eng->cfg.enable_wiener) {
    nr_wiener_process(&eng->wiener, &eng->cfg, eng->tmp_b, eng->tmp_b, NR_FRAME, fs);
  }

  nr_classifier_update_vad(&eng->classifier, &eng->cfg, vad_rn);
  if (eng->cfg.enable_classifier) {
    nr_classifier_analyze(&eng->classifier, &eng->cfg, buf, NR_FRAME, fs);
  }

  rms_a = nr_frame_rms(eng->tmp_a, NR_FRAME);
  rms_b = nr_frame_rms(eng->tmp_b, NR_FRAME);

  if (eng->cfg.enable_hybrid_blend && eng->cfg.enable_rnnoise && eng->cfg.enable_wiener) {
    float thr = eng->cfg.blend_silence_ratio * (rms_in + 1e-3f);
    int a_dead = rms_a < thr;
    int b_dead = rms_b < thr;
    blend = eng->cfg.enable_classifier ? eng->classifier.blend_ai : 0.7f;
    if (a_dead && !b_dead) blend = 0.f;
    else if (b_dead && !a_dead) blend = 1.f;
    else if (a_dead && b_dead) {
      float dry = eng->cfg.blend_dry_mix;
      for (i = 0; i < NR_FRAME; ++i)
        buf[i] = (1.f - dry) * eng->tmp_a[i] + dry * eng->tmp_dry[i];
      blend = 0.5f;
    } else {
      for (i = 0; i < NR_FRAME; ++i)
        buf[i] = blend * eng->tmp_a[i] + (1.f - blend) * eng->tmp_b[i];
    }
  } else if (eng->cfg.enable_rnnoise) {
    memcpy(buf, eng->tmp_a, sizeof(float) * NR_FRAME);
    blend = 1.f;
  } else if (eng->cfg.enable_wiener) {
    memcpy(buf, eng->tmp_b, sizeof(float) * NR_FRAME);
    blend = 0.f;
  }

  if (eng->cfg.enable_agc || eng->cfg.enable_eq) {
    nr_agc_eq_process(&eng->agc_eq, &eng->cfg, buf, NR_FRAME, fs);
  }

  eng->status.vad_prob = (vad_rn >= 0.f) ? vad_rn : eng->classifier.vad_prob;
  eng->status.snr_db = eng->classifier.snr_db;
  eng->status.blend_ai = blend;
  eng->status.path_a_rms = rms_a;
  eng->status.path_b_rms = rms_b;
  eng->status.blanker_rate = (float)eng->blanker.hits / (float)NR_FRAME;
  eng->status.notch_freq_hz = eng->notch.freq_hz;
  eng->status.notch_depth = eng->notch.notch_valid ? 1.f : 0.f;
  eng->status.agc_gain_db = 20.f * log10f(eng->agc_eq.gain < 1e-6f ? 1e-6f : eng->agc_eq.gain);
  return eng->status.vad_prob;
}

static float nr_process_cw(NrEngine *eng, float *buf) {
  const float fs = 48000.f;
  NrConfig agc_cfg;

  if (eng->cfg.cw_enable_blanker) {
    NrConfig bcfg = eng->cfg;
    bcfg.blanker_threshold = eng->cfg.blanker_threshold > 1.f ? eng->cfg.blanker_threshold : 10.f;
    bcfg.blanker_hold_samples = eng->cfg.blanker_hold_samples > 0 ? eng->cfg.blanker_hold_samples : 8;
    bcfg.blanker_avg_ms = eng->cfg.blanker_avg_ms > 0.f ? eng->cfg.blanker_avg_ms : 1.5f;
    nr_blanker_process(&eng->blanker, &bcfg, buf, NR_FRAME);
  } else {
    eng->blanker.hits = 0;
  }

  nr_cw_process(&eng->cw, &eng->cfg, buf, buf, NR_FRAME, fs);

  /* When AUTO finishes with a clear CW peak, lock user center and clear arm. */
  if (eng->cw.auto_done_pulse) {
    eng->cfg.cw_auto_arm = 0;
    if (eng->cw.auto_found_hz > 50.f) {
      eng->cfg.cw_bpf_center_hz = eng->cw.auto_found_hz;
      eng->cw.user_center_hz = eng->cw.auto_found_hz;
      eng->cw.afc_offset_hz = 0.f;
    }
  }

  if (eng->cfg.cw_enable_agc) {
    agc_cfg = eng->cfg;
    agc_cfg.enable_agc = 1;
    agc_cfg.enable_eq = 0;
    agc_cfg.agc_target_db = eng->cfg.cw_agc_target_db;
    agc_cfg.agc_max_gain_db = eng->cfg.cw_agc_max_gain_db;
    agc_cfg.agc_attack_ms = 3.f;
    agc_cfg.agc_decay_ms = 120.f;
    nr_agc_eq_process(&eng->cw_agc, &agc_cfg, buf, NR_FRAME, fs);
  }

  eng->status.blanker_rate = (float)eng->blanker.hits / (float)NR_FRAME;
  eng->status.agc_gain_db = 20.f * log10f(eng->cw_agc.gain < 1e-6f ? 1e-6f : eng->cw_agc.gain);
  eng->status.vad_prob = eng->cw.tone_prob;
  eng->status.blend_ai = 0.f;
  nr_cw_fill_status(&eng->cw, &eng->cfg, &eng->status);
  return eng->cw.tone_prob;
}

float nr_process_frame_f32(NrEngine *eng, float *out, const float *in) {
  float *buf;
  int i;

  if (!eng || !eng->rnnoise || !out || !in) return 0.f;
  if (eng->cfg_dirty) nr_engine_apply_runtime(eng);

  if (out != in) memcpy(out, in, sizeof(float) * NR_FRAME);
  buf = out;
  memcpy(eng->tmp_dry, in, sizeof(float) * NR_FRAME);

  eng->status.path_mode = eng->cfg.path_mode;
  if (eng->cfg.path_mode == NR_PATH_CW) {
    nr_process_cw(eng, buf);
  } else {
    nr_process_voice(eng, buf);
  }

  for (i = 0; i < NR_FRAME; ++i) {
    if (buf[i] > 32767.f) buf[i] = 32767.f;
    else if (buf[i] < -32768.f) buf[i] = -32768.f;
  }

  eng->status.struct_size = (int32_t)sizeof(NrStatus);
  eng->status.version = NR_STATUS_VERSION;
  eng->status.frame_peak = nr_peak(buf, NR_FRAME);
  return eng->status.vad_prob;
}

int nr_process_s16(NrEngine *eng, int16_t *out, const int16_t *in, int length) {
  float buf[NR_FRAME];
  int frames = 0;
  int i, n;
  if (!eng || !out || !in || length < NR_FRAME) return 0;
  n = (length / NR_FRAME) * NR_FRAME;
  for (i = 0; i < n; i += NR_FRAME) {
    int j;
    for (j = 0; j < NR_FRAME; ++j) buf[j] = (float)in[i + j];
    nr_process_frame_f32(eng, buf, buf);
    for (j = 0; j < NR_FRAME; ++j) out[i + j] = (int16_t)buf[j];
    ++frames;
  }
  return frames;
}
