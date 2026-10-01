/** Independent web speaker (does not affect desktop OUT volumes). Auto-starts after first user gesture. */

export class WebAudioPlayer {
  constructor() {
    this.ctx = null;
    this.gain = null;
    this.nextTime = 0;
    this.unlocked = false;
    this.packets = 0;
    this.webGain = 0.7;
    this._gestureWired = false;
    this.onStatus = null;
  }

  /** Wire once: any click/keydown/pointer unlocks AudioContext (browser autoplay policy). */
  armAutoStart() {
    if (this._gestureWired) return;
    this._gestureWired = true;
    const unlock = () => { void this.unlock(); };
    for (const evt of ["pointerdown", "keydown", "click", "touchstart"]) {
      window.addEventListener(evt, unlock, { capture: true, passive: true });
    }
    this.emitStatus();
  }

  emitStatus() {
    if (typeof this.onStatus === "function") this.onStatus(this);
  }

  async unlock() {
    this.ensure();
    if (!this.ctx) return false;
    try {
      if (this.ctx.state === "suspended") await this.ctx.resume();
      this.unlocked = this.ctx.state === "running";
    } catch {
      this.unlocked = false;
    }
    this.emitStatus();
    return this.unlocked;
  }

  ensure() {
    if (!this.ctx) {
      try {
        this.ctx = new (window.AudioContext || window.webkitAudioContext)({ sampleRate: 48000 });
      } catch {
        this.ctx = new (window.AudioContext || window.webkitAudioContext)();
      }
      this.gain = this.ctx.createGain();
      this.gain.gain.value = this.webGain;
      this.gain.connect(this.ctx.destination);
      this.nextTime = this.ctx.currentTime + 0.05;
    }
    if (this.gain) this.gain.gain.value = this.webGain;
    return this.ctx;
  }

  setVolume(pct) {
    this.webGain = Math.max(0, Math.min(1, pct / 100));
    if (this.gain) this.gain.gain.value = this.webGain;
  }

  /**
   * @param {string} base64
   * @param {number} [sampleRate]
   */
  playPcm16(base64, sampleRate = 48000) {
    if (!base64) return;
    try {
      this.ensure();
      if (!this.ctx || !this.gain) return;
      // Still suspended → wait for gesture; don't schedule silent backlog.
      if (this.ctx.state === "suspended") {
        void this.ctx.resume().then(() => {
          this.unlocked = this.ctx.state === "running";
          this.emitStatus();
        }).catch(() => this.emitStatus());
        return;
      }

      const raw = atob(base64);
      const frames = raw.length >> 1;
      if (frames < 8) return;
      const rate = sampleRate || this.ctx.sampleRate || 48000;
      const buf = this.ctx.createBuffer(1, frames, rate);
      const ch = buf.getChannelData(0);
      for (let i = 0; i < frames; i++) {
        const lo = raw.charCodeAt(i * 2);
        const hi = raw.charCodeAt(i * 2 + 1);
        let sample = (hi << 8) | lo;
        if (sample >= 0x8000) sample -= 0x10000;
        ch[i] = sample / 32768;
      }
      const src = this.ctx.createBufferSource();
      src.buffer = buf;
      src.connect(this.gain);
      const now = this.ctx.currentTime;
      if (this.nextTime < now + 0.03) this.nextTime = now + 0.04;
      if (this.nextTime > now + 0.75) this.nextTime = now + 0.05; // resync if stalled
      src.start(this.nextTime);
      this.nextTime += buf.duration;
      this.packets++;
      this.unlocked = true;
      if ((this.packets % 40) === 1) this.emitStatus();
    } catch {
      /* ignore decode glitches */
    }
  }
}
