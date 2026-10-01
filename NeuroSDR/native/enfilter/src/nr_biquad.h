#ifndef NR_BIQUAD_H
#define NR_BIQUAD_H

typedef struct NrBiquad {
  float b0, b1, b2, a1, a2;
  float z1, z2;
} NrBiquad;

void nr_biquad_clear(NrBiquad *bq);
void nr_biquad_set_peaking(NrBiquad *bq, float fs, float f0, float q, float gain_db);
void nr_biquad_set_lowpass(NrBiquad *bq, float fs, float f0, float q);
void nr_biquad_set_highpass(NrBiquad *bq, float fs, float f0, float q);
void nr_biquad_set_bandpass(NrBiquad *bq, float fs, float f0, float q);
void nr_biquad_set_notch(NrBiquad *bq, float fs, float f0, float q);
float nr_biquad_process(NrBiquad *bq, float x);

#endif
