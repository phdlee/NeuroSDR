(() => {
  "use strict";

  const $ = (id) => document.getElementById(id);
  const spectrum = $("spectrum");
  const waterfall = $("waterfall");
  const sctx = spectrum.getContext("2d");
  const wctx = waterfall.getContext("2d");
  const modes = ["AM", "FM", "USB", "LSB", "CW", "DSB", "RAW", "WFM"];

  let state = null;
  let hub = null;
  let applying = false;
  let audioCtx = null;
  let gainNode = null;
  let nextPlayTime = 0;
  let webGain = 0.7;
  let audioUnlocked = false;
  let audioPackets = 0;
  let waterfallReady = false;
  const afLines = [];
  const AF_MAX = 200;

  function token() {
    const q = new URLSearchParams(location.search).get("token")
      || new URLSearchParams(location.search).get("access_token");
    if (q) {
      localStorage.setItem("neurosdr.token", q);
      return q;
    }
    return localStorage.getItem("neurosdr.token") || "";
  }

  function fmtHz(hz) {
    if (!Number.isFinite(hz)) return "—";
    if (Math.abs(hz) >= 1e6) return (hz / 1e6).toFixed(6) + " MHz";
    if (Math.abs(hz) >= 1e3) return (hz / 1e3).toFixed(3) + " kHz";
    return Math.round(hz) + " Hz";
  }

  function setConn(text, ok) {
    $("connStatus").textContent = text;
    $("connStatus").style.color = ok ? "var(--ok)" : "var(--muted)";
  }

  function ensureAudio() {
    if (!audioCtx) {
      audioCtx = new (window.AudioContext || window.webkitAudioContext)({ sampleRate: 48000 });
      gainNode = audioCtx.createGain();
      gainNode.gain.value = webGain;
      gainNode.connect(audioCtx.destination);
      nextPlayTime = audioCtx.currentTime + 0.08;
    }
    if (audioCtx.state === "suspended") {
      audioCtx.resume().then(() => { audioUnlocked = true; updateSpeakerBtn(); })
        .catch(() => { audioUnlocked = false; updateSpeakerBtn(); });
    } else {
      audioUnlocked = true;
      updateSpeakerBtn();
    }
    if (gainNode) gainNode.gain.value = webGain;
    return audioCtx;
  }

  function updateSpeakerBtn() {
    const btn = $("speakerBtn");
    if (!btn) return;
    if (audioUnlocked && audioCtx && audioCtx.state === "running") {
      btn.textContent = "Speaker on · " + audioPackets + " pkts";
      btn.classList.add("on");
    } else {
      btn.textContent = "Enable speaker";
      btn.classList.remove("on");
    }
  }

  function playPcm16(base64, sampleRate) {
    if (!base64) return;
    try {
      ensureAudio();
      if (!audioCtx || !gainNode) return;
      const raw = atob(base64);
      const frames = raw.length >> 1;
      if (frames < 8) return;
      const rate = sampleRate || 48000;
      const buf = audioCtx.createBuffer(1, frames, rate);
      const ch = buf.getChannelData(0);
      for (let i = 0; i < frames; i++) {
        const lo = raw.charCodeAt(i * 2);
        const hi = raw.charCodeAt(i * 2 + 1);
        let sample = (hi << 8) | lo;
        if (sample >= 0x8000) sample -= 0x10000;
        ch[i] = sample / 32768;
      }

      const src = audioCtx.createBufferSource();
      src.buffer = buf;
      src.connect(gainNode);
      const now = audioCtx.currentTime;
      // Keep a small jitter buffer; if we fall behind, resync without dropping everything.
      if (nextPlayTime < now + 0.04) nextPlayTime = now + 0.04;
      if (nextPlayTime > now + 0.75) nextPlayTime = now + 0.12;
      src.start(nextPlayTime);
      nextPlayTime += buf.duration;
      audioPackets++;
      if ((audioPackets % 25) === 0) updateSpeakerBtn();
    } catch (e) {
      $("statusLine").textContent = "audio: " + (e && e.message ? e.message : e);
    }
  }

  let spectrumMeta = { centerHz: 0, spanHz: 0, tunedHz: 0, filterHz: 0 };
  let lastBins = null;
  let hoverRatio = -1;

  function heat(v) {
    const r = Math.min(255, Math.floor(v * 2.2 * 255));
    const g = Math.min(255, Math.floor(Math.max(0, v - 0.25) * 2.4 * 255));
    const b = Math.min(255, Math.floor(Math.max(0, 0.55 - v) * 1.6 * 255 + v * 40));
    return [r, g, b];
  }

  function viewLeftHz() {
    return spectrumMeta.centerHz - spectrumMeta.spanHz / 2;
  }

  function ratioToHz(ratio) {
    const span = Math.max(1, spectrumMeta.spanHz || 0);
    const left = viewLeftHz();
    return Math.round(left + Math.max(0, Math.min(1, ratio)) * span);
  }

  function hzToRatio(hz) {
    const span = Math.max(1, spectrumMeta.spanHz || 1);
    return (hz - viewLeftHz()) / span;
  }

  function pointerRatio(canvas, evt) {
    const rect = canvas.getBoundingClientRect();
    const clientX = (evt.touches && evt.touches[0] ? evt.touches[0].clientX : evt.clientX);
    if (!rect.width) return 0;
    return Math.max(0, Math.min(1, (clientX - rect.left) / rect.width));
  }

  function drawTuneMarkers(ctx, w, h) {
    if (!(spectrumMeta.spanHz > 0)) return;
    const tunedR = hzToRatio(spectrumMeta.tunedHz);
    if (tunedR >= 0 && tunedR <= 1) {
      const x = tunedR * (w - 1);
      ctx.strokeStyle = "rgba(240,180,41,0.95)";
      ctx.lineWidth = 1.5;
      ctx.beginPath();
      ctx.moveTo(x, 0);
      ctx.lineTo(x, h);
      ctx.stroke();
      const half = (spectrumMeta.filterHz || 0) / Math.max(1, spectrumMeta.spanHz) / 2;
      if (half > 0) {
        const x0 = Math.max(0, (tunedR - half) * (w - 1));
        const x1 = Math.min(w - 1, (tunedR + half) * (w - 1));
        ctx.fillStyle = "rgba(240,180,41,0.12)";
        ctx.fillRect(x0, 0, Math.max(1, x1 - x0), h);
      }
    }
    if (hoverRatio >= 0 && hoverRatio <= 1) {
      const hx = hoverRatio * (w - 1);
      ctx.strokeStyle = "rgba(46,196,214,0.7)";
      ctx.setLineDash([4, 3]);
      ctx.beginPath();
      ctx.moveTo(hx, 0);
      ctx.lineTo(hx, h);
      ctx.stroke();
      ctx.setLineDash([]);
    }
  }

  function drawSpectrum(bins) {
    lastBins = bins;
    const w = spectrum.width;
    const h = spectrum.height;
    sctx.fillStyle = "#050b11";
    sctx.fillRect(0, 0, w, h);
    if (!bins || bins.length < 2) {
      drawTuneMarkers(sctx, w, h);
      return;
    }

    let min = Infinity, max = -Infinity;
    for (let i = 0; i < bins.length; i++) {
      const v = bins[i];
      if (v < min) min = v;
      if (v > max) max = v;
    }
    const span = Math.max(20, max - min);
    sctx.beginPath();
    sctx.strokeStyle = "#2ec4d6";
    sctx.lineWidth = 1.5;
    for (let i = 0; i < bins.length; i++) {
      const x = (i / (bins.length - 1)) * (w - 1);
      const n = (bins[i] - min) / span;
      const y = h - 4 - n * (h - 10);
      if (i === 0) sctx.moveTo(x, y);
      else sctx.lineTo(x, y);
    }
    sctx.stroke();
    drawTuneMarkers(sctx, w, h);

    const ww = waterfall.width;
    const wh = waterfall.height;
    if (!waterfallReady) {
      wctx.fillStyle = "#050b11";
      wctx.fillRect(0, 0, ww, wh);
      waterfallReady = true;
    }
    wctx.drawImage(waterfall, 0, 0, ww, wh - 1, 0, 1, ww, wh - 1);
    const row = wctx.createImageData(ww, 1);
    for (let x = 0; x < ww; x++) {
      const i = Math.min(bins.length - 1, Math.floor((x / (ww - 1)) * (bins.length - 1)));
      const n = Math.max(0, Math.min(1, (bins[i] - min) / span));
      const [r, g, b] = heat(n);
      const o = x * 4;
      row.data[o] = r;
      row.data[o + 1] = g;
      row.data[o + 2] = b;
      row.data[o + 3] = 255;
    }
    wctx.putImageData(row, 0, 0);
    // tune marker on top row of waterfall
    const tunedR = hzToRatio(spectrumMeta.tunedHz);
    if (tunedR >= 0 && tunedR <= 1) {
      const tx = Math.round(tunedR * (ww - 1));
      wctx.fillStyle = "rgba(240,180,41,0.9)";
      wctx.fillRect(tx, 0, 1, 3);
    }
  }

  function tuneFromPointer(canvas, evt) {
    if (!(spectrumMeta.spanHz > 0)) return;
    const ratio = pointerRatio(canvas, evt);
    const hz = ratioToHz(ratio);
    $("freqInput").value = hz;
    $("freqLabel").textContent = fmtHz(hz);
    spectrumMeta.tunedHz = hz;
    if (lastBins) drawSpectrum(lastBins);
    invoke("SetFrequency", hz);
  }

  function appendAf(evt) {
    if (!evt) return;
    const line = document.createElement("div");
    line.className = "af-line";
    const ticks = evt.utcTicks || evt.UtcTicks;
    let when = "";
    if (ticks) {
      const ms = (Number(ticks) - 621355968000000000) / 10000;
      if (Number.isFinite(ms)) when = new Date(ms).toLocaleTimeString();
    }
    let plugin = evt.pluginName || evt.pluginId || "AF";
    const lower = String(plugin).toLowerCase();
    if (lower.includes("ft8") && lower.includes("ft4")) {
      const mode = (evt.fields && (evt.fields.mode || evt.fields.Mode)) || "";
      plugin = String(mode).toUpperCase() === "FT4" ? "FT4" : "FT8";
    } else if (lower.includes("ft8 / ft4") || lower === "ft8 / ft4 decoder") {
      plugin = "FT8";
    }
    const kind = evt.kind || "";
    const freqHz = Number(evt.frequencyHz || evt.FrequencyHz || (evt.fields && (evt.fields.rfHz || evt.fields.freq)) || 0);
    const freqLabel = freqHz > 0 ? fmtHz(freqHz) : "";
    let text = evt.text || "";
    if (!text && evt.fields) {
      try { text = Object.entries(evt.fields).map(([k, v]) => k + "=" + v).join(" "); }
      catch (_) { text = ""; }
    }
    if (!text) text = JSON.stringify(evt);
    if (freqLabel && text.indexOf(freqLabel) !== 0 && !/^\d/.test(text.trim())) {
      text = freqLabel + "  " + text;
    }
    const metaParts = [when, plugin, freqLabel].filter(Boolean);
    if (kind && !String(kind).includes("DECODE")) metaParts.push(kind);
    line.innerHTML = `<div class="meta"></div><div class="body"></div>`;
    line.querySelector(".meta").textContent = metaParts.join(" · ");
    line.querySelector(".body").textContent = text;
    const feed = $("afFeed");
    feed.prepend(line);
    afLines.unshift(line);
    while (afLines.length > AF_MAX) {
      const old = afLines.pop();
      old?.remove();
    }
  }

  function renderModes(active) {
    const grid = $("modeGrid");
    grid.innerHTML = "";
    const list = (state?.modes && state.modes.length) ? state.modes : modes;
    for (const m of list) {
      const b = document.createElement("button");
      b.type = "button";
      b.className = "chip" + (String(m).toUpperCase() === String(active || "").toUpperCase() ? " active" : "");
      b.textContent = m;
      b.addEventListener("click", () => invoke("SetMode", m));
      grid.appendChild(b);
    }
  }

  function applyState(s) {
    if (!s) return;
    state = s;
    applying = true;
    try {
      const freq = s.frequencyHz;
      const bw = s.filterBandwidthHz ?? s.bandwidthHz;
      const gain = s.gainPercent ?? 0;
      const vol1 = s.volume1 ?? s.volume1Percent ?? 0;
      const vol2 = s.volume2 ?? s.volume2Percent ?? 0;
      const running = !!(s.running ?? s.isReceiving);
      const source = s.source ?? s.sourceName ?? "";

      $("freqLabel").textContent = fmtHz(freq);
      $("freqInput").value = Math.round(freq || 0);
      $("bwInput").value = Math.round(bw || 0);
      $("gainSlider").value = Math.round(gain);
      $("gainVal").textContent = Math.round(gain) + "%";
      $("vol1").value = Math.round(vol1);
      $("vol1Val").textContent = Math.round(vol1) + "%";
      $("vol2").value = Math.round(vol2);
      $("vol2Val").textContent = Math.round(vol2) + "%";
      $("sql1").checked = !!(s.squelch1Enabled ?? s.sql1Enabled);
      $("sql2").checked = !!(s.squelch2Enabled ?? s.sql2Enabled);
      $("sql1Thr").value = Math.round(s.squelch1Threshold ?? s.sql1ThresholdDb ?? -80);
      $("sql2Thr").value = Math.round(s.squelch2Threshold ?? s.sql2ThresholdDb ?? -80);
      $("rxBtn").textContent = running ? "RX STOP" : "RX START";
      $("rxBtn").classList.toggle("on", running);
      renderModes(s.mode || "USB");

      const sel = $("sourceSelect");
      const sources = s.availableSources || [];
      sel.innerHTML = "";
      for (const name of sources) {
        const opt = document.createElement("option");
        opt.value = name;
        opt.textContent = name;
        if (name === source) opt.selected = true;
        sel.appendChild(opt);
      }

      const plugins = (s.activeAfPlugins || []).join(", ");
      $("afPlugins").textContent = plugins ? "Active: " + plugins : "No AF plugins active";
      $("statusLine").textContent = (source || "") + (s.webUrl ? " · " + s.webUrl : "");
      const s1 = !!(s.squelch1Open ?? s.sql1Open);
      const s2 = !!(s.squelch2Open ?? s.sql2Open);
      $("sqlLeds").innerHTML =
        `<span class="led ${s1 ? "open" : ""}"><i></i>SQL1</span>` +
        `<span class="led ${s2 ? "open" : ""}"><i></i>SQL2</span>`;
      $("signalLabel").textContent = running ? ("RX · " + (Number.isFinite(s.signalDb) ? s.signalDb.toFixed(1) + " dB" : "")) : "idle";
      if (Number.isFinite(freq)) spectrumMeta.tunedHz = freq;
      if (Number.isFinite(s.viewBandwidthHz) && s.viewBandwidthHz > 0) spectrumMeta.spanHz = s.viewBandwidthHz;
      if (Number.isFinite(s.rfCenterHz) && s.rfCenterHz > 0 && !(spectrumMeta.spanHz > 0)) {
        spectrumMeta.centerHz = s.rfCenterHz;
      }
    } finally {
      applying = false;
    }
  }

  async function invoke(method, ...args) {
    if (!hub || hub.state !== signalR.HubConnectionState.Connected) return;
    try {
      await hub.invoke(method, ...args);
    } catch (e) {
      $("statusLine").textContent = String(e.message || e);
    }
  }

  function bindUi() {
    document.querySelectorAll(".tab").forEach((btn) => {
      btn.addEventListener("click", () => {
        document.querySelectorAll(".tab").forEach((b) => b.classList.remove("active"));
        document.querySelectorAll(".tab-panel").forEach((p) => p.classList.remove("active"));
        btn.classList.add("active");
        $("tab-" + btn.dataset.tab).classList.add("active");
        ensureAudio();
      });
    });

    $("rxBtn").addEventListener("click", () => {
      ensureAudio();
      const running = !!(state?.running ?? state?.isReceiving);
      invoke(running ? "StopRx" : "StartRx");
    });
    const speakerBtn = $("speakerBtn");
    if (speakerBtn) {
      speakerBtn.addEventListener("click", () => {
        ensureAudio();
        updateSpeakerBtn();
      });
    }
    $("freqApply").addEventListener("click", () => {
      const hz = Number($("freqInput").value);
      if (Number.isFinite(hz)) invoke("SetFrequency", hz);
    });
    document.querySelectorAll("[data-tune]").forEach((b) => {
      b.addEventListener("click", () => {
        const delta = Number(b.dataset.tune);
        const hz = Number($("freqInput").value || state?.frequencyHz || 0) + delta;
        $("freqInput").value = Math.round(hz);
        invoke("SetFrequency", hz);
      });
    });
    $("bwInput").addEventListener("change", () => {
      if (applying) return;
      invoke("SetBandwidth", Number($("bwInput").value));
    });
    $("gainSlider").addEventListener("input", () => {
      $("gainVal").textContent = $("gainSlider").value + "%";
    });
    $("gainSlider").addEventListener("change", () => {
      if (applying) return;
      invoke("SetGain", Number($("gainSlider").value));
    });
    $("sourceSelect").addEventListener("change", () => {
      if (applying) return;
      invoke("SetSource", $("sourceSelect").value);
    });
    $("webVol").addEventListener("input", () => {
      webGain = Number($("webVol").value) / 100;
      $("webVolVal").textContent = Math.round(webGain * 100) + "%";
      ensureAudio();
      if (gainNode) gainNode.gain.value = webGain;
    });
    $("vol1").addEventListener("change", () => {
      if (applying) return;
      invoke("SetVolume", 1, Number($("vol1").value));
    });
    $("vol2").addEventListener("change", () => {
      if (applying) return;
      invoke("SetVolume", 2, Number($("vol2").value));
    });
    $("sql1").addEventListener("change", () => {
      if (applying) return;
      invoke("SetSquelch", 1, $("sql1").checked, Number($("sql1Thr").value));
    });
    $("sql2").addEventListener("change", () => {
      if (applying) return;
      invoke("SetSquelch", 2, $("sql2").checked, Number($("sql2Thr").value));
    });
    $("sql1Thr").addEventListener("change", () => {
      if (applying) return;
      invoke("SetSquelch", 1, $("sql1").checked, Number($("sql1Thr").value));
    });
    $("sql2Thr").addEventListener("change", () => {
      if (applying) return;
      invoke("SetSquelch", 2, $("sql2").checked, Number($("sql2Thr").value));
    });

    ["pointerdown", "keydown", "touchstart"].forEach((ev) => {
      window.addEventListener(ev, () => ensureAudio(), { once: true, passive: true });
    });

    function bindTuneCanvas(canvas) {
      canvas.style.cursor = "crosshair";
      canvas.addEventListener("click", (e) => {
        e.preventDefault();
        tuneFromPointer(canvas, e);
      });
      canvas.addEventListener("pointermove", (e) => {
        if (!(spectrumMeta.spanHz > 0)) return;
        hoverRatio = pointerRatio(canvas, e);
        const hz = ratioToHz(hoverRatio);
        $("signalLabel").textContent = "tap → " + fmtHz(hz);
        if (lastBins) drawSpectrum(lastBins);
        else {
          sctx.fillStyle = "#050b11";
          sctx.fillRect(0, 0, spectrum.width, spectrum.height);
          drawTuneMarkers(sctx, spectrum.width, spectrum.height);
        }
      });
      canvas.addEventListener("pointerleave", () => {
        hoverRatio = -1;
        if (lastBins) drawSpectrum(lastBins);
      });
    }
    bindTuneCanvas(spectrum);
    bindTuneCanvas(waterfall);
  }

  async function connect() {
    const t = token();
    const url = "/hubs/radio" + (t ? "?access_token=" + encodeURIComponent(t) : "");
    hub = new signalR.HubConnectionBuilder()
      .withUrl(url)
      .withAutomaticReconnect()
      .build();

    hub.on("state", applyState);
    hub.on("spectrum", (payload) => {
      if (payload) {
        if (payload.centerHz != null) spectrumMeta.centerHz = Number(payload.centerHz);
        if (payload.spanHz != null) spectrumMeta.spanHz = Number(payload.spanHz);
        if (payload.tunedHz != null) spectrumMeta.tunedHz = Number(payload.tunedHz);
        if (payload.filterHz != null) spectrumMeta.filterHz = Number(payload.filterHz);
      }
      const bins = payload?.levels || payload?.bins;
      if (bins) drawSpectrum(bins);
      const tuned = payload?.tunedHz ?? payload?.frequencyHz;
      if (tuned != null) $("freqLabel").textContent = fmtHz(tuned);
    });
    hub.on("audio", (payload) => {
      playPcm16(payload?.pcm16Base64 || payload?.pcm16, payload?.sampleRate || 48000);
    });
    hub.on("af", appendAf);

    hub.onreconnecting(() => setConn("reconnecting…", false));
    hub.onreconnected(async () => {
      setConn("online", true);
      try { applyState(await hub.invoke("GetState")); } catch (_) {}
      try {
        const feed = await hub.invoke("GetAfFeed");
        if (Array.isArray(feed)) feed.forEach(appendAf);
      } catch (_) {}
    });
    hub.onclose(() => setConn("offline", false));

    setConn("connecting…", false);
    await hub.start();
    setConn("online", true);
    applyState(await hub.invoke("GetState"));
    try {
      const feed = await hub.invoke("GetAfFeed");
      if (Array.isArray(feed)) feed.forEach(appendAf);
    } catch (_) {}
  }

  let deferredPrompt = null;
  window.addEventListener("beforeinstallprompt", (e) => {
    e.preventDefault();
    deferredPrompt = e;
    $("installBtn").hidden = false;
  });
  $("installBtn").addEventListener("click", async () => {
    if (!deferredPrompt) return;
    deferredPrompt.prompt();
    await deferredPrompt.userChoice;
    deferredPrompt = null;
    $("installBtn").hidden = true;
  });

  if ("serviceWorker" in navigator) {
    navigator.serviceWorker.register("/sw.js").catch(() => {});
  }

  bindUi();
  connect().catch((e) => {
    setConn("failed", false);
    $("statusLine").textContent = String(e.message || e);
  });
})();
