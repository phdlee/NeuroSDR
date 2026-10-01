/**
 * ENFilter — platform-independent noise-reduction engine API.
 * Voice (SSB/RNNoise) and CW are completely separate paths.
 * Plugin/DLL product name: ENFilter (ENFilter.dll / ENFilterCore.lib).
 */
#ifndef NR_ENGINE_H
#define NR_ENGINE_H

#include <stddef.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

/** Product identity for hosts / SDR plugins */
#define ENFILTER_NAME "ENFilter"
#define ENFILTER_PRODUCT "ENFilter"

#if defined(_WIN32) && defined(NR_ENGINE_DLL)
#  if defined(NR_ENGINE_BUILD)
#    define NR_API __declspec(dllexport)
#  else
#    define NR_API __declspec(dllimport)
#  endif
#else
#  define NR_API
#endif

#define NR_CONFIG_VERSION 5
#define NR_STATUS_VERSION 4
#define NR_CW_SPEC_BINS 128

/* Processing path: voice DSP vs dedicated CW DSP */
#define NR_PATH_VOICE 0
#define NR_PATH_CW    1

/* hybrid blend_mode (voice path only) */
#define NR_BLEND_MODE_SNR   0
#define NR_BLEND_MODE_VAD   1
#define NR_BLEND_MODE_FIXED 2

typedef struct NrEngine NrEngine;

typedef struct NrConfig {
  int32_t struct_size;
  int32_t version;

  /* Global path select */
  int32_t path_mode; /* NR_PATH_VOICE or NR_PATH_CW */

  /* ===== Voice path enables ===== */
  int32_t enable_blanker;
  int32_t enable_notch;
  int32_t enable_classifier;
  int32_t enable_rnnoise;
  int32_t enable_wiener;
  int32_t enable_hybrid_blend;
  int32_t enable_agc;
  int32_t enable_eq;

  /* Voice Stage 1 Blanker */
  float blanker_threshold;
  int32_t blanker_hold_samples;
  float blanker_avg_ms;

  /* Voice Stage 2 Auto Notch */
  float notch_mu;
  float notch_bw_hz;
  float notch_min_tone_snr_db;
  float notch_adapt_alpha;

  /* Voice Stage 3 Classifier */
  float vad_threshold;
  float snr_noise_tau_ms;
  float snr_strong_db;
  float snr_weak_db;

  /* Voice Stage 4B Wiener */
  float wiener_strength;
  float wiener_noise_floor_db;
  float vss_mu_max;
  float vss_mu_min;
  int32_t vss_taps;

  /* Voice Stage 5 AGC / EQ */
  float agc_target_db;
  float agc_max_gain_db;
  float agc_attack_ms;
  float agc_decay_ms;
  float eq_low_cut_hz;
  float eq_high_cut_hz;
  float eq_presence_hz;
  float eq_presence_gain_db;

  /* Voice hybrid blend */
  float blend_silence_ratio;
  float blend_dry_mix;
  int32_t blend_mode;
  float blend_vad_low;
  float blend_vad_high;
  float blend_fixed_ai;
  float blend_smooth;

  /* ===== CW path (independent) ===== */
  int32_t cw_enable_blanker;   /* optional impulse blanker before CW chain */
  int32_t cw_enable_bpf;
  float cw_bpf_center_hz;      /* e.g. 750 */
  float cw_bpf_bw_hz;          /* e.g. 70 */
  int32_t cw_enable_ale;
  float cw_ale_mu;
  int32_t cw_ale_delay;        /* samples */
  int32_t cw_ale_taps;
  int32_t cw_enable_apf;
  float cw_apf_q;
  float cw_apf_gain_db;
  int32_t cw_enable_goertzel;
  float cw_goertzel_thr;       /* tone magnitude threshold */
  int32_t cw_enable_gate;
  float cw_gate_threshold;
  float cw_gate_attack_ms;
  float cw_gate_release_ms;
  int32_t cw_enable_regen;     /* optional NCO regen with confidence mix */
  float cw_regen_mix;          /* 0=filtered audio .. 1=full synth when confident */
  int32_t cw_enable_agc;
  float cw_agc_target_db;
  float cw_agc_max_gain_db;

  /* CW AUTO peak-find + AFC tracking */
  int32_t cw_auto_arm;         /* set 1 to start scan; cleared when done */
  float cw_auto_seconds;       /* max scan timeout (early exit on clear CW) */
  int32_t cw_enable_afc;       /* follow clear CW within +/- cw_afc_range_hz */
  float cw_afc_range_hz;       /* e.g. 80 */
  float cw_afc_smooth;         /* tracking EMA 0.05..0.5 */
} NrConfig;

typedef struct NrStatus {
  int32_t struct_size;
  int32_t version;
  int32_t path_mode;
  float snr_db;
  float vad_prob;
  float blanker_rate;
  float notch_freq_hz;
  float notch_depth;
  float blend_ai;
  float agc_gain_db;
  float frame_peak;
  float path_a_rms;
  float path_b_rms;
  /* CW meters */
  float cw_tone_prob;
  float cw_tone_mag;
  float cw_center_hz;      /* actual filter/track frequency */
  float cw_bw_hz;
  float cw_gate_gain;
  float cw_user_center_hz; /* UI setpoint */
  float cw_afc_offset_hz;  /* track - user (+/-) */
  int32_t cw_auto_busy;
  float cw_auto_progress;  /* 0..1 */
  int32_t cw_auto_done;    /* 1 for a frame when AUTO finished */
  float cw_auto_found_hz;
} NrStatus;

NR_API void nr_config_default(NrConfig *cfg);
NR_API void nr_config_preset_voice(NrConfig *cfg);
NR_API void nr_config_preset_cw(NrConfig *cfg);

NR_API NrEngine *nr_create(void);
NR_API void nr_destroy(NrEngine *eng);
NR_API int nr_set_config(NrEngine *eng, const NrConfig *cfg);
NR_API void nr_get_config(const NrEngine *eng, NrConfig *cfg);
NR_API void nr_get_status(const NrEngine *eng, NrStatus *status);
NR_API void nr_reset(NrEngine *eng);
NR_API int nr_frame_size(void);
NR_API int nr_sample_rate(void);

NR_API float nr_process_frame_f32(NrEngine *eng, float *out, const float *in);
NR_API int nr_process_s16(NrEngine *eng, int16_t *out, const int16_t *in, int length);

/** Arm CW AUTO peak search (also sets cfg.cw_auto_arm). */
NR_API void nr_cw_arm_auto(NrEngine *eng);

/** Copy CW spectrum magnitudes (linear power). n_bins typically NR_CW_SPEC_BINS. */
NR_API int nr_get_cw_spectrum(const NrEngine *eng, float *bins, int n_bins);

#ifdef __cplusplus
}
#endif

#endif /* NR_ENGINE_H */
