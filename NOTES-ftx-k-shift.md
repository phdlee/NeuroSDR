# FT8/FT4 K Shift (formerly 4K Shift) — removed 2026-08-17

This document records the **entire** FT8/FT4 AF mix-down shift feature so it can be restored later. The feature was removed because it had little practical value: the physically correct offset was closer to **6 kHz**, but that much mix-down attenuated the wanted segment too much; **5 kHz** was tried as a compromise and still did not justify keeping the path.

Default was always **0 kHz** (no shift) after the checkbox became a numeric box.

## What it did

Goal: decode an **upper AF segment** (e.g. FT8 in 0–3 kHz and FT4 stacked above it) by mixing that upper band down to 0 Hz+ so `ft8_lib` sees a normal baseband.

Pipeline:

1. **RX filter** must be wide enough to pass `shift + ~3 kHz` (FT8/FT4 occupancy is rarely >3 kHz).
2. **Before decode:** high-pass near the shift edge → cosine mix-down by `shiftHz` → low-pass ~3.5 kHz.
3. **After decode:** add `shiftHz` back onto the reported audio frequency so the UI/list shows the original AF Hz, not the mixed-down Hz.

Audio output / AF waterfall were **not** shifted. Only the decoder PCM path was.

## UI history

### Checkbox (4K Shift)

- Control bar checkbox `Text = "4K Shift"`, `Tag = "ftx-shift4k"`.
- Checked → `instance.FtxShift4k = true` and **auto BW = 10 kHz** if current BW `< 10000`.
- Later changed to **auto BW = 7 kHz** if current BW `< 7000`, because FT8/FT4 rarely needs >3 kHz (`4 kHz shift + 3 kHz occupancy ≈ 7 kHz`).
- Color: `ForeColor = Color.FromArgb(255, 193, 69)`, location ~`(368, 4)`.

### Numeric box (K Shift)

Replaced the checkbox (user wanted to try 5 kHz instead of a fixed 4 kHz):

- Label `"K Shift"` + `NumericUpDown` (`Tag = "ftx-shift-khz"`).
- Range **0–12 kHz**, default **0**. Increment was 0.5 then 1.0 (integer kHz).
- `instance.FtxShiftKhz = shiftKhz.Value`.
- Auto BW: if value `> 0` and current BW `< shiftHz + 3000`, set BW to that (clamped to `_bandwidthBox.Maximum`).
  - Example: 5 kHz shift → need 8000 Hz.
- CLEAR button min-left was bumped from **455** to **464** to make room.

Layout (last version):

```
shiftLabel: Location (400, 3), Size (52, 21), Text "K Shift"
shiftKhz:   Location (370, 2), Size (35, 25), Min 0, Max 12, DecimalPlaces 0
CLEAR:      Math.Max(464, decoderWidth - 64)
```

`BuildFtxControlBar` added `shiftLabel`, `shiftKhz` next to VFO / preset / T / AUTO / QSO / CLEAR.

## Settings

`AfPluginInstanceSettings` (`NeuroSDR/NeuroSDR/Settings/AppSettings.cs`):

```csharp
/// <summary>kHz mix-down before FT8/FT4 decode. 0 = no shift.</summary>
public decimal FtxShiftKhz { get; set; }

/// <summary>Legacy checkbox. Reading true with FtxShiftKhz=0 migrates to 4 kHz.</summary>
[JsonIgnore(Condition = JsonIgnoreCondition.WhenWriting)]
public bool FtxShift4k
{
    get => false;
    set
    {
        if (value && FtxShiftKhz <= 0)
            FtxShiftKhz = 4m;
    }
}
```

- Persist in `%LocalAppData%\NeuroSDR\settings.json` under each AF plugin instance.
- On load, clamp `FtxShiftKhz` to `0..12`.
- Plugin setup row (`PluginSetupForm.AfPluginSetupRow.ToSettings`) copied `FtxShiftKhz` from the prior instance so SETUP did not wipe it.
- `frmNeuroSDR.AfPluginInstanceSignature()` included `FtxShiftKhz` so changing shift rebuilt plugin activation.

Old JSON with only `"FtxShift4k": true` migrated to **4 kHz**.

## Host → plugin options

`AfPluginHost.Apply` passed:

```csharp
["shiftKhz"] = instance.FtxShiftKhz.ToString(InvariantCulture)   // FTX plugins only; others "0"
```

Earlier boolean form:

```csharp
["shift4k"] = (plugin is FTX && instance.FtxShift4k).ToString()
```

`FtxDecoderAfPlugin.Configure` parsed `shiftKhz` (float kHz × 1000 → Hz). Fallback: `shift4k == true` → 4000 Hz.

## Decoder DSP

File: `NeuroSDR/NeuroSDR/Plugins/BuiltInAfPlugins.cs`

`FtxDecoderAfPlugin` held:

- `AfFrequencyShifter _shifter`
- `float _shiftHz`

`Process`:

```csharp
var pcm = shiftHz > 0.5f
    ? ToPcm16(_shifter.Process(block.Input.Span, block.SampleRate))
    : ToPcm16(block.Input.Span);
_receiver.PushPcm(pcm, block.SampleRate);
```

`PublishDecode` remapped frequency for the host/UI:

```csharp
var audioHz = line.FreqHz + shiftHz;
fields["freq"] = audioHz.ToString("0");
```

Changing shift called `_shifter.SetShiftHz(nextShiftHz)` (rebuilds filters).

### `AfFrequencyShifter`

High-pass above ~shift, mix down by shift Hz, then low-pass so FT decode sees the upper AF band as 0 Hz+.

```csharp
internal sealed class AfFrequencyShifter
{
    private float _shiftHz;
    private int _sampleRate;
    private double _phase;
    private Biquad _highPass = Biquad.Bypass();
    private Biquad _lowPass = Biquad.Bypass();

    public void SetShiftHz(float shiftHz)
    {
        _shiftHz = Math.Max(0, shiftHz);
        _sampleRate = 0; // force filter rebuild
        Reset();
    }

    public float[] Process(ReadOnlySpan<float> input, int sampleRate)
    {
        if (sampleRate != _sampleRate || sampleRate <= 0)
        {
            _sampleRate = Math.Max(1, sampleRate);
            _highPass = Biquad.HighPass(_shiftHz * .92f, _sampleRate, .707f);
            _lowPass = Biquad.LowPass(3_500f, _sampleRate, .707f);
            Reset();
        }

        var output = new float[input.Length];
        var step = 2 * Math.PI * _shiftHz / _sampleRate;
        for (var index = 0; index < input.Length; index++)
        {
            var filtered = _highPass.Process(input[index]);
            _phase += step;
            if (_phase > Math.PI * 2) _phase -= Math.PI * 2;
            var mixed = filtered * 2f * (float)Math.Cos(_phase);
            output[index] = _lowPass.Process(mixed);
        }
        return output;
    }
}
```

Nested `Biquad` implemented RBJ **LowPass** / **HighPass** (`q = 0.707`) plus `Bypass` and `Reset`. Coefficients:

- LP: `b0=b2=(1-cos)/2`, `b1=1-cos`, `a0=1+alpha`, `a1=-2cos`, `a2=1-alpha`
- HP: `b0=b2=(1+cos)/2`, `b1=-(1+cos)`, same `a*`
- `alpha = sin / (2q)`, `w0` clamped to `[20, 0.45 * sampleRate]` Hz

**Caveat:** 6 kHz mix-down + HP at `0.92 * shift` + LP 3.5 kHz after mix caused too much attenuation on the wanted segment. A variable kHz box was added so 5 kHz could be tested; that still did not look worth keeping.

## Related (keep vs remove)

These were **comments / wide-USB support**, not the shift DSP itself. They stayed after removal except the “K Shift” wording:

- `AudioDemodulator.EnsureChannelFilter` USB band-pass clamp **500–12000 Hz** (wide USB for stacked digital, independent of mix-down).
- `ConfigureAfDisplay` AF span: `Clamp(bandwidth <= 4000 ? 3500 : bandwidth, 3500, 12000)` so a wide USB filter still shows a wide AF waterfall.

Auto-selecting **7 k / 10 k** BW buttons when enabling shift **is** part of this feature and must not come back unless shift is restored.

## Files that contained the feature

| File | What to restore |
|---|---|
| `NeuroSDR/NeuroSDR/frmNeuroSDR.cs` | K Shift label + NumericUpDown (or 4K checkbox), auto-BW, CLEAR x=464, signature, AF-span comment |
| `NeuroSDR/NeuroSDR/Settings/AppSettings.cs` | `FtxShiftKhz`, legacy `FtxShift4k`, load clamp 0–12 |
| `NeuroSDR/NeuroSDR/Plugins/AfPluginHost.cs` | `shiftKhz` (or `shift4k`) in `Configure` options |
| `NeuroSDR/NeuroSDR/Plugins/PluginSetupForm.cs` | copy `FtxShiftKhz` in `ToSettings` |
| `NeuroSDR/NeuroSDR/Plugins/BuiltInAfPlugins.cs` | `_shifter`, `_shiftHz`, `ParseShiftHz`, mix-down `Process`, freq remap, `AfFrequencyShifter` + `Biquad` |
| `NeuroSDR/NeuroSDR/Dsp/AudioDemodulator.cs` | comment only (USB 12 kHz clamp stays) |

## Restore checklist

1. Put `AfFrequencyShifter` + `Biquad` back in `BuiltInAfPlugins.cs`.
2. Mix-down decoder PCM when `shiftHz > 0.5`; add `shiftHz` to `fields["freq"]`.
3. Persist `FtxShiftKhz` per AF instance; accept old `FtxShift4k`.
4. Pass `shiftKhz` from `AfPluginHost`.
5. Rebuild FTX control-bar K Shift box (default 0).
6. Auto-widen RX BW to `shiftHz + 3000` when shift > 0.
7. Do **not** shift speaker audio or the AF waterfall unless that is an explicit new requirement.
