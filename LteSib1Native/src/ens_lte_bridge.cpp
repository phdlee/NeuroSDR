/**
 * ens_lte_bridge.cpp — IQ → cellsearch|hint → ue_mib_sync (MIB) → SIB1
 *
 * MIB always at 1.92 MS/s (center 6 PRB). SIB1 needs sample rate ≥
 * srsran_sampling_freq_hz(nof_prb) from the host SDR capture.
 *
 * AGPL-3.0 (via srsRAN).
 */

#include "ens_lte.h"

#include <atomic>
#include <chrono>
#include <cmath>
#include <condition_variable>
#include <cstdio>
#include <cstring>
#include <mutex>
#include <string>
#include <thread>
#include <vector>

extern "C" {
#include <complex.h>
}

#include "srsran/srsran.h"
#include "srsran/asn1/rrc/bcch_msg.h"
#include "srsran/asn1/rrc/si.h"
#include "srsran/asn1/rrc_utils.h"
#include "srsran/interfaces/rrc_interface_types.h"

namespace {

constexpr int SEARCH_RATE     = 1920000;
constexpr int MAX_PRB         = 100;
constexpr int CS_MAX_FRAMES   = 50;
constexpr int CS_VALID_FRAMES = 8;
constexpr int MIB_MAX_FRAMES  = 500;
constexpr float CS_MIN_PSR    = 2.2f;

struct Ring {
  std::vector<cf_t>       buf;
  size_t                  w = 0, r = 0, count = 0;
  std::mutex              mu;
  std::condition_variable cv;
  std::atomic<bool>*      stop = nullptr;

  explicit Ring(size_t n) : buf(n) {}

  void clear()
  {
    std::lock_guard<std::mutex> lock(mu);
    w = r = count = 0;
  }

  void push(const cf_t* samples, size_t n)
  {
    {
      std::lock_guard<std::mutex> lock(mu);
      for (size_t i = 0; i < n; i++) {
        buf[w] = samples[i];
        w      = (w + 1) % buf.size();
        if (count < buf.size())
          count++;
        else
          r = (r + 1) % buf.size();
      }
    }
    cv.notify_all();
  }

  int pop_blocking(cf_t* out, uint32_t n)
  {
    std::unique_lock<std::mutex> lock(mu);
    while (count < n) {
      if (stop && stop->load())
        return -1;
      cv.wait_for(lock, std::chrono::milliseconds(50));
      if (stop && stop->load())
        return -1;
    }
    for (uint32_t i = 0; i < n; i++) {
      out[i] = buf[r];
      r      = (r + 1) % buf.size();
      count--;
    }
    return (int)n;
  }
};

int recv_cb(void* h, cf_t* data[SRSRAN_MAX_CHANNELS], uint32_t nsamples, srsran_timestamp_t* /*t*/)
{
  return static_cast<Ring*>(h)->pop_blocking(data[0], nsamples);
}

void format_plmn(const asn1::rrc::plmn_id_s& id, ens_lte_plmn_t* out)
{
  std::memset(out, 0, sizeof(*out));
  srsran::plmn_id_t p = srsran::make_plmn_id_t(id);
  std::string       s = p.to_string();
  if (s.size() >= 3) {
    std::memcpy(out->mcc, s.data(), 3);
    out->mcc[3] = '\0';
    std::snprintf(out->mnc, sizeof(out->mnc), "%s", s.c_str() + 3);
  }
}

bool unpack_sib1(const uint8_t* payload, uint32_t len, ens_lte_cell_t* cell)
{
  asn1::rrc::bcch_dl_sch_msg_s msg;
  asn1::cbit_ref               bref(payload, len);
  if (msg.unpack(bref) != asn1::SRSASN_SUCCESS)
    return false;
  if (msg.msg.type().value != asn1::rrc::bcch_dl_sch_msg_type_c::types_opts::c1)
    return false;
  auto& c1 = msg.msg.c1();
  if (c1.type().value != asn1::rrc::bcch_dl_sch_msg_type_c::c1_c_::types_opts::sib_type1)
    return false;
  const auto& access = c1.sib_type1().cell_access_related_info;
  cell->plmn_count   = 0;
  for (uint32_t i = 0; i < access.plmn_id_list.size() && cell->plmn_count < ENS_LTE_MAX_PLMN; i++) {
    format_plmn(access.plmn_id_list[i].plmn_id, &cell->plmn[cell->plmn_count]);
    cell->plmn_count++;
  }
  std::snprintf(cell->tac, sizeof(cell->tac), "%04llX", (unsigned long long)access.tac.to_number());
  std::snprintf(cell->cell_id, sizeof(cell->cell_id), "%07llX", (unsigned long long)access.cell_id.to_number());
  cell->stage = 3;
  return cell->plmn_count > 0;
}

} // namespace

struct ens_lte_decoder {
  struct Snapshot {
    int32_t       cell_count = 0;
    ens_lte_cell_t cells[ENS_LTE_MAX_CELLS]{};
    char          status[256]{};
  };

  Snapshot             result{};
  std::mutex           result_mu;
  Ring                 ring{SEARCH_RATE * 6};
  std::atomic<bool>    stop{false};
  std::atomic<bool>    reset_req{false};
  std::thread          worker;
  std::atomic<int>     last_rate{0};
  std::atomic<int64_t> last_center{0};
  std::atomic<int>     target_rate{SEARCH_RATE};
  double               resample_phase = 0; // fractional read index into stage
  std::mutex           resample_mu;
  std::vector<cf_t>    work;
  std::vector<cf_t>    stage; // resample input staging

  std::atomic<int>   hint_pci{-1};
  std::atomic<int>   hint_cp{0};
  std::atomic<float> hint_cfo{0};
  std::atomic<int>   hint_seq{0};
  int                applied_hint_seq = -1;

  bool                   have_cs = false;
  bool                   have_mib_sync = false;
  bool                   have_sync = false;
  bool                   have_dl = false;
  srsran_ue_cellsearch_t cs{};
  srsran_ue_mib_sync_t   mib_sync{};
  srsran_ue_sync_t       ue_sync{};
  srsran_ue_dl_t         ue_dl{};
  srsran_cell_t          cell{};
  cf_t*                  sf_buffer[SRSRAN_MAX_CHANNELS]{};
  uint8_t*               data_bytes[SRSRAN_MAX_CODEWORDS]{};
  enum { ST_SEARCH, ST_MIB, ST_SIB1, ST_MIB_DONE_WAIT_RATE } state = ST_SEARCH;
  uint32_t sfn             = 0;
  int      decode_attempts = 0;
  float    cell_cfo_hz     = 0;
  int      mib_attempt     = 0; // 0..3 then abandon
  int      blacklist_pci   = -1;
  int64_t  blacklist_until_ms = 0;
  // Sticky last successful MIB — re-lock after retune near same center.
  int      sticky_mib_pci  = -1;
  int      sticky_mib_cp   = 0; // 0 NORM 1 EXT
  int      sticky_mib_prb  = 0;
  int      sticky_mib_ports = 0;
  int64_t  sticky_mib_center = 0;
  int      sticky_fail_rounds = 0;
  std::atomic<bool> retune_req{false};
  // Autonomous srsRAN cellsearch when no managed hint (native-only / no hint yet).
  bool use_cellsearch = true;

  static int64_t now_ms()
  {
    return std::chrono::duration_cast<std::chrono::milliseconds>(
               std::chrono::steady_clock::now().time_since_epoch())
        .count();
  }

  ens_lte_decoder()
  {
    ring.stop = &stop;
    set_status("ens_lte ready (mib_sync)");
    worker = std::thread([this] { worker_main(); });
  }

  ~ens_lte_decoder()
  {
    stop.store(true);
    ring.cv.notify_all();
    if (worker.joinable())
      worker.join();
    free_phy();
  }

  void set_status(const char* s)
  {
    std::lock_guard<std::mutex> lock(result_mu);
    std::snprintf(result.status, sizeof(result.status), "%s", s);
  }

  ens_lte_cell_t* upsert_locked(int pci)
  {
    for (int i = 0; i < result.cell_count; i++)
      if (result.cells[i].pci == pci)
        return &result.cells[i];
    if (result.cell_count >= ENS_LTE_MAX_CELLS)
      return nullptr;
    auto* c = &result.cells[result.cell_count++];
    std::memset(c, 0, sizeof(*c));
    c->pci     = pci;
    c->nof_prb = 6;
    c->stage   = 1;
    c->earfcn  = -1;
    c->sfn     = -1;
    return c;
  }

  void free_phy()
  {
    if (have_dl) {
      srsran_ue_dl_free(&ue_dl);
      have_dl = false;
    }
    if (have_sync) {
      srsran_ue_sync_free(&ue_sync);
      have_sync = false;
    }
    if (have_mib_sync) {
      srsran_ue_mib_sync_free(&mib_sync);
      have_mib_sync = false;
    }
    if (have_cs) {
      srsran_ue_cellsearch_free(&cs);
      have_cs = false;
    }
    for (auto& p : sf_buffer) {
      free(p);
      p = nullptr;
    }
    for (auto& p : data_bytes) {
      free(p);
      p = nullptr;
    }
  }

  bool ensure_sf_buffers()
  {
    for (int i = 0; i < SRSRAN_MAX_CHANNELS; i++) {
      if (!sf_buffer[i]) {
        sf_buffer[i] = srsran_vec_cf_malloc(3 * SRSRAN_SF_LEN_PRB(MAX_PRB));
        if (!sf_buffer[i])
          return false;
      }
    }
    for (int i = 0; i < SRSRAN_MAX_CODEWORDS; i++) {
      if (!data_bytes[i]) {
        data_bytes[i] = srsran_vec_u8_malloc(SRSRAN_MAX_BUFFER_SIZE_BITS);
        if (!data_bytes[i])
          return false;
      }
    }
    return true;
  }

  void publish_sync_cell()
  {
    std::lock_guard<std::mutex> lock(result_mu);
    auto*                       out = upsert_locked((int)cell.id);
    if (!out)
      return;
    out->extended_cp = (cell.cp == SRSRAN_CP_EXT) ? 1 : 0;
    out->cfo_hz      = cell_cfo_hz;
    // Never downgrade MIB/SIB1 rows back to SYNC.
    if (out->stage < 1)
      out->stage = 1;
    out->freq_mhz = last_center.load() / 1e6;
    out->hits++;
  }

  bool center_near_sticky() const
  {
    if (sticky_mib_pci < 0 || sticky_mib_center == 0)
      return false;
    int64_t c = last_center.load();
    int64_t d = c > sticky_mib_center ? c - sticky_mib_center : sticky_mib_center - c;
    return d <= 8000; // ±8 kHz
  }

  bool begin_sticky_mib_if_any()
  {
    if (!center_near_sticky() || sticky_fail_rounds >= 2)
      return false;
    cell             = {};
    cell.id          = (uint32_t)sticky_mib_pci;
    cell.cp          = sticky_mib_cp ? SRSRAN_CP_EXT : SRSRAN_CP_NORM;
    cell.nof_prb     = 6; // MIB always on center 6 PRB
    cell.nof_ports   = 0;
    cell.frame_type  = SRSRAN_FDD;
    mib_attempt      = 0;
    char buf[96];
    std::snprintf(buf, sizeof(buf), "re-lock sticky MIB PCI %d…", sticky_mib_pci);
    set_status(buf);
    return begin_mib(/*apply_cfo=*/false);
  }

  bool begin_mib(bool apply_cfo)
  {
    free_phy();
    target_rate.store(SEARCH_RATE);
    ring.clear();
    {
      std::lock_guard<std::mutex> lock(resample_mu);
      resample_phase = 0;
      stage.clear();
    }
    if (srsran_ue_mib_sync_init_multi(&mib_sync, recv_cb, 1, &ring)) {
      set_status("ue_mib_sync init failed");
      return false;
    }
    have_mib_sync = true;

    srsran_cell_t mib_cell = cell;
    mib_cell.nof_prb       = SRSRAN_UE_MIB_NOF_PRB;
    mib_cell.nof_ports     = 0; // blind
    if (srsran_ue_mib_sync_set_cell(&mib_sync, mib_cell)) {
      set_status("ue_mib_sync set_cell failed");
      return false;
    }
    // First attempts: let ue_sync acquire CFO itself — wrong seed often kills PBCH.
    if (apply_cfo && cell_cfo_hz != 0) {
      mib_sync.ue_sync.cfo_current_value       = cell_cfo_hz / 15000.f;
      mib_sync.ue_sync.cfo_is_copied           = true;
      mib_sync.ue_sync.cfo_correct_enable_find = true;
      srsran_sync_set_cfo_cp_enable(&mib_sync.ue_sync.sfind, false, 0);
    }
    publish_sync_cell();
    state = ST_MIB;
    char buf[128];
    std::snprintf(buf,
                  sizeof(buf),
                  "PCI %d · MIB try %d CP %s%s",
                  (int)cell.id,
                  mib_attempt + 1,
                  cell.cp == SRSRAN_CP_EXT ? "EXT" : "NORM",
                  apply_cfo ? " +CFO" : "");
    set_status(buf);
    return true;
  }

  void abandon_mib(const char* why)
  {
    char buf[160];
    std::snprintf(buf, sizeof(buf), "PCI %d · %s · re-search", (int)cell.id, why);
    set_status(buf);
    blacklist_pci      = (int)cell.id;
    blacklist_until_ms = now_ms() + 5000;
    if ((int)cell.id == sticky_mib_pci)
      sticky_fail_rounds++;
    mib_attempt = 0;
    free_phy();
    state = ST_SEARCH;
  }

  void try_apply_hint()
  {
    // Do not interrupt a decoded MIB waiting for rate, or active SIB1.
    if (state == ST_MIB_DONE_WAIT_RATE || state == ST_SIB1)
      return;
    int seq = hint_seq.load();
    if (seq == applied_hint_seq)
      return;
    int pci = hint_pci.load();
    if (pci < 0 || pci > 503)
      return;
    if (pci == blacklist_pci && now_ms() < blacklist_until_ms)
      return;
    // Prefer sticky MIB PCI near this center over hopping to every managed ghost.
    if (center_near_sticky() && pci != sticky_mib_pci && state == ST_MIB)
      return;
    applied_hint_seq = seq;
    cell             = {};
    cell.id          = (uint32_t)pci;
    cell.cp          = hint_cp.load() ? SRSRAN_CP_EXT : SRSRAN_CP_NORM;
    cell.nof_prb     = 6;
    cell.nof_ports   = 0;
    cell.frame_type  = SRSRAN_FDD;
    cell_cfo_hz      = hint_cfo.load();
    mib_attempt      = 0;
    begin_mib(/*apply_cfo=*/false);
  }

  void do_search()
  {
    if (!use_cellsearch) {
      set_status("waiting managed PCI hint…");
      std::this_thread::sleep_for(std::chrono::milliseconds(100));
      return;
    }
    if (begin_sticky_mib_if_any())
      return;
    if (!have_cs) {
      if (srsran_ue_cellsearch_init_multi(&cs, CS_MAX_FRAMES, recv_cb, 1, &ring)) {
        set_status("cellsearch init failed");
        std::this_thread::sleep_for(std::chrono::milliseconds(200));
        return;
      }
      have_cs = true;
      srsran_ue_cellsearch_set_nof_valid_frames(&cs, CS_VALID_FRAMES);
      target_rate.store(SEARCH_RATE);
      set_status("native cellsearch…");
    }

    srsran_ue_cellsearch_result_t found[3]{};
    uint32_t                      max_n = 0;
    int                           n     = srsran_ue_cellsearch_scan(&cs, found, &max_n);
    if (stop.load() || reset_req.load())
      return;
    if (n < 0) {
      set_status("cellsearch error · retry");
      free_phy();
      return;
    }

    // Pick best non-blacklisted candidate by PSR.
    int   best     = -1;
    float best_psr = 0;
    const int64_t t = now_ms();
    for (int i = 0; i < 3; i++) {
      if (found[i].psr < CS_MIN_PSR)
        continue;
      int pci = (int)found[i].cell_id;
      if (pci == blacklist_pci && t < blacklist_until_ms)
        continue;
      if (found[i].psr > best_psr) {
        best_psr = found[i].psr;
        best     = i;
      }
    }
    if (best < 0) {
      set_status("native searching PSS/SSS…");
      return;
    }
    cell.id         = found[best].cell_id;
    cell.cp         = found[best].cp;
    cell.nof_prb    = 6;
    cell.nof_ports  = 0;
    cell.frame_type = found[best].frame_type;
    cell_cfo_hz     = found[best].cfo;
    {
      std::lock_guard<std::mutex> lock(result_mu);
      auto*                       out = upsert_locked((int)cell.id);
      if (out) {
        out->pss_psr      = found[best].psr;
        out->pss_power_db = 10.f * log10f(fmaxf(found[best].peak, 1e-20f));
        out->cfo_hz       = found[best].cfo;
        out->extended_cp  = (cell.cp == SRSRAN_CP_EXT) ? 1 : 0;
        if (out->stage < 1)
          out->stage = 1;
        out->nof_prb      = out->nof_prb > 0 ? out->nof_prb : 6;
        if (out->stage < 2)
          out->nof_ports = 0;
        out->hits++;
        out->freq_mhz = last_center.load() / 1e6;
      }
    }
    if (have_cs) {
      srsran_ue_cellsearch_free(&cs);
      have_cs = false;
    }
    mib_attempt = 0;
    begin_mib(/*apply_cfo=*/false);
  }

  void do_mib()
  {
    if (!have_mib_sync) {
      if (!begin_mib(mib_attempt >= 2))
        return;
    }
    uint8_t  bch[SRSRAN_BCH_PAYLOAD_LEN]{};
    uint32_t ports = 0;
    int      sfn_off = 0;

    int mib_ret = srsran_ue_mib_sync_decode(&mib_sync, MIB_MAX_FRAMES, bch, &ports, &sfn_off);
    if (stop.load() || reset_req.load())
      return;
    if (mib_ret < 0) {
      set_status("MIB sync recv error · retry");
      free_phy();
      state = ST_SEARCH;
      mib_attempt = 0;
      return;
    }
    if (mib_ret != SRSRAN_UE_MIB_FOUND) {
      mib_attempt++;
      if (mib_attempt >= 4) {
        abandon_mib("MIB not found");
        return;
      }
      // Cycle: flip CP, then flip again with CFO seed, then flip with CFO.
      cell.cp = (cell.cp == SRSRAN_CP_NORM) ? SRSRAN_CP_EXT : SRSRAN_CP_NORM;
      bool use_cfo = mib_attempt >= 2;
      char msg[128];
      std::snprintf(msg,
                    sizeof(msg),
                    "PCI %d · MIB miss · next try %d CP %s%s",
                    (int)cell.id,
                    mib_attempt + 1,
                    cell.cp == SRSRAN_CP_EXT ? "EXT" : "NORM",
                    use_cfo ? " +CFO" : "");
      set_status(msg);
      if (have_mib_sync) {
        srsran_ue_mib_sync_free(&mib_sync);
        have_mib_sync = false;
      }
      begin_mib(use_cfo);
      return;
    }

    srsran_pbch_mib_unpack(bch, &cell, &sfn);
    cell.nof_ports = ports ? ports : 1;
    sfn            = (sfn + sfn_off) % 1024;
    mib_attempt    = 0;
    sticky_mib_pci    = (int)cell.id;
    sticky_mib_cp     = (cell.cp == SRSRAN_CP_EXT) ? 1 : 0;
    sticky_mib_prb    = cell.nof_prb;
    sticky_mib_ports  = cell.nof_ports;
    sticky_mib_center = last_center.load();
    sticky_fail_rounds = 0;
    {
      std::lock_guard<std::mutex> lock(result_mu);
      auto*                       out = upsert_locked((int)cell.id);
      if (out) {
        out->nof_prb     = cell.nof_prb;
        out->nof_ports   = cell.nof_ports;
        out->extended_cp = (cell.cp == SRSRAN_CP_EXT) ? 1 : 0;
        out->sfn         = (int)sfn;
        out->stage       = 2;
        out->hits++;
        out->cfo_hz = cell_cfo_hz;
      }
    }

    int need = srsran_sampling_freq_hz(cell.nof_prb);
    int have = last_rate.load();
    if (cell.nof_prb > 6 && have < need) {
      char buf[160];
      std::snprintf(buf,
                    sizeof(buf),
                    "MIB OK %d PRB %d ports · need ≥%.2f MS/s for SIB1 (have %.2f)",
                    cell.nof_prb,
                    cell.nof_ports,
                    need / 1e6,
                    have / 1e6);
      set_status(buf);
      state = ST_MIB_DONE_WAIT_RATE;
      return;
    }

    enter_sib1();
  }

  void enter_sib1()
  {
    int need = srsran_sampling_freq_hz(cell.nof_prb);
    if (cell.nof_prb <= 6)
      need = SEARCH_RATE;
    target_rate.store(need);
    ring.clear();
    {
      std::lock_guard<std::mutex> lock(resample_mu);
      resample_phase = 0;
    }

    if (have_mib_sync) {
      srsran_ue_mib_sync_free(&mib_sync);
      have_mib_sync = false;
    }
    if (!ensure_sf_buffers()) {
      set_status("SIB1 buffer alloc failed");
      return;
    }
    if (srsran_ue_sync_init_multi(&ue_sync, cell.nof_prb, false, recv_cb, 1, &ring)) {
      set_status("SIB1 ue_sync init failed");
      return;
    }
    have_sync = true;
    srsran_ue_sync_set_cell(&ue_sync, cell);
    if (cell_cfo_hz != 0) {
      ue_sync.cfo_current_value = cell_cfo_hz / 15000.f;
      ue_sync.cfo_is_copied     = true;
    }
    if (srsran_ue_dl_init(&ue_dl, sf_buffer, cell.nof_prb, 1)) {
      set_status("SIB1 ue_dl init failed");
      return;
    }
    have_dl = true;
    srsran_ue_dl_set_cell(&ue_dl, cell);
    state           = ST_SIB1;
    decode_attempts = 0;
    char buf[128];
    std::snprintf(buf, sizeof(buf), "MIB OK %d PRB · SIB1 @ %.2f MS/s…", cell.nof_prb, need / 1e6);
    set_status(buf);
  }

  void do_wait_rate()
  {
    int need = srsran_sampling_freq_hz(cell.nof_prb);
    int have = last_rate.load();
    if (have >= need) {
      enter_sib1();
      return;
    }
    // Keep MIB row alive and status stable — do not fall back to search.
    {
      std::lock_guard<std::mutex> lock(result_mu);
      auto*                       out = upsert_locked((int)cell.id);
      if (out) {
        out->nof_prb     = cell.nof_prb;
        out->nof_ports   = cell.nof_ports;
        out->extended_cp = (cell.cp == SRSRAN_CP_EXT) ? 1 : 0;
        out->stage       = 2;
        out->hits++;
        out->freq_mhz    = last_center.load() / 1e6;
      }
    }
    char buf[192];
    std::snprintf(buf,
                  sizeof(buf),
                  "MIB OK PCI %d · %d PRB %d ports · MCC/MNC needs ≥%.2f MS/s (now %.2f) · stay on freq",
                  (int)cell.id,
                  cell.nof_prb,
                  cell.nof_ports,
                  need / 1e6,
                  have / 1e6);
    set_status(buf);
    std::this_thread::sleep_for(std::chrono::milliseconds(400));
  }

  void do_sib1()
  {
    int ret = srsran_ue_sync_zerocopy(&ue_sync, sf_buffer, 3 * SRSRAN_SF_LEN_PRB(MAX_PRB));
    if (stop.load() || reset_req.load() || ret < 0)
      return;
    if (ret != 1)
      return;
    uint32_t sf_idx = srsran_ue_sync_get_sfidx(&ue_sync);
    if (sf_idx == 0)
      sfn = (sfn + 1) % 1024;
    if (!(sf_idx == 5 && (sfn % 2) == 0))
      return;

    srsran_dl_sf_cfg_t dl_sf{};
    srsran_ue_dl_cfg_t ue_dl_cfg{};
    srsran_pdsch_cfg_t pdsch_cfg{};
    bool               acks[SRSRAN_MAX_CODEWORDS]{};
    dl_sf.tti      = sfn * 10 + sf_idx;
    dl_sf.sf_type  = SRSRAN_SF_NORM;
    pdsch_cfg.rnti = SRSRAN_SIRNTI;

    for (uint32_t tm = 0; tm < 4; tm++) {
      ue_dl_cfg.cfg.tm = (srsran_tm_t)tm;
      if ((tm == 0 && cell.nof_ports == 1) || (tm > 0 && cell.nof_ports > 1)) {
        int n = srsran_ue_dl_find_and_decode(&ue_dl, &dl_sf, &ue_dl_cfg, &pdsch_cfg, data_bytes, acks);
        if (n > 0 && acks[0]) {
          std::lock_guard<std::mutex> lock(result_mu);
          auto*                       out = upsert_locked((int)cell.id);
          if (out) {
            uint32_t tbs = pdsch_cfg.grant.tb[0].tbs;
            if (tbs > 0 && unpack_sib1(data_bytes[0], (tbs + 7) / 8, out)) {
              out->hits++;
              char buf[160];
              std::snprintf(buf,
                            sizeof(buf),
                            "SIB1 PLMN %s-%s TAC %s CID %s",
                            out->plmn[0].mcc,
                            out->plmn[0].mnc,
                            out->tac,
                            out->cell_id);
              std::snprintf(result.status, sizeof(result.status), "%s", buf);
            } else {
              std::snprintf(result.status, sizeof(result.status), "PDSCH OK · SIB1 ASN.1 fail");
            }
          }
          return;
        }
      }
    }
    decode_attempts++;
    if (decode_attempts % 50 == 0)
      set_status("waiting SIB1 (SI-RNTI SF5 even SFN)…");
  }

  void worker_main()
  {
    while (!stop.load()) {
      if (reset_req.exchange(false)) {
        free_phy();
        state            = ST_SEARCH;
        decode_attempts  = 0;
        mib_attempt      = 0;
        blacklist_pci    = -1;
        blacklist_until_ms = 0;
        // Keep sticky_mib_* so returning to the same freq can re-lock after CLEAR.
        sticky_fail_rounds = 0;
        applied_hint_seq = -1;
        target_rate.store(SEARCH_RATE);
        ring.clear();
        {
          std::lock_guard<std::mutex> lock(resample_mu);
          resample_phase = 0;
          stage.clear();
        }
        set_status("reset · searching");
      }
      if (retune_req.exchange(false)) {
        blacklist_pci      = -1;
        blacklist_until_ms = 0;
        if (state == ST_MIB_DONE_WAIT_RATE || state == ST_SIB1) {
          if (!center_near_sticky()) {
            free_phy();
            state       = ST_SEARCH;
            mib_attempt = 0;
            set_status("retune · searching");
          }
        } else if (state == ST_MIB) {
          free_phy();
          state       = ST_SEARCH;
          mib_attempt = 0;
          set_status("retune · searching");
        }
      }
      try_apply_hint();
      switch (state) {
        case ST_SEARCH:
          do_search();
          break;
        case ST_MIB:
          do_mib();
          break;
        case ST_MIB_DONE_WAIT_RATE:
          do_wait_rate();
          break;
        case ST_SIB1:
          do_sib1();
          break;
      }
    }
  }

  void push_resampled(const float* iq, int n, int rate)
  {
    const int dest = target_rate.load();
    std::lock_guard<std::mutex> lock(resample_mu);

    double pwr = 0;
    for (int i = 0; i < n; i++) {
      double I = iq[2 * i], Q = iq[2 * i + 1];
      pwr += I * I + Q * Q;
    }
    float gain = 1.f;
    if (n > 0) {
      float rms = (float)std::sqrt(pwr / (double)n);
      if (rms > 1e-9f && rms < 1e-3f)
        gain = 5e-2f / rms;
      else if (rms > 0.5f)
        gain = 0.15f / rms;
    }

    if (rate == dest) {
      work.resize(n);
      for (int i = 0; i < n; i++)
        work[i] = (gain * iq[2 * i]) + _Complex_I * (gain * iq[2 * i + 1]);
      ring.push(work.data(), n);
      return;
    }

    const size_t old = stage.size();
    stage.resize(old + (size_t)n);
    for (int i = 0; i < n; i++)
      stage[old + (size_t)i] = (gain * iq[2 * i]) + _Complex_I * (gain * iq[2 * i + 1]);

    const double step = (double)rate / (double)dest;
    work.clear();
    while (resample_phase + 1.0 < (double)stage.size()) {
      size_t i0 = (size_t)resample_phase;
      double f  = resample_phase - (double)i0;
      cf_t   a  = stage[i0];
      cf_t   b  = stage[i0 + 1];
      float  ar = crealf(a), ai = cimagf(a);
      float  br = crealf(b), bi = cimagf(b);
      work.push_back(((1.0 - f) * ar + f * br) + _Complex_I * ((1.0 - f) * ai + f * bi));
      resample_phase += step;
    }
    size_t consumed = (size_t)resample_phase;
    if (consumed > 0 && consumed <= stage.size()) {
      stage.erase(stage.begin(), stage.begin() + (std::ptrdiff_t)consumed);
      resample_phase -= (double)consumed;
    }
    if (!work.empty())
      ring.push(work.data(), work.size());
  }
};

extern "C" {

ENS_LTE_API ens_lte_decoder_t* ens_lte_create(void)
{
  try {
    return new ens_lte_decoder();
  } catch (...) {
    return nullptr;
  }
}

ENS_LTE_API void ens_lte_destroy(ens_lte_decoder_t* dec) { delete dec; }

ENS_LTE_API void ens_lte_reset(ens_lte_decoder_t* dec)
{
  if (!dec)
    return;
  dec->hint_pci.store(-1);
  dec->applied_hint_seq = -1;
  dec->hint_seq.store(0);
  dec->reset_req.store(true);
  dec->ring.cv.notify_all();
  std::lock_guard<std::mutex> lock(dec->result_mu);
  std::memset(&dec->result, 0, sizeof(dec->result));
  std::snprintf(dec->result.status, sizeof(dec->result.status), "reset");
}

ENS_LTE_API int ens_lte_hint_cell(ens_lte_decoder_t* dec, int32_t pci, int32_t extended_cp, float cfo_hz)
{
  if (!dec || pci < 0 || pci > 503)
    return -1;
  dec->hint_pci.store(pci);
  dec->hint_cp.store(extended_cp ? 1 : 0);
  dec->hint_cfo.store(cfo_hz);
  dec->hint_seq.fetch_add(1);
  dec->ring.cv.notify_all();
  return 0;
}

ENS_LTE_API int ens_lte_process_iq(ens_lte_decoder_t* dec,
                                   const float*      iq_interleaved,
                                   int32_t           num_complex_samples,
                                   int32_t           sample_rate_hz,
                                   int64_t           center_hz)
{
  if (!dec || !iq_interleaved || num_complex_samples <= 0 || sample_rate_hz < SEARCH_RATE)
    return -1;
  if (dec->last_rate.load() != sample_rate_hz) {
    dec->last_rate.store(sample_rate_hz);
    {
      std::lock_guard<std::mutex> lock(dec->resample_mu);
      dec->resample_phase = 0;
      dec->stage.clear();
    }
    dec->ring.clear();
  }
  {
    int64_t prev = dec->last_center.load();
    if (prev != 0) {
      int64_t d = center_hz > prev ? center_hz - prev : prev - center_hz;
      if (d > 15000)
        dec->retune_req.store(true);
    }
  }
  dec->last_center.store(center_hz);
  {
    std::lock_guard<std::mutex> lock(dec->result_mu);
    for (int i = 0; i < dec->result.cell_count; i++) {
      dec->result.cells[i].freq_mhz = center_hz / 1e6;
      // EARFCN is not decoded from RF at SYNC — mirror host center (Band 5 / 3 / 7 common).
      if (dec->result.cells[i].earfcn < 0) {
        double mhz = center_hz / 1e6;
        if (mhz >= 869.0 && mhz <= 894.0)
          dec->result.cells[i].earfcn = (int32_t)lround((mhz - 869.0) / 0.1) + 2400;
        else if (mhz >= 1805.0 && mhz <= 1880.0)
          dec->result.cells[i].earfcn = (int32_t)lround((mhz - 1805.0) / 0.1) + 1200;
        else if (mhz >= 2620.0 && mhz <= 2690.0)
          dec->result.cells[i].earfcn = (int32_t)lround((mhz - 2620.0) / 0.1) + 2750;
      }
    }
  }
  dec->push_resampled(iq_interleaved, num_complex_samples, sample_rate_hz);
  return 0;
}

ENS_LTE_API int ens_lte_cell_count(ens_lte_decoder_t* dec)
{
  if (!dec)
    return 0;
  std::lock_guard<std::mutex> lock(dec->result_mu);
  return dec->result.cell_count;
}

ENS_LTE_API int ens_lte_get_cell(ens_lte_decoder_t* dec, int32_t index, ens_lte_cell_t* out)
{
  if (!dec || !out || index < 0)
    return -1;
  std::lock_guard<std::mutex> lock(dec->result_mu);
  if (index >= dec->result.cell_count)
    return -1;
  *out = dec->result.cells[index];
  return 0;
}

ENS_LTE_API const char* ens_lte_status(ens_lte_decoder_t* dec)
{
  return dec ? dec->result.status : "";
}

ENS_LTE_API const char* ens_lte_version(void)
{
  return "ens_lte 1.5.0 (sticky MIB + no stage downgrade)";
}

} // extern "C"
