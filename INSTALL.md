# NeuroSDR install guide (release zip)

This guide is for people who **download a release archive** and run NeuroSDR. You do not need to compile the project.

## 1. Get the software

1. Open the NeuroSDR GitHub **Releases** page.
2. Download the latest Windows zip (x64 unless you specifically need x86).
3. Extract the whole archive to a folder you can write to (for example `C:\NeuroSDR` or your Documents folder).  
   Do not run from a half-extracted archive or from a read-only network share if you can avoid it.

## 2. What is already included

A normal release folder already contains:

- `NeuroSDR.exe` (and supporting .NET / native files as packaged for that build)
- Bundled device libraries for common dongles (RTL-SDR, HackRF, Airspy, and related USB helpers), under paths such as `native\`
- Built-in AF / IQ plugins that ship with the app

You usually **do not** need to download extra DLLs for RTL-SDR, HackRF, or Airspy beyond what is in the zip.

## 3. Extra software you may need (by radio)

### Windows USB (RTL-SDR / HackRF / Airspy)

1. Plug the dongle in.
2. If Windows does not see it as a usable SDR, install a WinUSB / libusb driver with **[Zadig](https://zadig.akeo.ie/)** (one-time per device).  
   Pick the SDR interface (not a composite “Storage” or HID-only entry) and install WinUSB.
3. Start NeuroSDR and choose the matching **SOURCE**.

### SDRplay (RSP1 / RSP1A / RSPdx / …)

NeuroSDR loads the official API from the SDRplay install path. The release zip does **not** replace that installer.

1. Download and install **SDRplay API 3.x** from the SDRplay website.
2. Reboot if the installer asks.
3. Connect the RSP, start NeuroSDR, select the SDRplay source, then **RX START**.

### SoapySDR devices (Lime, Pluto, BladeRF, USRP, …)

Optional. Install **[PothosSDR](https://github.com/pothosware/PothosSDR/wiki)** (or another SoapySDR install). NeuroSDR looks for `SoapySDR.dll` in common locations such as `%ProgramFiles%\PothosSDR\bin\`.

### Remote-only (WebSDR / KiwiSDR / OpenWebRX)

No local radio or driver. You need Internet access and a working site URL. Use the site list / Find controls in the UI, then **RX START**.

## 4. First run

1. Start `NeuroSDR.exe`.
2. Select a **SOURCE** (hardware or remote).
3. Press **RX START**.
4. Click the spectrum to tune, or type a frequency into the VFO and press Enter.
5. Open **SET** for audio devices, RF performance, plugins, and web remote options.

Settings are stored under:

```text
%LocalAppData%\NeuroSDR\
```

(`settings.json` in Debug builds; release builds use the release settings file name documented with that package.)

## 5. Optional: voice guidance

SETUP → General → **VOICE GUIDANCE** uses offline **eSpeak NG**. Install eSpeak NG for Windows if you want spoken frequency / mode prompts. This is optional and not required for normal listening.

## 6. Optional: web remote

In SETUP → General, enable **WEB REMOTE** if you want the phone / browser UI. Default is off. When enabled, the status bar shows a listening URL (often `http://127.0.0.1:8765/`). Binding to the LAN and access tokens are also configured there.

## 7. If something fails

| Symptom | What to try |
|---|---|
| No SOURCE for your dongle | Zadig / USB cable / another USB port; for SDRplay, confirm the API is installed |
| RX START fails | Close other SDR apps that hold the same device; check the status bar message |
| No sound | SETUP → Audio: pick a real waveOut device; unmute OUT1; check Windows volume |
| Remote WebSDR audio wrong | Confirm the site URL; try another site; check Internet / firewall |
| App will not start | Extract the full zip again; use the x64 build on 64-bit Windows; Windows Defender may need an allow once |

## 8. You do not need (for normal use)

- Visual Studio or the .NET SDK
- Cloning this Git repository
- Building native DLLs yourself
- The large `samples\` trees used only for development

Those are for contributors. Everyday use is: **download release → extract → install any required vendor driver → run NeuroSDR.exe**.

---

Other languages: [한국어](INSTALL_kor.md) · [日本語](INSTALL_jpn.md) · [中文](INSTALL_chn.md)  
Project overview: [README.md](README.md)
