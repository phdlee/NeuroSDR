# NeuroSDR

**Version 0.2** — Windows software-defined radio receiver by **KD8CEC**

Most people do **not** need to compile anything. Download a release archive, extract it, and run `NeuroSDR.exe`.

| Document | Language |
|---|---|
| [Install guide](INSTALL.md) | English (start here) |
| [한국어 README](README_kor.md) · [설치](INSTALL_kor.md) | Korean |
| [日本語 README](README_jpn.md) · [インストール](INSTALL_jpn.md) | Japanese |
| [中文 README](README_chn.md) · [安装](INSTALL_chn.md) | Chinese |

## What you get

- Spectrum and waterfall with click / drag / wheel tuning
- Modes: AM, SAM, NFM, WFM, USB, LSB, CW, RAW, plus digital voice paths (DMR, D-STAR, C4FM, FreeDV, and related)
- Local dongles: SDRplay, RTL-SDR, HackRF, Airspy (and optional SoapySDR devices)
- Remote receivers: WebSDR, KiwiSDR, OpenWebRX
- AF plugins (FT8/FT4, CW, SSTV, weather fax, and more), memory / scan, IQ and AF recording, satellite scene
- Dual audio outputs, web remote (optional), scene / settings save

## Quick start

1. Open **[Releases](https://github.com/phdlee/NeuroSDR/releases)** and download **NeuroSDR-v0.2-win-x64.zip** (or the newest release zip).
2. Follow **[INSTALL.md](INSTALL.md)** (driver notes for your radio, if any). For RTL-SDR, use the **latest Zadig**—older Zadig builds often fail to install WinUSB.
3. Run `NeuroSDR.exe` → pick a **SOURCE** → **RX START**.

You do not need Visual Studio, the .NET SDK, or this source tree to use NeuroSDR day to day.

Longer install and using manuals **with screenshots** will be published on the blog: **[https://www.hamskey.com](https://www.hamskey.com)**. Please visit there for the full picture guides.

## Hardware at a glance

| Radio | Extra install? |
|---|---|
| RTL-SDR, HackRF, Airspy | Usually no app DLLs; USB needs **latest Zadig** once if WinUSB is missing (old Zadig often fails) |
| SDRplay RSP | Yes — install the official **SDRplay API 3.x** |
| Lime / Pluto / BladeRF / USRP | Optional — install **PothosSDR** (SoapySDR) |
| WebSDR / Kiwi / OpenWebRX | No hardware; Internet only |

Bundled native libraries ship inside the release folder. Details: [INSTALL.md](INSTALL.md).

## Development and maintenance

**Design, coding, documentation, and GitHub maintenance for NeuroSDR are done by AI (Grok 4.7).** Human review and hardware testing by **KD8CEC** guide releases; the day-to-day engineering and repo work are AI-driven.

## Source code

This repository is for contributors and for building from source. End users should prefer the release zip.

```text
NeuroSDR/          main Windows app
KiwiSDRPlugin/     optional AF plugin sample project
```

## License / third-party

Vendor SDRs and native libraries keep their own licenses. See files under `NeuroSDR/native/` (COPYING / BINARIES notes) in the source tree and the licenses that ship with each release package.

---

NeuroSDR by **KD8CEC**

Detailed install and using manuals with screenshots will be published on the blog. Please visit **[https://www.hamskey.com](https://www.hamskey.com)** for the full picture guides and updates.
