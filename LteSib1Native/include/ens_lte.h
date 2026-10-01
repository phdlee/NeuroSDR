/**
 * ens_lte.h — ENSdr LTE broadcast decode (PSS/SSS → MIB → SIB1/PLMN)
 *
 * Built from srsRAN_4G PHY + RRC ASN.1 (AGPL-3.0). See LICENSE / README.
 */
#pragma once

#include <stdint.h>

#ifdef _WIN32
#  ifdef ENS_LTE_EXPORTS
#    define ENS_LTE_API __declspec(dllexport)
#  else
#    define ENS_LTE_API __declspec(dllimport)
#  endif
#else
#  define ENS_LTE_API __attribute__((visibility("default")))
#endif

#ifdef __cplusplus
extern "C" {
#endif

#define ENS_LTE_MAX_CELLS 16
#define ENS_LTE_MAX_PLMN 6

typedef struct ens_lte_plmn {
  char mcc[4];
  char mnc[4];
} ens_lte_plmn_t;

typedef struct ens_lte_cell {
  int32_t        pci;
  int32_t        earfcn;
  double         freq_mhz;
  int32_t        nof_prb;
  int32_t        nof_ports;
  int32_t        extended_cp;
  float          pss_psr;
  float          pss_power_db;
  float          cfo_hz;
  int32_t        sfn;
  char           tac[8];
  char           cell_id[12];
  int32_t        plmn_count;
  ens_lte_plmn_t plmn[ENS_LTE_MAX_PLMN];
  int32_t        stage; /* 1=SYNC 2=MIB 3=SIB1 */
  int32_t        hits;
} ens_lte_cell_t;

typedef struct ens_lte_decoder ens_lte_decoder_t;

ENS_LTE_API ens_lte_decoder_t* ens_lte_create(void);
ENS_LTE_API void               ens_lte_destroy(ens_lte_decoder_t* dec);
ENS_LTE_API void               ens_lte_reset(ens_lte_decoder_t* dec);

/** Optional: managed PSS/SSS PCI (+ CFO Hz) so native can run ue_mib_sync. */
ENS_LTE_API int ens_lte_hint_cell(ens_lte_decoder_t* dec, int32_t pci, int32_t extended_cp, float cfo_hz);

ENS_LTE_API int ens_lte_process_iq(ens_lte_decoder_t* dec,
                                   const float*      iq_interleaved,
                                   int32_t           num_complex_samples,
                                   int32_t           sample_rate_hz,
                                   int64_t           center_hz);

ENS_LTE_API int         ens_lte_cell_count(ens_lte_decoder_t* dec);
ENS_LTE_API int         ens_lte_get_cell(ens_lte_decoder_t* dec, int32_t index, ens_lte_cell_t* out);
ENS_LTE_API const char* ens_lte_status(ens_lte_decoder_t* dec);
ENS_LTE_API const char* ens_lte_version(void);

#ifdef __cplusplus
}
#endif
