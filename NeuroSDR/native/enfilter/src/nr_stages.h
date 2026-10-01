#ifndef NR_STAGES_H
#define NR_STAGES_H

#include "nr_engine.h"
#include "nr_biquad.h"

#define NR_FRAME 480
#define NR_VSS_MAX_TAPS 32
#define NR_CW_BANDS 48

typedef struct NrBlankerState {
  float avg_abs;
  int hold;
  float prev;
  int hits;
} NrBlankerState;

typedef struct NrNotchState {
  float w[2];
  float x_d1, x_d2;
  float freq_hz;
  float tone_level;
  float noise_level;
  NrBiquad notch;
  int notch_valid;
} NrNotchState;

typedef struct NrClassifierState {
  float noise_power;
  float signal_power;
  float snr_db;
  float vad_prob;
  float blend_ai;
} NrClassifierState;

typedef struct NrWienerState {
  float noise_power;
  float signal_power;
  float band_noise[NR_CW_BANDS];
  float band_signal[NR_CW_BANDS];
} NrWienerState;

typedef struct NrAgcEqState {
  float env;
  float gain;
  NrBiquad hpf;
  NrBiquad lpf;
  NrBiquad presence;
  int eq_ready;
} NrAgcEqState;

void nr_blanker_reset(NrBlankerState *st);
void nr_blanker_process(NrBlankerState *st, const NrConfig *cfg, float *x, int n);

void nr_notch_reset(NrNotchState *st);
/* skip_notch: when CW protect is active, do not carve the desired tone */
void nr_notch_process(NrNotchState *st, const NrConfig *cfg, float *x, int n,
                      float fs, int skip_notch);

void nr_classifier_reset(NrClassifierState *st);
void nr_classifier_analyze(NrClassifierState *st, const NrConfig *cfg,
                           const float *x, int n, float fs);
void nr_classifier_update_vad(NrClassifierState *st, const NrConfig *cfg,
                              float rnnoise_vad);

void nr_wiener_reset(NrWienerState *st);
void nr_wiener_process(NrWienerState *st, const NrConfig *cfg,
                       float *out, const float *in, int n, float fs);

void nr_agc_eq_reset(NrAgcEqState *st);
void nr_agc_eq_prepare(NrAgcEqState *st, const NrConfig *cfg, float fs);
void nr_agc_eq_process(NrAgcEqState *st, const NrConfig *cfg, float *x, int n, float fs);

float nr_frame_rms(const float *x, int n);

#endif
