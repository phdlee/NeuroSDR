#include "nr_biquad.h"

#include <math.h>

#ifndef M_PI
#define M_PI 3.14159265358979323846
#endif

void nr_biquad_clear(NrBiquad *bq) {
  bq->z1 = 0.f;
  bq->z2 = 0.f;
}

static void nr_biquad_normalize(NrBiquad *bq, float a0) {
  bq->b0 /= a0;
  bq->b1 /= a0;
  bq->b2 /= a0;
  bq->a1 /= a0;
  bq->a2 /= a0;
}

void nr_biquad_set_lowpass(NrBiquad *bq, float fs, float f0, float q) {
  float w0 = 2.f * (float)M_PI * f0 / fs;
  float alpha = sinf(w0) / (2.f * q);
  float cosw = cosf(w0);
  float a0 = 1.f + alpha;
  bq->b0 = (1.f - cosw) * 0.5f;
  bq->b1 = 1.f - cosw;
  bq->b2 = (1.f - cosw) * 0.5f;
  bq->a1 = -2.f * cosw;
  bq->a2 = 1.f - alpha;
  nr_biquad_normalize(bq, a0);
}

void nr_biquad_set_highpass(NrBiquad *bq, float fs, float f0, float q) {
  float w0 = 2.f * (float)M_PI * f0 / fs;
  float alpha = sinf(w0) / (2.f * q);
  float cosw = cosf(w0);
  float a0 = 1.f + alpha;
  bq->b0 = (1.f + cosw) * 0.5f;
  bq->b1 = -(1.f + cosw);
  bq->b2 = (1.f + cosw) * 0.5f;
  bq->a1 = -2.f * cosw;
  bq->a2 = 1.f - alpha;
  nr_biquad_normalize(bq, a0);
}

void nr_biquad_set_bandpass(NrBiquad *bq, float fs, float f0, float q) {
  float w0 = 2.f * (float)M_PI * f0 / fs;
  float alpha = sinf(w0) / (2.f * q);
  float cosw = cosf(w0);
  float a0 = 1.f + alpha;
  bq->b0 = alpha;
  bq->b1 = 0.f;
  bq->b2 = -alpha;
  bq->a1 = -2.f * cosw;
  bq->a2 = 1.f - alpha;
  nr_biquad_normalize(bq, a0);
}

void nr_biquad_set_peaking(NrBiquad *bq, float fs, float f0, float q, float gain_db) {
  float A = powf(10.f, gain_db / 40.f);
  float w0 = 2.f * (float)M_PI * f0 / fs;
  float alpha = sinf(w0) / (2.f * q);
  float cosw = cosf(w0);
  float a0 = 1.f + alpha / A;
  bq->b0 = 1.f + alpha * A;
  bq->b1 = -2.f * cosw;
  bq->b2 = 1.f - alpha * A;
  bq->a1 = -2.f * cosw;
  bq->a2 = 1.f - alpha / A;
  nr_biquad_normalize(bq, a0);
}

void nr_biquad_set_notch(NrBiquad *bq, float fs, float f0, float q) {
  float w0 = 2.f * (float)M_PI * f0 / fs;
  float alpha = sinf(w0) / (2.f * q);
  float cosw = cosf(w0);
  float a0 = 1.f + alpha;
  bq->b0 = 1.f;
  bq->b1 = -2.f * cosw;
  bq->b2 = 1.f;
  bq->a1 = -2.f * cosw;
  bq->a2 = 1.f - alpha;
  nr_biquad_normalize(bq, a0);
}

float nr_biquad_process(NrBiquad *bq, float x) {
  float y = bq->b0 * x + bq->z1;
  bq->z1 = bq->b1 * x - bq->a1 * y + bq->z2;
  bq->z2 = bq->b2 * x - bq->a2 * y;
  return y;
}
