# LteSib1Native (`ens_lte.dll`)

MinGW DLL that wraps **srsRAN_4G** PHY + RRC ASN.1:

`IQ → cell search (PSS/SSS) → MIB (PBCH) → SIB1 (SI-RNTI PDSCH) → PLMN / TAC / CellIdentity`

## Build (Windows)

Prereqs (already used by `build.ps1`):

- MSYS2 + `mingw-w64-x86_64-{toolchain,cmake,ninja,fftw,mbedtls,boost}`
- Tree at `samples/srsRAN_4G`

```powershell
cd J:\codex\sdr\NeuroSDR\LteSib1Native
.\build.ps1
```

Output: `NeuroSDR/native/win-x64/ens_lte.dll` (+ MinGW runtime DLLs may need to sit beside it: `libgcc_s_seh-1.dll`, `libstdc++-6.dll`, `libwinpthread-1.dll`, `libfftw3f-3.dll`).

## C API

See `include/ens_lte.h`:

- `ens_lte_create` / `destroy` / `reset`
- `ens_lte_process_iq(iq_interleaved, n, rate, center_hz)`
- `ens_lte_copy_result` → cells with `stage` 1=SYNC 2=MIB 3=SIB1

## License

srsRAN_4G is **AGPL-3.0**. Shipping `ens_lte.dll` linked against it inherits AGPL obligations.
