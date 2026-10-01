#include "nr_cw.h"

#include <math.h>
#include <string.h>

#ifndef M_PI
#define M_PI 3.14159265358979323846
#endif

static float nr_clampf(float x, float lo, float hi) {
  if (x < lo) return lo;
  if (x > hi) return hi;
  return x;
}

static float nr_alpha_ms(float ms, float fs) {
  if (ms < 0.1f) ms = 0.1f;
  return 1.f - expf(-1.f / (0.001f * ms * fs));
}

static float nr_cw_bin_hz(int b) {
  return NR_CW_AUTO_MIN_HZ +
         (NR_CW_AUTO_MAX_HZ - NR_CW_AUTO_MIN_HZ) * ((float)b / (float)(NR_CW_SPEC_BINS - 1));
}

void nr_cw_reset(NrCwState *st) {
  memset(st, 0, sizeof(*st));
  st->gate_gain = 0.f;
  st->bpf_f0 = -1.f;
  st->apf_f0 = -1.f;
  st->apf_q = -1.f;
  st->track_hz = 750.f;
  st->user_center_hz = 750.f;
  st->afc_candidate_hz = 750.f;
  st->auto_cand_hz = 0.f;
}

static void nr_cw_update_filters(NrCwState *st, const NrConfig *cfg, float fs, float f0) {
  float bw = cfg->cw_bpf_bw_hz;
  float q;
  if (f0 < 100.f) f0 = 100.f;
  if (f0 > fs * 0.45f) f0 = fs * 0.45f;
  if (bw < 20.f) bw = 20.f;
  if (bw > 500.f) bw = 500.f;
  q = f0 / bw;
  if (q < 1.f) q = 1.f;
  if (q > 80.f) q = 80.f;

  if (fabsf(f0 - st->bpf_f0) > 0.5f || fabsf(bw - st->bpf_bw) > 0.5f) {
    nr_biquad_set_bandpass(&st->bpf1, fs, f0, q);
    nr_biquad_set_bandpass(&st->bpf2, fs, f0, q);
    st->bpf_f0 = f0;
    st->bpf_bw = bw;
  }

  if (cfg->cw_enable_apf) {
    float aq = cfg->cw_apf_q;
    if (aq < 5.f) aq = 5.f;
    if (aq > 80.f) aq = 80.f;
    if (fabsf(f0 - st->apf_f0) > 0.5f || fabsf(aq - st->apf_q) > 0.5f) {
      nr_biquad_set_peaking(&st->apf, fs, f0, aq, cfg->cw_apf_gain_db);
      st->apf_f0 = f0;
      st->apf_q = aq;
    }
  }
}

void nr_cw_prepare(NrCwState *st, const NrConfig *cfg, float fs) {
  st->user_center_hz = cfg->cw_bpf_center_hz;
  if (st->track_hz < 100.f) st->track_hz = cfg->cw_bpf_center_hz;
  if (!cfg->cw_enable_afc && !st->auto_busy) {
    st->track_hz = cfg->cw_bpf_center_hz;
  }
  nr_cw_update_filters(st, cfg, fs, st->track_hz);
}

void nr_cw_state_arm_auto(NrCwState *st, const NrConfig *cfg, float fs) {
  float sec = cfg->cw_auto_seconds;
  int need;
  (void)fs;
  /* Max timeout only; clear CW can finish much earlier. */
  if (sec < 0.3f) sec = 0.3f;
  if (sec > 3.f) sec = 3.f;
  need = (int)(sec * 100.f + 0.5f); /* 10 ms frames */
  if (need < 30) need = 30;
  if (need > 400) need = 400;
  memset(st->auto_accum, 0, sizeof(st->auto_accum));
  st->auto_frames = 0;
  st->auto_frames_needed = need;
  st->auto_busy = 1;
  st->auto_done_pulse = 0;
  st->auto_progress = 0.f;
  st->auto_found_hz = 0.f;
  st->auto_cand_streak = 0;
  st->auto_cand_hz = 0.f;
}

static float nr_cw_goertzel_mag(const float *x, int n, float fs, float f0) {
  float w = 2.f * (float)M_PI * f0 / fs;
  float coeff = 2.f * cosf(w);
  float s0 = 0.f, s1 = 0.f, s2 = 0.f;
  int i;
  for (i = 0; i < n; ++i) {
    s0 = x[i] + coeff * s1 - s2;
    s2 = s1;
    s1 = s0;
  }
  {
    float re = s1 - s2 * cosf(w);
    float im = s2 * sinf(w);
    return sqrtf(re * re + im * im) / (float)n;
  }
}

static void nr_cw_update_spectrum(NrCwState *st, const float *x, int n, float fs) {
  int b;
  for (b = 0; b < NR_CW_SPEC_BINS; ++b) {
    float f = nr_cw_bin_hz(b);
    float w0 = 2.f * (float)M_PI * f / fs;
    float c = cosf(w0), s = sinf(w0);
    float rc = 1.f, rs = 0.f, re = 0.f, im = 0.f;
    int i;
    for (i = 0; i < n; ++i) {
      float nrc = rc * c - rs * s;
      float nrs = rc * s + rs * c;
      rc = nrc;
      rs = nrs;
      re += x[i] * rc;
      im += x[i] * rs;
    }
    st->spectrum[b] = 0.7f * st->spectrum[b] + 0.3f * ((re * re + im * im) / (float)(n * n));
  }
  st->spectrum_ready = 1;
}

typedef struct NrCwPeakInfo {
  float peak_hz;
  float peak_mag;
  float second_mag;
  float mean;
  int confident;
} NrCwPeakInfo;

/* Narrow CW tone: strong unique spectral peak, not just "loudest bin". */
static void nr_cw_analyze_peak(const float *spec, float lo_hz, float hi_hz,
                               float abs_floor, NrCwPeakInfo *out) {
  int b;
  float best = -1.f, second = -1.f, best_f = 0.5f * (lo_hz + hi_hz);
  float sum = 0.f;
  int cnt = 0;
  float ratio_mean, ratio_2nd;

  out->peak_hz = best_f;
  out->peak_mag = 0.f;
  out->second_mag = 0.f;
  out->mean = 0.f;
  out->confident = 0;

  for (b = 0; b < NR_CW_SPEC_BINS; ++b) {
    float f = nr_cw_bin_hz(b);
    float m;
    if (f < lo_hz || f > hi_hz) continue;
    m = spec[b];
    sum += m;
    cnt++;
    if (m > best) {
      second = best;
      best = m;
      best_f = f;
    } else if (m > second) {
      second = m;
    }
  }
  if (cnt <= 0 || best < 0.f) return;

  out->peak_hz = best_f;
  out->peak_mag = best;
  out->second_mag = second > 0.f ? second : 0.f;
  out->mean = sum / (float)cnt;

  /* Absolute floor: ignore silence / hiss peaks. PCM16-scale spectrum. */
  if (best < abs_floor) return;
  if (out->mean < 1e-20f) return;

  ratio_mean = best / out->mean;
  ratio_2nd = (second > 1e-20f) ? (best / second) : 100.f;

  /* Require clear prominence + uniqueness (CW-like sinusoid). */
  if (ratio_mean < 5.0f) return;
  if (ratio_2nd < 1.7f) return;

  /* Extra: peak must dominate local neighborhood (±~30 Hz). */
  {
    float local = 0.f;
    int lc = 0;
    for (b = 0; b < NR_CW_SPEC_BINS; ++b) {
      float f = nr_cw_bin_hz(b);
      float df = fabsf(f - best_f);
      if (df < 8.f || df > 35.f) continue;
      local += spec[b];
      lc++;
    }
    if (lc > 0) {
      float lmean = local / (float)lc;
      if (lmean > 1e-20f && best < lmean * 3.5f) return;
    }
  }

  out->confident = 1;
}

static void nr_cw_finish_auto(NrCwState *st, float best_f) {
  st->track_hz = best_f;
  st->auto_found_hz = best_f;
  st->auto_busy = 0;
  st->auto_done_pulse = 1;
  st->auto_progress = 1.f;
  st->afc_offset_hz = 0.f;
  st->afc_good_streak = 0;
  st->afc_miss_streak = 0;
  st->afc_candidate_hz = best_f;
}

static void nr_cw_run_auto_afc(NrCwState *st, const NrConfig *cfg, const float *x, int n, float fs) {
  int b;
  float frame_pwr = 0.f;
  float abs_floor;
  (void)fs;
  st->auto_done_pulse = 0;
  st->user_center_hz = cfg->cw_bpf_center_hz;

  for (b = 0; b < n; ++b) frame_pwr += x[b] * x[b];
  frame_pwr /= (float)(n > 0 ? n : 1);
  /* Spectrum ~ A^2/4 for matched tone; floor scales with weak frame energy. */
  abs_floor = 0.02f * frame_pwr;
  if (abs_floor < 80.f) abs_floor = 80.f; /* reject near-silence */

  /* Always refresh spectrum from raw input for peak search (pre-filter). */
  nr_cw_update_spectrum(st, x, n, fs);

  if (st->auto_busy || cfg->cw_auto_arm) {
    NrCwPeakInfo cur, acc;
    const int min_frames = 8;      /* ~80 ms before early lock */
    const int need_streak = 4;     /* stable clear CW */

    if (!st->auto_busy) {
      nr_cw_state_arm_auto(st, cfg, fs);
    }
    for (b = 0; b < NR_CW_SPEC_BINS; ++b) {
      st->auto_accum[b] += st->spectrum[b];
    }
    st->auto_frames++;
    st->auto_progress = (float)st->auto_frames / (float)st->auto_frames_needed;
    if (st->auto_progress > 1.f) st->auto_progress = 1.f;

    /* Early exit: clear CW-like peak seen consistently (no need to wait full timeout). */
    nr_cw_analyze_peak(st->spectrum, NR_CW_AUTO_MIN_HZ, NR_CW_AUTO_MAX_HZ, abs_floor, &cur);
    if (st->auto_frames >= min_frames && cur.confident) {
      if (st->auto_cand_hz > 0.f && fabsf(cur.peak_hz - st->auto_cand_hz) <= 25.f) {
        st->auto_cand_streak++;
      } else {
        st->auto_cand_hz = cur.peak_hz;
        st->auto_cand_streak = 1;
      }
      if (st->auto_cand_streak >= need_streak) {
        nr_cw_finish_auto(st, cur.peak_hz);
        return;
      }
    } else {
      st->auto_cand_streak = 0;
    }

    if (st->auto_frames >= st->auto_frames_needed) {
      /* Timeout: only lock if accum still looks like CW; else keep user center. */
      nr_cw_analyze_peak(st->auto_accum, NR_CW_AUTO_MIN_HZ, NR_CW_AUTO_MAX_HZ,
                         abs_floor * (float)st->auto_frames_needed * 0.35f, &acc);
      if (acc.confident) {
        nr_cw_finish_auto(st, acc.peak_hz);
      } else {
        st->auto_found_hz = 0.f;
        st->auto_busy = 0;
        st->auto_done_pulse = 1;
        st->auto_progress = 1.f;
        st->track_hz = st->user_center_hz;
        st->afc_offset_hz = 0.f;
      }
    }
    return;
  }

  if (cfg->cw_enable_afc) {
    float range = cfg->cw_afc_range_hz;
    float smooth = cfg->cw_afc_smooth;
    float lo, hi;
    NrCwPeakInfo pk;
    const int lock_need = 6;   /* ~60 ms confident CW before moving */
    const int miss_hold = 25;  /* hold track ~250 ms after signal fades */
    const int miss_return = 60; /* then drift back to user center */

    if (range < 10.f) range = 10.f;
    if (range > 400.f) range = 400.f;
    if (smooth < 0.05f) smooth = 0.05f;
    if (smooth > 0.5f) smooth = 0.5f;
    lo = st->user_center_hz - range;
    hi = st->user_center_hz + range;
    if (lo < NR_CW_AUTO_MIN_HZ) lo = NR_CW_AUTO_MIN_HZ;
    if (hi > NR_CW_AUTO_MAX_HZ) hi = NR_CW_AUTO_MAX_HZ;

    nr_cw_analyze_peak(st->spectrum, lo, hi, abs_floor, &pk);

    if (pk.confident) {
      if (fabsf(pk.peak_hz - st->afc_candidate_hz) <= 20.f) {
        st->afc_good_streak++;
      } else {
        st->afc_candidate_hz = pk.peak_hz;
        st->afc_good_streak = 1;
      }
      st->afc_miss_streak = 0;

      if (st->afc_good_streak >= lock_need) {
        /* Slow AFC once locked: less hunting on marginal tones. */
        float s = smooth * 0.55f;
        st->track_hz = (1.f - s) * st->track_hz + s * pk.peak_hz;
      }
    } else {
      st->afc_good_streak = 0;
      st->afc_miss_streak++;
      /* No clear CW: do not chase noise. Hold briefly, then return to setpoint. */
      if (st->afc_miss_streak > miss_return) {
        float back = 0.08f;
        st->track_hz = (1.f - back) * st->track_hz + back * st->user_center_hz;
      } else if (st->afc_miss_streak > miss_hold) {
        /* hold track_hz */
      }
    }

    st->track_hz = nr_clampf(st->track_hz, lo, hi);
    st->afc_offset_hz = st->track_hz - st->user_center_hz;
  } else {
    st->track_hz = st->user_center_hz;
    st->afc_offset_hz = 0.f;
    st->afc_good_streak = 0;
    st->afc_miss_streak = 0;
  }
}

void nr_cw_copy_spectrum(const NrCwState *st, float *dst, int n_bins) {
  int n = n_bins < NR_CW_SPEC_BINS ? n_bins : NR_CW_SPEC_BINS;
  int i;
  if (!dst || n_bins <= 0) return;
  for (i = 0; i < n; ++i) dst[i] = st->spectrum[i];
  for (; i < n_bins; ++i) dst[i] = 0.f;
}

void nr_cw_fill_status(const NrCwState *st, const NrConfig *cfg, NrStatus *status) {
  status->cw_tone_prob = st->tone_prob;
  status->cw_tone_mag = st->tone_mag;
  status->cw_center_hz = st->track_hz;
  status->cw_bw_hz = cfg->cw_bpf_bw_hz;
  status->cw_gate_gain = st->gate_gain;
  status->cw_user_center_hz = st->user_center_hz;
  status->cw_afc_offset_hz = st->afc_offset_hz;
  status->cw_auto_busy = st->auto_busy;
  status->cw_auto_progress = st->auto_progress;
  status->cw_auto_done = st->auto_done_pulse;
  status->cw_auto_found_hz = st->auto_found_hz;
}

void nr_cw_process(NrCwState *st, const NrConfig *cfg,
                   float *out, const float *in, int n, float fs) {
  float f0;
  float tone_thr = cfg->cw_goertzel_thr;
  float mu = cfg->cw_ale_mu;
  int taps = cfg->cw_ale_taps;
  int delay_n = cfg->cw_ale_delay;
  float buf[480];
  int i, t;
  float tone_mag;
  float gate_open;
  const int delay_cap = NR_CW_DELAY_BUF;

  if (n > 480) n = 480;
  if (taps < 2) taps = 2;
  if (taps > NR_CW_ALE_MAX_TAPS) taps = NR_CW_ALE_MAX_TAPS;
  if (delay_n < 1) delay_n = 1;
  if (delay_n > 48) delay_n = 48;
  if (mu < 1e-6f) mu = 1e-6f;
  if (mu > 0.2f) mu = 0.2f;
  if (tone_thr < 1.f) tone_thr = 1.f;

  /* Peak find / AFC on raw input first */
  nr_cw_run_auto_afc(st, cfg, in, n, fs);
  f0 = st->track_hz;
  nr_cw_update_filters(st, cfg, fs, f0);

  memcpy(buf, in, sizeof(float) * (size_t)n);

  if (cfg->cw_enable_bpf) {
    for (i = 0; i < n; ++i) {
      float y = nr_biquad_process(&st->bpf1, buf[i]);
      buf[i] = nr_biquad_process(&st->bpf2, y);
    }
  }

  if (cfg->cw_enable_ale) {
    for (i = 0; i < n; ++i) {
      float y = 0.f;
      float e;
      float xin = buf[i];
      float norm = 1e-3f;
      int dpos = st->ale_pos - delay_n;
      if (dpos < 0) dpos += delay_cap;
      for (t = 0; t < taps; ++t) {
        int idx = dpos - t;
        if (idx < 0) idx += delay_cap;
        y += st->ale_w[t] * st->ale_delay[idx];
        norm += st->ale_delay[idx] * st->ale_delay[idx];
      }
      e = xin - y;
      for (t = 0; t < taps; ++t) {
        int idx = dpos - t;
        if (idx < 0) idx += delay_cap;
        st->ale_w[t] += (mu * e * st->ale_delay[idx]) / norm;
        st->ale_w[t] = nr_clampf(st->ale_w[t], -2.f, 2.f);
      }
      st->ale_delay[st->ale_pos] = xin;
      st->ale_pos++;
      if (st->ale_pos >= delay_cap) st->ale_pos = 0;
      buf[i] = 0.70f * y + 0.30f * xin;
    }
  }

  if (cfg->cw_enable_apf) {
    for (i = 0; i < n; ++i) buf[i] = nr_biquad_process(&st->apf, buf[i]);
  }

  tone_mag = nr_cw_goertzel_mag(buf, n, fs, f0);
  st->tone_mag = 0.8f * st->tone_mag + 0.2f * tone_mag;
  {
    float p = st->tone_mag / (tone_thr + 1e-6f);
    if (p > 1.f) p = 1.f;
    st->tone_prob = 0.7f * st->tone_prob + 0.3f * p;
  }
  gate_open = cfg->cw_enable_goertzel ? st->tone_prob : 1.f;

  if (cfg->cw_enable_gate) {
    float atk = nr_alpha_ms(cfg->cw_gate_attack_ms, fs);
    float rel = nr_alpha_ms(cfg->cw_gate_release_ms, fs);
    float thr = cfg->cw_gate_threshold;
    if (thr < 1.f) thr = 1.f;
    for (i = 0; i < n; ++i) {
      float ax = fabsf(buf[i]);
      if (ax > st->env) st->env += atk * (ax - st->env);
      else st->env += rel * (ax - st->env);
      {
        float open = ((st->env > thr) ? 1.f : 0.f) * gate_open;
        float galpha = (open > st->gate_gain) ? atk : rel;
        st->gate_gain += galpha * (open - st->gate_gain);
        buf[i] *= (st->gate_gain < 0.02f) ? 0.f : st->gate_gain;
      }
    }
  } else if (cfg->cw_enable_goertzel) {
    for (i = 0; i < n; ++i) buf[i] *= gate_open;
  }

  if (cfg->cw_enable_regen) {
    float mix = nr_clampf(cfg->cw_regen_mix, 0.f, 1.f);
    float w = 2.f * (float)M_PI * f0 / fs;
    float atk = nr_alpha_ms(3.f, fs);
    float rel = nr_alpha_ms(20.f, fs);
    for (i = 0; i < n; ++i) {
      float ax = fabsf(buf[i]);
      if (ax > st->regen_env) st->regen_env += atk * (ax - st->regen_env);
      else st->regen_env += rel * (ax - st->regen_env);
      {
        float synth = st->regen_env * sinf(st->nco_phase);
        float m = mix * st->tone_prob;
        buf[i] = (1.f - m) * buf[i] + m * synth;
        st->nco_phase += w;
        if (st->nco_phase > 2.f * (float)M_PI) st->nco_phase -= 2.f * (float)M_PI;
      }
    }
  }

  memcpy(out, buf, sizeof(float) * (size_t)n);
}
