# LTE / PS-LTE IQ plugin

## Broadcast decode ladder (3GPP “정석”)

```
IQ @ ≥1.92 MS/s
  → PSS/SSS          PCI, CP, CFO          ← implemented (SYNC)
  → PBCH / MIB       nof_prb, ports, SFN   ← next
  → PDCCH SI-RNTI    SF5 & even SFN        ← then
  → PDSCH SIB1 TB
  → RRC ASN.1        PLMN-IdentityInfoList, TAC, CellIdentity
```

**SIB1 schedule (FDD):** RNTI `0xFFFF` (SI-RNTI), subframe **5**, SFN **even** → opportunity every **20 ms**.  
PBCH combines over **40 ms** (4 frames); SIB1 content often refreshed on an **80 ms** wall-clock cadence in practice — the air-interface opportunity is the 20 ms SI window above (TS 36.331 / 36.321).

PLMN is **only** authoritative from SIB1 `cellAccessRelatedInfo.plmn-IdentityList` (not from EARFCN alone).

## Deploy note

Default mode is **managed** (C# PSS/SSS only). `ens_lte.dll` is **optional** — omit it from the package for a managed-only build. HYBRID/NATIVE buttons load the DLL later when present.

MIB→SIB1/PLMN via native is experimental and parked for a later retry (sample-rate / PBCH stability).


| Column | Source today |
|--------|----------------|
| EARFCN / FREQ / Band | Tune frequency (TS 36.101) |
| PCI / CP / PSS / CFO | PSS + SSS |
| PRB / PORTS | 6 / `?` until MIB |
| TAC / CID / MCC-MNC | Empty until SIB1 |
| NOTE | `SYNC` · `SIB1/PLMN pending` |

## Validate your 875 MHz capture

Example: **EARFCN 2469**, **875.850 MHz**, **PCI 53 / 379**

1. Band 5 DL: `F = 869 + 0.1×(N−2400)` → N=2469 → **875.9 MHz** (0.05 MHz offset from 875.85 is normal VFO/center skew).
2. PCI must be **stable** across scans (same N_id_1/N_id_2); bouncing PCI ⇒ weak PSS/SSS.
3. CP column: almost always **N** (normal) on commercial / PS-LTE FDD.
4. After SIB1 lands: MCC for Korea land mobile is typically **450**; MNC identifies the operator / PS-LTE PLMN.

## Algorithms / sources

- Sync: managed port of srsRAN_4G `cell_search` / `synch_file` / `pss` / `sss` under `samples/srsRAN_4G`
- PLMN bit layout: `asn1::rrc::plmn_id_s` (+ `srsenb/test/upper/plmn_test.cc` golden `{0x89,0x19,0x14}` → `123-45`)
- Full MIB + SI-RNTI PDSCH still to wire (C# PHY or thin native `srsran_phy` + `rrc_asn1` DLL)

## Hardware

- ≥ **1.92 MS/s** (6 PRB sync); after MIB, capture BW should cover the real `nof_prb` (e.g. 5 MHz → ~7.68 MS/s)
- Mode **RAW**, center on DL EARFCN
