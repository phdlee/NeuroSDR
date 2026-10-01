#ifndef NR_CW_H
#define NR_CW_H

#include "nr_engine.h"
#include "nr_biquad.h"

#define NR_CW_ALE_MAX_TAPS 32
#define NR_CW_DELAY_BUF 96
#define NR_CW_AUTO_MIN_HZ 200.f
#define NR_CW_AUTO_MAX_HZ 1500.f

typedef struct NrCwState {
  NrBiquad bpf1;
  NrBiquad bpf2;
  NrBiquad apf;
  float bpf_f0;
  float bpf_bw;
  float apf_f0;
  float apf_q;

  float ale_delay[NR_CW_DELAY_BUF];
  float ale_w[NR_CW_ALE_MAX_TAPS];
  int ale_pos;

  float env;
  float gate_gain;

  float tone_mag;
  float tone_prob;

  float nco_phase;
  float regen_env;

  float spectrum[NR_CW_SPEC_BINS];
  int spectrum_ready;

  /* Tracking / AUTO / AFC */
  float track_hz;              /* actual filter center */
  float user_center_hz;        /* last setpoint from config */
  float auto_accum[NR_CW_SPEC_BINS];
  int auto_frames;
  int auto_frames_needed;
  int auto_busy;
  int auto_done_pulse;
  float auto_progress;
  float auto_found_hz;
  float afc_offset_hz;
  int afc_good_streak;     /* consecutive confident CW frames */
  int afc_miss_streak;     /* consecutive weak/no-CW frames */
  float afc_candidate_hz;  /* sticky peak while locking */
  int auto_cand_streak;
  float auto_cand_hz;
} NrCwState;

void nr_cw_reset(NrCwState *st);
void nr_cw_prepare(NrCwState *st, const NrConfig *cfg, float fs);
void nr_cw_state_arm_auto(NrCwState *st, const NrConfig *cfg, float fs);
void nr_cw_process(NrCwState *st, const NrConfig *cfg,
                   float *out, const float *in, int n, float fs);
void nr_cw_copy_spectrum(const NrCwState *st, float *dst, int n_bins);
void nr_cw_fill_status(const NrCwState *st, const NrConfig *cfg, NrStatus *status);

#endif
