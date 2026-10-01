#include "nr_stages.h"

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

static float nr_db(float p) {
  if (p < 1e-20f) p = 1e-20f;
  return 10.f * log10f(p);
}

static float nr_alpha_from_ms(float ms, float fs) {
  if (ms < 0.1f) ms = 0.1f;
  return 1.f - expf(-1.f / (0.001f * ms * fs));
}

float nr_frame_rms(const float *x, int n) {
  double acc = 0.0;
  int i;
  for (i = 0; i < n; ++i) acc += (double)x[i] * (double)x[i];
  return (float)sqrt(acc / (double)n);
}

/* -------- Stage 1: Impulse blanker -------- */

void nr_blanker_reset(NrBlankerState *st) {
  memset(st, 0, sizeof(*st));
  st->avg_abs = 100.f;
}

void nr_blanker_process(NrBlankerState *st, const NrConfig *cfg, float *x, int n) {
  float alpha = nr_alpha_from_ms(cfg->blanker_avg_ms, 48000.f);
  float thr = cfg->blanker_threshold;
  int hold_n = cfg->blanker_hold_samples;
  int i;
  int hits = 0;

  if (thr < 1.5f) thr = 1.5f;
  if (hold_n < 1) hold_n = 1;
  if (hold_n > 64) hold_n = 64;

  for (i = 0; i < n; ++i) {
    float ax = fabsf(x[i]);
    st->avg_abs += alpha * (ax - st->avg_abs);
    if (st->avg_abs < 1.f) st->avg_abs = 1.f;

    if (st->hold > 0) {
      x[i] = st->prev;
      st->hold--;
      hits++;
    } else if (ax > thr * st->avg_abs) {
      x[i] = st->prev;
      st->hold = hold_n;
      hits++;
    } else {
      st->prev = x[i];
    }
  }
  st->hits = hits;
}

/* -------- Stage 2: Auto notch (interferer only; skipped for CW) -------- */

void nr_notch_reset(NrNotchState *st) {
  memset(st, 0, sizeof(*st));
  st->freq_hz = 1000.f;
  st->noise_level = 1.f;
}

void nr_notch_process(NrNotchState *st, const NrConfig *cfg, float *x, int n,
                      float fs, int skip_notch) {
  float mu = cfg->notch_mu;
  float alpha = cfg->notch_adapt_alpha;
  float bw = cfg->notch_bw_hz;
  int i;
  float energy = 0.f;
  float tone_f;
  float tone_snr;

  if (skip_notch) {
    st->notch_valid = 0;
    return;
  }

  if (mu < 1e-5f) mu = 1e-5f;
  if (mu > 0.2f) mu = 0.2f;
  if (alpha < 0.01f) alpha = 0.01f;
  if (alpha > 1.f) alpha = 1.f;
  if (bw < 10.f) bw = 10.f;

  /* Light ALE for tracking only; output stays mostly dry until IIR notch engages. */
  for (i = 0; i < n; ++i) {
    float xd1 = st->x_d1;
    float xd2 = st->x_d2;
    float y = st->w[0] * xd1 + st->w[1] * xd2;
    float e = x[i] - y;
    float scale = 1.f / (1.f + xd1 * xd1 + xd2 * xd2);
    st->w[0] += mu * e * xd1 * scale * 1e-3f;
    st->w[1] += mu * e * xd2 * scale * 1e-3f;
    st->w[0] = nr_clampf(st->w[0], -1.5f, 1.5f);
    st->w[1] = nr_clampf(st->w[1], -1.5f, 1.5f);
    st->x_d2 = xd1;
    st->x_d1 = x[i];
    energy += x[i] * x[i];
  }

  {
    float best_mag = 0.f;
    float best_f = st->freq_hz;
    int k;
    for (k = -4; k <= 4; ++k) {
      float f = st->freq_hz + (float)k * 25.f;
      float w0, re = 0.f, im = 0.f;
      int j;
      if (f < 80.f) f = 80.f;
      if (f > fs * 0.45f) f = fs * 0.45f;
      w0 = 2.f * (float)M_PI * f / fs;
      {
        float c = cosf(w0), s = sinf(w0);
        float rc = 1.f, rs = 0.f;
        for (j = 0; j < n; ++j) {
          float nrc = rc * c - rs * s;
          float nrs = rc * s + rs * c;
          rc = nrc;
          rs = nrs;
          re += x[j] * rc;
          im += x[j] * rs;
        }
      }
      {
        float mag = re * re + im * im;
        if (mag > best_mag) {
          best_mag = mag;
          best_f = f;
        }
      }
    }
    tone_f = best_f;
    st->tone_level = (1.f - alpha) * st->tone_level + alpha * (best_mag / (float)(n * n) + 1e-6f);
    st->noise_level = (1.f - alpha) * st->noise_level + alpha * (energy / (float)n + 1e-6f);
    st->freq_hz = (1.f - alpha) * st->freq_hz + alpha * tone_f;
  }

  tone_snr = nr_db(st->tone_level + 1e-12f) - nr_db(st->noise_level + 1e-12f);
  if (tone_snr >= cfg->notch_min_tone_snr_db) {
    float q = st->freq_hz / bw;
    if (q < 2.f) q = 2.f;
    if (q > 80.f) q = 80.f;
    nr_biquad_set_notch(&st->notch, fs, st->freq_hz, q);
    st->notch_valid = 1;
    for (i = 0; i < n; ++i) {
      x[i] = nr_biquad_process(&st->notch, x[i]);
    }
  } else {
    st->notch_valid = 0;
  }
}

/* -------- Stage 3: SNR / VAD classifier (voice path only) -------- */

void nr_classifier_reset(NrClassifierState *st) {
  memset(st, 0, sizeof(*st));
  st->noise_power = 1e6f;
  st->signal_power = 1e6f;
  st->blend_ai = 1.f;
}

void nr_classifier_analyze(NrClassifierState *st, const NrConfig *cfg,
                           const float *x, int n, float fs) {
  float power = 0.f;
  float alpha = nr_alpha_from_ms(cfg->snr_noise_tau_ms, fs);
  int i;

  (void)fs;
  for (i = 0; i < n; ++i) power += x[i] * x[i];
  power /= (float)n;
  st->signal_power = (1.f - alpha) * st->signal_power + alpha * power;

  if (st->vad_prob < cfg->vad_threshold) {
    st->noise_power = (1.f - alpha) * st->noise_power + alpha * power;
  }

  st->snr_db = nr_db(st->signal_power + 1e-12f) - nr_db(st->noise_power + 1e-12f);

  {
    float blend = 1.f;
    float smooth = cfg->blend_smooth;
    if (smooth < 0.05f) smooth = 0.05f;
    if (smooth > 1.f) smooth = 1.f;

    if (cfg->blend_mode == NR_BLEND_MODE_FIXED) {
      blend = cfg->blend_fixed_ai;
    } else if (cfg->blend_mode == NR_BLEND_MODE_VAD) {
      float lo = cfg->blend_vad_low;
      float hi = cfg->blend_vad_high;
      float v = st->vad_prob;
      if (hi < lo + 0.05f) hi = lo + 0.05f;
      if (v <= lo) blend = 0.f;
      else if (v >= hi) blend = 1.f;
      else blend = (v - lo) / (hi - lo);
    } else {
      if (st->snr_db <= cfg->snr_weak_db) {
        blend = 0.15f;
      } else if (st->snr_db >= cfg->snr_strong_db) {
        blend = 1.f;
      } else {
        blend = (st->snr_db - cfg->snr_weak_db) /
                (cfg->snr_strong_db - cfg->snr_weak_db + 1e-6f);
        blend = 0.15f + 0.85f * blend;
      }
    }
    st->blend_ai = (1.f - smooth) * st->blend_ai + smooth * nr_clampf(blend, 0.f, 1.f);
  }
}

void nr_classifier_update_vad(NrClassifierState *st, const NrConfig *cfg,
                              float rnnoise_vad) {
  if (rnnoise_vad >= 0.f) {
    st->vad_prob = rnnoise_vad;
  } else {
    st->vad_prob = (st->signal_power > st->noise_power * 2.5f) ? 1.f : 0.f;
  }
  (void)cfg;
}

/* -------- Stage 4B: soft Wiener (voice weak-signal path) -------- */

void nr_wiener_reset(NrWienerState *st) {
  int i;
  memset(st, 0, sizeof(*st));
  st->noise_power = 1e6f;
  st->signal_power = 1e6f;
  for (i = 0; i < NR_CW_BANDS; ++i) {
    st->band_noise[i] = 1e6f;
    st->band_signal[i] = 1e6f;
  }
}

void nr_wiener_process(NrWienerState *st, const NrConfig *cfg,
                       float *out, const float *in, int n, float fs) {
  float strength = nr_clampf(cfg->wiener_strength, 0.f, 1.f);
  float floor_lin = powf(10.f, cfg->wiener_noise_floor_db / 20.f);
  float power = 0.f;
  int i, b;

  if (floor_lin < 0.15f) floor_lin = 0.15f;
  if (floor_lin > 1.f) floor_lin = 1.f;

  for (i = 0; i < n; ++i) power += in[i] * in[i];
  power /= (float)n;
  st->signal_power = 0.9f * st->signal_power + 0.1f * power;

  for (b = 0; b < NR_CW_BANDS; ++b) {
    float f = 80.f + (3000.f - 80.f) * ((float)b / (float)(NR_CW_BANDS - 1));
    float w0 = 2.f * (float)M_PI * f / fs;
    float c = cosf(w0), s = sinf(w0);
    float rc = 1.f, rs = 0.f, re = 0.f, im = 0.f;
    float mag;
    for (i = 0; i < n; ++i) {
      float nrc = rc * c - rs * s;
      float nrs = rc * s + rs * c;
      rc = nrc;
      rs = nrs;
      re += in[i] * rc;
      im += in[i] * rs;
    }
    mag = (re * re + im * im) / (float)(n * n) + 1e-6f;
    st->band_signal[b] = 0.8f * st->band_signal[b] + 0.2f * mag;
    if (mag < st->signal_power * 0.5f) {
      st->band_noise[b] = 0.9f * st->band_noise[b] + 0.1f * mag;
    }
  }

  {
    float noise = 0.f;
    float sig = 0.f;
    float snr, wgain;
    for (b = 0; b < NR_CW_BANDS; ++b) {
      noise += st->band_noise[b];
      sig += st->band_signal[b];
    }
    noise /= (float)NR_CW_BANDS;
    sig /= (float)NR_CW_BANDS;
    st->noise_power = 0.9f * st->noise_power + 0.1f * noise;
    snr = (sig + 1e-9f) / (noise + 1e-9f);
    wgain = snr / (snr + 1.f);
    wgain = floor_lin + (1.f - floor_lin) * wgain;
    wgain = 1.f - strength * (1.f - wgain);
    for (i = 0; i < n; ++i) out[i] = in[i] * wgain;
  }
}

/* -------- Stage 5: AGC + EQ -------- */

void nr_agc_eq_reset(NrAgcEqState *st) {
  memset(st, 0, sizeof(*st));
  st->gain = 1.f;
  st->env = 1000.f;
  st->eq_ready = 0;
}

void nr_agc_eq_prepare(NrAgcEqState *st, const NrConfig *cfg, float fs) {
  nr_biquad_set_highpass(&st->hpf, fs, cfg->eq_low_cut_hz, 0.707f);
  nr_biquad_set_lowpass(&st->lpf, fs, cfg->eq_high_cut_hz, 0.707f);
  nr_biquad_set_peaking(&st->presence, fs, cfg->eq_presence_hz, 1.0f, cfg->eq_presence_gain_db);
  nr_biquad_clear(&st->hpf);
  nr_biquad_clear(&st->lpf);
  nr_biquad_clear(&st->presence);
  st->eq_ready = 1;
}

void nr_agc_eq_process(NrAgcEqState *st, const NrConfig *cfg, float *x, int n, float fs) {
  float attack = nr_alpha_from_ms(cfg->agc_attack_ms, fs);
  float decay = nr_alpha_from_ms(cfg->agc_decay_ms, fs);
  float target = powf(10.f, cfg->agc_target_db / 20.f) * 32768.f;
  float max_gain = powf(10.f, cfg->agc_max_gain_db / 20.f);
  int i;

  if (!st->eq_ready) {
    nr_agc_eq_prepare(st, cfg, fs);
  }

  if (cfg->enable_eq) {
    for (i = 0; i < n; ++i) {
      float y = nr_biquad_process(&st->hpf, x[i]);
      y = nr_biquad_process(&st->presence, y);
      y = nr_biquad_process(&st->lpf, y);
      x[i] = y;
    }
  }

  if (cfg->enable_agc) {
    for (i = 0; i < n; ++i) {
      float ax = fabsf(x[i]);
      if (ax > st->env) st->env += attack * (ax - st->env);
      else st->env += decay * (ax - st->env);
      if (st->env < 1.f) st->env = 1.f;
      {
        float desired = target / st->env;
        if (desired > max_gain) desired = max_gain;
        st->gain += 0.05f * (desired - st->gain);
        x[i] *= st->gain;
      }
    }
  }
}
