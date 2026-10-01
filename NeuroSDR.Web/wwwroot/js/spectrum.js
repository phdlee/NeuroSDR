/** Desktop-like RF spectrum + classic waterfall renderer. */

export class SpectrumView {
  /**
   * @param {HTMLCanvasElement} spectrum
   * @param {HTMLCanvasElement} waterfall
   * @param {(hz: number) => void} onTune
   */
  constructor(spectrum, waterfall, onTune) {
    this.spectrum = spectrum;
    this.waterfall = waterfall;
    this.onTune = onTune;
    this.sctx = spectrum.getContext("2d");
    this.wctx = waterfall.getContext("2d");
    /** @type {import('./types.js').SpectrumRemoteFrame | null} */
    this.frame = null;
    this.waterfallReady = false;
    /** @type {{ x: number, moved: boolean } | null} */
    this.drag = null;
    /** Optimistic VFO while waiting for host state (ms). */
    this._localTunedHz = 0;
    this._localTuneUntil = 0;

    const hzAt = (canvas, clientX) => {
      const frame = this.frame;
      if (!frame || !frame.spanHz) return null;
      const rect = canvas.getBoundingClientRect();
      const x = (clientX - rect.left) / Math.max(1, rect.width);
      return Math.round(frame.centerHz - frame.spanHz / 2 + x * frame.spanHz);
    };

    const applyTune = (hz) => {
      if (hz == null || !Number.isFinite(hz)) return;
      this._localTunedHz = hz;
      this._localTuneUntil = performance.now() + 1200;
      this.onTune(hz);
    };

    for (const canvas of [spectrum, waterfall]) {
      canvas.addEventListener("pointerdown", (ev) => {
        if (ev.button !== 0) return;
        canvas.setPointerCapture(ev.pointerId);
        // Match desktop: click commits on release; drag retunes to absolute X.
        this.drag = { x: ev.clientX, moved: false };
      });
      canvas.addEventListener("pointermove", (ev) => {
        if (!this.drag) return;
        if (Math.abs(ev.clientX - this.drag.x) >= 4) this.drag.moved = true;
        if (this.drag.moved) applyTune(hzAt(canvas, ev.clientX));
      });
      const end = (ev) => {
        if (!this.drag) return;
        if (!this.drag.moved) applyTune(hzAt(canvas, ev.clientX ?? this.drag.x));
        this.drag = null;
      };
      canvas.addEventListener("pointerup", end);
      canvas.addEventListener("pointercancel", () => { this.drag = null; });
    }

    window.addEventListener("resize", () => this.resize());
    this.resize();
  }

  /** Prefer local click until host catches up. */
  tunedHz() {
    if (this._localTunedHz && performance.now() < this._localTuneUntil) return this._localTunedHz;
    return this.frame?.tunedHz ?? this._localTunedHz;
  }

  /** Host snapshot may lag; keep showing local VFO for a moment. */
  preferredFrequency(hostHz) {
    if (this._localTunedHz && performance.now() < this._localTuneUntil
        && Math.abs((hostHz || 0) - this._localTunedHz) >= 50)
      return this._localTunedHz;
    return hostHz;
  }

  setLocalTuned(hz) {
    this._localTunedHz = hz;
    this._localTuneUntil = performance.now() + 1200;
  }

  resize() {
    for (const c of [this.spectrum, this.waterfall]) {
      const cssW = Math.max(320, Math.floor(c.clientWidth));
      const cssH = Math.max(80, Math.floor(c.clientHeight));
      if (c.width !== cssW || c.height !== cssH) {
        c.width = cssW;
        c.height = cssH;
        if (c === this.waterfall) this.waterfallReady = false;
      }
    }
  }

  /** @param {import('./types.js').SpectrumRemoteFrame} frame */
  draw(frame) {
    this.frame = frame;
    // Host confirmed near our optimistic tune — clear local hold.
    if (this._localTunedHz && Math.abs((frame.tunedHz || 0) - this._localTunedHz) < 50)
      this._localTuneUntil = 0;
    this.resize();
    const levels = frame.levels || [];
    if (!levels.length || !this.sctx || !this.wctx) return;

    const sw = this.spectrum.width;
    const sh = this.spectrum.height;
    const tuned = this.tunedHz();
    const g = this.sctx.createLinearGradient(0, 0, 0, sh);
    g.addColorStop(0, "#0a2030");
    g.addColorStop(1, "#050b11");
    this.sctx.fillStyle = g;
    this.sctx.fillRect(0, 0, sw, sh);

    this.sctx.strokeStyle = "rgba(70,110,130,0.35)";
    this.sctx.lineWidth = 1;
    for (let i = 1; i < 4; i++) {
      const y = (sh * i) / 4;
      this.sctx.beginPath();
      this.sctx.moveTo(0, y);
      this.sctx.lineTo(sw, y);
      this.sctx.stroke();
    }

    const minDb = -140, maxDb = 0;
    const toY = (db) => {
      const t = (db - minDb) / (maxDb - minDb);
      return sh - Math.max(0, Math.min(1, t)) * (sh - 4) - 2;
    };

    if (frame.filterHz > 0 && frame.spanHz > 0) {
      const left = tuned - frame.filterHz / 2;
      const right = tuned + frame.filterHz / 2;
      const x0 = ((left - (frame.centerHz - frame.spanHz / 2)) / frame.spanHz) * sw;
      const x1 = ((right - (frame.centerHz - frame.spanHz / 2)) / frame.spanHz) * sw;
      this.sctx.fillStyle = "rgba(80, 160, 220, 0.12)";
      this.sctx.fillRect(Math.min(x0, x1), 0, Math.abs(x1 - x0), sh);
    }

    this.sctx.beginPath();
    for (let i = 0; i < levels.length; i++) {
      const x = (i / Math.max(1, levels.length - 1)) * sw;
      const y = toY(levels[i]);
      if (i === 0) this.sctx.moveTo(x, y);
      else this.sctx.lineTo(x, y);
    }
    this.sctx.lineTo(sw, sh);
    this.sctx.lineTo(0, sh);
    this.sctx.closePath();
    const fill = this.sctx.createLinearGradient(0, 0, 0, sh);
    fill.addColorStop(0, "rgba(68,210,242,0.45)");
    fill.addColorStop(1, "rgba(68,210,242,0.02)");
    this.sctx.fillStyle = fill;
    this.sctx.fill();

    this.sctx.beginPath();
    for (let i = 0; i < levels.length; i++) {
      const x = (i / Math.max(1, levels.length - 1)) * sw;
      const y = toY(levels[i]);
      if (i === 0) this.sctx.moveTo(x, y);
      else this.sctx.lineTo(x, y);
    }
    this.sctx.strokeStyle = "#7bddf7";
    this.sctx.lineWidth = 1.5;
    this.sctx.stroke();

    if (frame.spanHz > 0) {
      const tx = ((tuned - (frame.centerHz - frame.spanHz / 2)) / frame.spanHz) * sw;
      this.sctx.strokeStyle = "#ffc145";
      this.sctx.lineWidth = 1.2;
      this.sctx.beginPath();
      this.sctx.moveTo(tx, 0);
      this.sctx.lineTo(tx, sh);
      this.sctx.stroke();
    }

    const ww = this.waterfall.width;
    const wh = this.waterfall.height;
    if (!this.waterfallReady) {
      this.wctx.fillStyle = "#050b11";
      this.wctx.fillRect(0, 0, ww, wh);
      this.waterfallReady = true;
    }
    this.wctx.drawImage(this.waterfall, 0, 0, ww, wh - 1, 0, 1, ww, wh - 1);
    const row = this.wctx.createImageData(ww, 1);
    for (let x = 0; x < ww; x++) {
      const i = Math.min(levels.length - 1, Math.floor((x / ww) * levels.length));
      const t = Math.max(0, Math.min(1, (levels[i] - minDb) / (maxDb - minDb)));
      const c = heat(t);
      const o = x * 4;
      row.data[o] = c[0];
      row.data[o + 1] = c[1];
      row.data[o + 2] = c[2];
      row.data[o + 3] = 255;
    }
    this.wctx.putImageData(row, 0, 0);
  }
}

/** @param {number} t 0..1 */
function heat(t) {
  const r = Math.floor(Math.min(255, t * 2.2 * 255));
  const g = Math.floor(Math.min(255, Math.max(0, (t - 0.25) * 2.4 * 255)));
  const b = Math.floor(Math.min(255, Math.max(0, (0.55 - t) * 2.8 * 255 + t * 40)));
  return [r, g, b];
}
