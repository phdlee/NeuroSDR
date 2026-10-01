# NeuroSDR Web Remote — Desktop UI

The live UI served by Kestrel is under `wwwroot/` (ES modules + `desktop.css`).

Typed contracts live in `client/src/types.ts`. The browser modules in `wwwroot/js/` follow the same structure (JSDoc + ESM) so the app runs without a Node build step.

## Desktop parity (this revision)

- RX CONTROL layout: frequency digits, SCENE, channels, modes, bands, BW, OUT/SQL, SUB VFO list
- RF spectrum + classic waterfall (desktop colors / tune marker / filter shade)
- AF plugin decode feed
- SignalR control: scene apply, channel select, band presets, view zoom helpers

## Mobile

A simplified mobile shell (current phone-remote fidelity) is planned as a follow-up route; this page is desktop-first and still usable on narrow screens via responsive stacking.

## Optional Vite build (later)

```bash
cd client
npm create vite@latest . -- --template vanilla-ts
# map output to ../wwwroot
```
