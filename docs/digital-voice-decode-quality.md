# Digital voice decode quality (NeuroSDR vs dmrext)

This note is the brief for the next change. Do not re-investigate the architecture from scratch. Implement the discriminator feed described below, then test DMR and D-STAR.

## Verdict

The gap is a **software feed problem** in NeuroSDR, with a smaller hardware remainder.

dmrext (`J:\cursor\dmrext`) and NeuroSDR both decode with a DSD-FME slicer (`getSymbol` + `dmr_filter` + mbelib). dmrext is not a different vocoder and it is not faster at the algorithm. It is clearer because the samples that enter the slicer are what DSD-FME was written for: a **uniform 48 kHz FM-discriminator waveform**, DC-centered, large enough to fill the 4-level eye, with no voice filter in front of the matched filter.

NeuroSDR builds that waveform incorrectly, then hands it to the same kind of slicer. Bit errors in the AMBE/IMBE frames are why the voice sounds rough and why calls are missed. Speaker filtering alone does not explain a lower recognition rate.

A real radio's limiter and ceramic IF are cleaner than an RSP1 software demodulator. That remainder is real, but it is not a reason to skip the feed fix. After the feed matches dmrext's contract, re-listen. Only the leftover gap is hardware.

## What each side actually does

### dmrext (the reference that sounds like a radio)

```
radio FM-detect pin
  → RP2350 ADC, uniform 48 kHz, 12-bit          board_pins.h BOARD_ADC_SAMPLE_HZ
  → (sample - 2048) * input_gain                main.c live_push_preprocessed_block
  → subtract a learned mean, then negate
  → int16 PCM
  → dsd_fsk_push_pcm16                          src/dsp/dsd_fsk/dsd_fsk.c
  → getSymbol, 10 samples/symbol, cosine/DMR RRC filter
  → dibits → DMR / D-STAR frame decode → mbelib → 8 kHz PWM
```

Fixed facts, do not "improve" these away:

- Sample clock is uniform. `samplesPerSymbol = 10` means every sample is 1/48000 s (`dsd_fsk.c` `init_state`).
- `opts.dc_block = 0`. DC is removed once, up front, by the mean subtract. It is not a one-pole highpass inside the slicer.
- `opts.use_cosine_filter = 1`. The DMR RRC (`dsd_fsk_filter.c`, 61 taps, gain `6.82973`) is the only pulse-shaping filter on the discriminator.
- Default `input_gain` is 24 (`app_config.c`). Outer 4FSK levels are meant to land in the thousands of int16 counts, near DSD's initial window of about ±15000.
- The negate is the polarity of **that radio's detect pin**. Do not copy it into NeuroSDR unless a capture shows the sync stuck on the inverted DMR pattern.
- Voice audio out is 8 kHz PWM. There is no 48 kHz voice lowpass on the way in.

### NeuroSDR (what DSD-FME actually receives)

Entry: `DigitalModeEngine.ProcessAf` pushes int16 PCM into `dsdfme.dll` at a declared 48 kHz (`DsdFmeSession`, `InputSampleRate = 48000`, `DibitInput` off, `InvertedDmr = 0`). The DLL then runs the same `getSymbol` / `dmr_filter` path (`samples/dmr/dsd-fme-audio_work`).

The float samples in `ProcessAf` are **not** a discriminator tap. They are the normal AF output of `AudioDemodulator.Process`:

1. IQ is boxcar-decimated toward 250 kHz, then FIR-decimated by 5 to **50 kHz** (`NarrowDecimation = 5`, `NarrowSampleRate = 50000`). The second boxcar stage does **not** decimate twice. Output rate is `inputRate / factor`. Leave that alone.
2. A 191-tap complex lowpass at `bandwidth/2` runs **before** the FM demod (`EnsureChannelFilter`, default branch). At the default 12.5 kHz channel that cutoff is **±6.25 kHz**. That part is a reasonable channel filter. Do not make it narrower.
3. FM demod for DMR, D-STAR, and C4FM is `DemodulateFm(..., deviationHz: 12500)` with deemphasis off. A ±1944 Hz DMR outer symbol becomes `1944/12500 = 0.155` full scale. Inner symbols (±648 Hz) become about `0.052`.
4. `ProcessAudioSample` then applies a one-pole lowpass and a phase-accumulator drop to 48 kHz.
   - Cutoff is **7.5 kHz** when bandwidth ≥ 10 kHz, otherwise **4 kHz** (`CalculateAudioAlpha`). DMR/D-STAR/C4FM share this with NFM.
   - The drop keeps a 50 kHz lowpass state and throws samples away until 48000 counts accumulate per 50000. Average rate is 48 kHz. Spacing is not. Most gaps are one 50 kHz sample (20.0 µs); every 25th input sample the gap is two (40.0 µs). A true 48 kHz sample is 20.833 µs. DSD, once synced, does **not** retune timing (`getSymbol` only nudges jitter when `have_sync == 0`).
5. `DigitalModeEngine` multiplies by `DigitalVoiceFeedVolume` (saved value **70**) and, only if enabled, a slow AGC clamped to 0.5–1.5 around target 0.40. Saved `DigitalVoiceFeedAgc` is **false**. Outer symbols arrive near `0.155 * 0.70 * 32767 ≈ ±3500` counts, not near ±15000.
6. For DMR/D-STAR/C4FM, `ProcessAf` sees the buffer **after** AF plugins (`frmNeuroSDR.ProcessDemodulatedAudio`). FreeDV already snapshots the demod buffer before plugins. Digital voice does not. RNNoise is no longer on this buffer. Do not put it back.

So the slicer sees a time-warped, voice-filtered, half-scale copy of a 50 kHz discriminator. dmrext sees a flat 48 kHz discriminator. Same decoder, different eye.

## What to change

Touch only the samples that enter `DsdFmeSession.PushPcm16` for DMR, D-STAR, and C4FM. Analog NFM, WFM, AM, and FreeDV stay as they are.

### 1. Uniform 48 kHz time base

Replace the drop-sampler for these three modes with a resampler whose output samples are equally spaced at 1/48000 s.

The narrow discriminator is 50 kHz. 50 kHz → 48 kHz is 24/25. Use linear interpolation (or the existing `StreamingFloatResampler` if it is a real interpolator, not a drop). Do not emit the lowpass state by skipping input samples.

Invariant to test: over any 4800 output samples (100 ms) the count is 4800, and the source time of sample `n+1` is exactly one 48 kHz period after sample `n`.

D-STAR is GMSK at 4800 baud and C4FM is 4FSK at 4800 baud. Both assume the same 10 samples/symbol. One resampler covers all three modes.

### 2. No voice lowpass in front of `dmr_filter`

For these three modes, do not run `CalculateAudioAlpha`'s 7.5 kHz / 4 kHz one-pole on the DSD feed. DSD's cosine filter is the matched filter. A voice lowpass before it adds intersymbol interference, and the 4 kHz branch (bandwidth &lt; 10 kHz) is enough to round off the eye.

The ±6.25 kHz complex channel filter may stay. It is the channel selector, analogous to the radio's IF. Do not add deemphasis.

### 3. Discriminator level, independent of the speaker fader

Scale the DSD feed so a ±1944 Hz deviation (DMR outer symbol) lands near **±12000 to ±16000** int16 counts. With the current demod (`df / 12500`), that is a fixed gain of about **6** on the float discriminator, applied only inside the digital-voice feed.

Do not use `DigitalVoiceFeedVolume` or the OUT 1 volume for this. Those are speaker controls. Do not enable the existing feed AGC as the fix. Its range is 0.5–1.5 around 0.40 and cannot lift 0.155 up to a full eye.

After the slicer has run for a few frames, `min` / `max` in DSD adapt. The point of the fixed gain is the **initial** thresholds (`min = -15000`, `max = 15000`, `lmid/umid = ±7500` in both `dsd_fsk.c` and upstream `getSymbol`) and a stable eye on short calls.

### 4. Feed the raw discriminator, not plugin audio

Snapshot the digital-mode float buffer the way FreeDV already does, **before** `_afPluginHost.Process`. Push that snapshot to `ProcessAf`. Plugins, RNNoise, squelch, and the OUT 1 post processor must not be on this path.

### 5. Leave polarity at `inverted_dmr = 0`

`DsdFmePcmAfPlugin` already notes that RSP1 locks with `inverted_dmr = 0`. dmrext's negate is its detect-pin wiring. Do not invert NeuroSDR unless a saved capture shows sync only on the inverted DMR sync types. If that happens, set `InvertedDmr` from the mode options. Do not hard-code dmrext's negate.

## What not to change

- Do not port dmrext's slicer into NeuroSDR. `dsdfme.dll` already is that slicer. The bug is the PCM in front of it.
- Do not run a second 4FSK slicer in C# and push dibits. The PCM path is the one that matches dmrext.
- Do not special-case RSP1, HackRF, RTL-SDR, or WebSDR. The feed rules are properties of DMR/D-STAR/C4FM, not of a device.
- Do not change mbelib, the DMR RRC coefficients, or `samplesPerSymbol`.
- Do not start by replacing the 8 kHz → 48 kHz zero-order hold in `DigitalVoicePlayback.PushDecodedMono8k`. That hold can sound a bit hard, but it does not drop sync or callsigns. Revisit it only after the eye is clean and the voice is still dull.
- Do not put RNNoise on this feed.

## How to prove it

Use one saved transmission so fading does not move the result. A few seconds of RSP1 IQ (or a 48 kHz discriminator WAV already known to decode in dmrext / DSD-FME) of DMR, then the same for D-STAR, is enough. There is no fixture WAV in `samples/dmr` today. Record one before tuning thresholds.

On that same recording, before and after the feed change, log:

| Check | Pass |
|---|---|
| Output rate | 48000 samples per measured second, ±1 sample |
| Sample spacing | no 20 µs / 40 µs alternation |
| Outer-symbol peak of the int16 feed | about ±12000 to ±16000 on a full-quieting signal, not ~±3500 |
| 4-level histogram (DMR) | four separated peaks, not two blobs |
| Sync | held for the whole transmission, not a brief hit then garble |
| Voice | intelligible across the call; callsign / talkgroup stable when the signal is solid |

Live check after that, on the radio you already use: one DMR channel and one D-STAR channel, RSP1, 12.5 kHz bandwidth, feed volume irrelevant to the decoder. OUT 1 should still follow the normal volume fader.

If the recording decodes cleanly in dmrext or desktop DSD-FME and still falls apart in NeuroSDR after this feed matches the checks above, stop. That leftover is the analog limiter versus the SDR demodulator, and it is not another slicer tweak.

## File map

| File | Role |
|---|---|
| `NeuroSDR/Dsp/AudioDemodulator.cs` | 50 kHz FM demod, ±bandwidth/2 channel filter, 7.5/4 kHz one-pole, 50→48 kHz drop |
| `NeuroSDR/Plugins/DigitalVoice/DigitalModeEngine.cs` | ×0.70 scale, then `PushPcm16` |
| `NeuroSDR/Plugins/DigitalVoice/DsdFmeSession.cs` | `dsdfme.dll` config: 48 kHz PCM, protocol, `inverted_dmr = 0` |
| `NeuroSDR/frmNeuroSDR.cs` `ProcessDemodulatedAudio` | DMR/D-STAR/C4FM currently taken after AF plugins; FreeDV is snapshotted before |
| `samples/dmr/dsd-fme-audio_work/src/dsd_symbol.c` | slicer contract: 10 samples/symbol, timing frozen after sync, then `dmr_filter` |
| `J:\cursor\dmrext\dmrext\main.c` `live_push_preprocessed_block` | reference front end: 48 kHz, gain, one mean subtract, negate, push |
| `J:\cursor\dmrext\dmrext\src\dsp\dsd_fsk\dsd_fsk.c` | same slicer defaults (`samplesPerSymbol = 10`, `use_cosine_filter = 1`, `dc_block = 0`) |
