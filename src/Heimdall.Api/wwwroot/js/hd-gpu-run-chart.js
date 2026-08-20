/**
 * Zoomable multi-metric charts (Flood Live Active + Run behaviour).
 * data-series: JSON [{t,gpu?,cpu?,ram?,diskW?,netTx?}, ...] — legacy {t,g} = GPU only.
 * Left axis 0–100%: GPU / CPU / RAM. Right axis MB/s: Disk W / Net Tx.
 * Checkboxes toggle series (default all on).
 */
(function () {
  const PAD = { l: 44, r: 48, t: 10, b: 34 };

  const SERIES = [
    { key: "gpu", label: "GPU", axis: "pct", color: "#3db8a0", tip: "%" },
    { key: "cpu", label: "CPU", axis: "pct", color: "#5BA3D9", tip: "%" },
    { key: "ram", label: "RAM", axis: "pct", color: "#c47a1a", tip: "%" },
    { key: "diskW", label: "Disk W", axis: "rate", color: "#9b59b6", tip: " MB/s" },
    { key: "netTx", label: "Net Tx", axis: "rate", color: "#c45c5c", tip: " MB/s" }
  ];

  const NICE_STEPS = [
    1, 2, 5, 10, 15, 30,
    60, 120, 300, 600, 900, 1800,
    3600, 7200, 10800, 14400, 21600, 43200, 86400
  ];

  const RATE_NICE = [0.1, 0.2, 0.5, 1, 2, 5, 10, 20, 50, 100, 200, 500, 1000, 2000, 5000];

  function numOrNull(v) {
    if (v == null || v === "") return null;
    const n = Number(v);
    return Number.isFinite(n) ? n : null;
  }

  function normalizePoint(p) {
    if (!p || !Number.isFinite(Number(p.t))) return null;
    const t = Number(p.t);
    // Legacy {t,g} → gpu
    const gpu = numOrNull(p.gpu) ?? numOrNull(p.g);
    return {
      t,
      gpu,
      cpu: numOrNull(p.cpu),
      ram: numOrNull(p.ram),
      diskW: numOrNull(p.diskW),
      netTx: numOrNull(p.netTx)
    };
  }

  function parseSeries(el) {
    const raw = (el.getAttribute("data-series") || "[]").replace(/&quot;/g, '"');
    try {
      const arr = JSON.parse(raw);
      if (!Array.isArray(arr)) return [];
      return arr
        .map(normalizePoint)
        .filter((p) => p && Number.isFinite(p.t))
        .sort((a, b) => a.t - b.t);
    } catch {
      return [];
    }
  }

  function serializePoints(points) {
    return JSON.stringify(
      points.map((p) => {
        const o = { t: p.t };
        if (p.gpu != null) o.gpu = Math.round(p.gpu * 100) / 100;
        if (p.cpu != null) o.cpu = Math.round(p.cpu * 100) / 100;
        if (p.ram != null) o.ram = Math.round(p.ram * 100) / 100;
        if (p.diskW != null) o.diskW = Math.round(p.diskW * 1000) / 1000;
        if (p.netTx != null) o.netTx = Math.round(p.netTx * 1000) / 1000;
        return o;
      })
    ).replace(/"/g, "&quot;");
  }

  function medianDelta(points) {
    if (points.length < 2) return 10;
    const deltas = [];
    for (let i = 1; i < points.length; i++) {
      const d = points[i].t - points[i - 1].t;
      if (d > 0) deltas.push(d);
    }
    if (deltas.length === 0) return 10;
    deltas.sort((a, b) => a - b);
    return deltas[Math.floor(deltas.length / 2)] || 10;
  }

  function pad2(n) {
    return String(n).padStart(2, "0");
  }

  function formatTime(unix, withSeconds) {
    const d = new Date(unix * 1000);
    const base = `${pad2(d.getHours())}:${pad2(d.getMinutes())}`;
    return withSeconds ? `${base}:${pad2(d.getSeconds())}` : base;
  }

  function formatElapsed(seconds) {
    const s = Math.max(0, Math.floor(seconds));
    const h = Math.floor(s / 3600);
    const m = Math.floor((s % 3600) / 60);
    const sec = s % 60;
    if (h > 0) return `${h}h ${pad2(m)}m ${pad2(sec)}s`;
    if (m > 0) return `${m}m ${pad2(sec)}s`;
    return `${sec}s`;
  }

  function formatRate(v) {
    if (v == null) return "—";
    if (Math.abs(v) < 0.05) return "0";
    if (v >= 100) return v.toFixed(0);
    if (v >= 10) return v.toFixed(1);
    return v.toFixed(2);
  }

  function chooseStep(spanSec, maxTicks) {
    const target = Math.max(2, maxTicks);
    for (let i = 0; i < NICE_STEPS.length; i++) {
      if (spanSec / NICE_STEPS[i] <= target) return NICE_STEPS[i];
    }
    return NICE_STEPS[NICE_STEPS.length - 1];
  }

  function buildTimeTicks(viewStart, viewEnd, maxTicks) {
    const span = Math.max(viewEnd - viewStart, 1);
    const step = chooseStep(span, maxTicks);
    const withSeconds = step < 60;
    const tzOffsetSec = new Date(viewStart * 1000).getTimezoneOffset() * 60;
    const localStart = viewStart - tzOffsetSec;
    const firstLocal = Math.ceil(localStart / step) * step;
    const ticks = [];
    for (let localSec = firstLocal; ; localSec += step) {
      const utc = localSec + tzOffsetSec;
      if (utc > viewEnd + 0.5) break;
      if (utc >= viewStart - 0.5) ticks.push(utc);
      if (ticks.length > maxTicks + 2) break;
    }
    if (ticks.length === 0) {
      ticks.push(viewStart);
      if (viewEnd - viewStart > 1) ticks.push(viewEnd);
    }
    return { ticks, withSeconds };
  }

  function niceRateMax(maxVal) {
    const m = Math.max(maxVal, 0.1);
    for (let i = 0; i < RATE_NICE.length; i++) {
      if (RATE_NICE[i] >= m) return RATE_NICE[i];
    }
    return Math.ceil(m / 1000) * 1000;
  }

  function cssVar(el, name, fallback) {
    const v = getComputedStyle(el).getPropertyValue(name).trim();
    return v || fallback;
  }

  function ensureTooltip(root) {
    let tip = root.querySelector(".hd-gpu-chart-tooltip");
    if (tip) return tip;
    tip = document.createElement("div");
    tip.className = "hd-gpu-chart-tooltip";
    tip.hidden = true;
    tip.setAttribute("role", "tooltip");
    root.style.position = root.style.position || "relative";
    root.appendChild(tip);
    return tip;
  }

  function ensureSeriesToggles(root) {
    let bar = root.querySelector("[data-series-toggles]");
    if (bar) return bar;
    const toolbar = root.querySelector(".hd-gpu-chart-toolbar");
    bar = document.createElement("div");
    bar.className = "hd-gpu-chart-series";
    bar.setAttribute("data-series-toggles", "1");
    SERIES.forEach((s) => {
      const id = "hd-ser-" + s.key + "-" + Math.random().toString(36).slice(2, 8);
      const label = document.createElement("label");
      label.className = "hd-gpu-series-toggle";
      label.style.setProperty("--ser-color", s.color);
      label.innerHTML =
        `<input type="checkbox" data-series-key="${s.key}" checked />` +
        `<span class="hd-gpu-series-swatch" aria-hidden="true"></span>${s.label}`;
      bar.appendChild(label);
    });
    if (toolbar) toolbar.appendChild(bar);
    else root.insertBefore(bar, root.firstChild);
    return bar;
  }

  function nearestPoint(points, t) {
    if (points.length === 0) return null;
    let best = points[0];
    let bestDist = Math.abs(points[0].t - t);
    for (let i = 1; i < points.length; i++) {
      const d = Math.abs(points[i].t - t);
      if (d < bestDist) {
        best = points[i];
        bestDist = d;
      }
    }
    return best;
  }

  function initChart(root) {
    const canvas = root.querySelector(".hd-gpu-chart-canvas");
    const rangeLabel = root.querySelector("[data-range-label]");
    if (!canvas) return;
    const tip = ensureTooltip(root);
    const toggles = ensureSeriesToggles(root);

    let fullStart = Number(root.getAttribute("data-start-unix")) || 0;
    let fullEnd = Number(root.getAttribute("data-end-unix")) || fullStart + 1;
    let points = parseSeries(root);
    let minSpan = Math.max(medianDelta(points), 5);

    let viewStart = fullStart;
    let viewEnd = Math.max(fullEnd, fullStart + minSpan);
    let dragging = false;
    let dragOriginX = 0;
    let dragOriginStart = 0;
    let dragOriginEnd = 0;
    let hover = null;
    let followLive = true;

    const enabled = {};
    SERIES.forEach((s) => {
      enabled[s.key] = true;
    });

    function isEnabled(key) {
      return !!enabled[key];
    }

    function setRangeLabel() {
      if (!rangeLabel) return;
      const n = points.filter((p) => p.t >= viewStart && p.t <= viewEnd).length;
      rangeLabel.textContent = `${formatTime(viewStart, true)} → ${formatTime(viewEnd, true)} · ${n} pts`;
    }

    function layout() {
      const dpr = window.devicePixelRatio || 1;
      const cssW = Math.max(320, root.clientWidth - 8);
      const cssH = 168;
      canvas.width = Math.floor(cssW * dpr);
      canvas.height = Math.floor(cssH * dpr);
      canvas.style.width = cssW + "px";
      canvas.style.height = cssH + "px";
      return { dpr, cssW, cssH, plotW: cssW - PAD.l - PAD.r, plotH: cssH - PAD.t - PAD.b };
    }

    function rateMaxInView(visible) {
      let max = 0;
      visible.forEach((p) => {
        if (isEnabled("diskW") && p.diskW != null) max = Math.max(max, p.diskW);
        if (isEnabled("netTx") && p.netTx != null) max = Math.max(max, p.netTx);
      });
      return niceRateMax(max);
    }

    function draw() {
      const { dpr, cssW: w, cssH: h, plotW, plotH } = layout();
      const ctx = canvas.getContext("2d");
      if (!ctx) return;
      ctx.setTransform(dpr, 0, 0, dpr, 0, 0);

      const grid = cssVar(root, "--hd-border", "rgba(128,128,128,0.35)");
      const text = cssVar(root, "--hd-muted", "#9aa3b2");

      ctx.clearRect(0, 0, w, h);

      ctx.strokeStyle = grid;
      ctx.lineWidth = 1;
      ctx.beginPath();
      ctx.moveTo(PAD.l, PAD.t);
      ctx.lineTo(PAD.l, PAD.t + plotH);
      ctx.lineTo(PAD.l + plotW, PAD.t + plotH);
      ctx.lineTo(PAD.l + plotW, PAD.t);
      ctx.stroke();

      ctx.fillStyle = text;
      ctx.font = "11px Segoe UI, system-ui, sans-serif";
      [0, 25, 50, 75, 100].forEach((pct) => {
        const y = PAD.t + plotH * (1 - pct / 100);
        ctx.globalAlpha = 0.3;
        ctx.beginPath();
        ctx.moveTo(PAD.l, y);
        ctx.lineTo(PAD.l + plotW, y);
        ctx.stroke();
        ctx.globalAlpha = 1;
        ctx.fillText(String(pct), 6, y + 3);
      });

      const span = Math.max(viewEnd - viewStart, 1);
      const xOf = (t) => PAD.l + ((t - viewStart) / span) * plotW;
      const yPct = (v) => PAD.t + plotH * (1 - Math.min(100, Math.max(0, v)) / 100);

      const visible = points.filter((p) => p.t >= viewStart && p.t <= viewEnd);
      const rMax = rateMaxInView(visible);
      const yRate = (v) => PAD.t + plotH * (1 - Math.min(rMax, Math.max(0, v)) / rMax);

      // Right axis labels when any rate series is on.
      const anyRate =
        (isEnabled("diskW") || isEnabled("netTx")) &&
        visible.some((p) => (isEnabled("diskW") && p.diskW != null) || (isEnabled("netTx") && p.netTx != null));
      if (anyRate) {
        ctx.fillStyle = text;
        ctx.textAlign = "left";
        [0, 0.5, 1].forEach((frac) => {
          const val = rMax * (1 - frac);
          const y = PAD.t + plotH * frac;
          const label = formatRate(val) + (frac === 0 ? "" : "");
          ctx.fillText(formatRate(rMax * (1 - frac)), PAD.l + plotW + 4, y + 3);
        });
        ctx.fillText("MB/s", PAD.l + plotW + 4, PAD.t + plotH + 14);
      }

      const { ticks, withSeconds } = buildTimeTicks(viewStart, viewEnd, 20);
      ctx.fillStyle = text;
      ctx.textAlign = "center";
      ticks.forEach((t) => {
        const x = xOf(t);
        if (x < PAD.l - 2 || x > PAD.l + plotW + 2) return;
        ctx.globalAlpha = 0.25;
        ctx.beginPath();
        ctx.moveTo(x, PAD.t);
        ctx.lineTo(x, PAD.t + plotH);
        ctx.stroke();
        ctx.globalAlpha = 1;
        ctx.fillText(formatTime(t, withSeconds), x, h - 8);
      });
      ctx.textAlign = "left";

      if (visible.length === 0) {
        ctx.fillStyle = text;
        ctx.fillText("No samples in this window", PAD.l + 8, PAD.t + plotH / 2);
        setRangeLabel();
        return;
      }

      SERIES.forEach((s) => {
        if (!isEnabled(s.key)) return;
        const yOf = s.axis === "pct" ? yPct : yRate;
        ctx.strokeStyle = s.color;
        ctx.lineWidth = 1.75;
        ctx.lineJoin = "round";
        ctx.lineCap = "round";
        ctx.beginPath();
        let started = false;
        visible.forEach((p) => {
          const v = p[s.key];
          if (v == null) {
            started = false;
            return;
          }
          const x = xOf(p.t);
          const y = yOf(s.axis === "pct" ? Math.min(100, Math.max(0, v)) : v);
          if (!started) {
            ctx.moveTo(x, y);
            started = true;
          } else ctx.lineTo(x, y);
        });
        ctx.stroke();
      });

      if (hover) {
        ctx.strokeStyle = text;
        ctx.globalAlpha = 0.45;
        ctx.setLineDash([4, 3]);
        ctx.beginPath();
        ctx.moveTo(hover.x, PAD.t);
        ctx.lineTo(hover.x, PAD.t + plotH);
        ctx.stroke();
        ctx.setLineDash([]);
        ctx.globalAlpha = 1;
        SERIES.forEach((s) => {
          if (!isEnabled(s.key) || hover.p[s.key] == null) return;
          const yOf = s.axis === "pct" ? yPct : yRate;
          const v = hover.p[s.key];
          const y = yOf(s.axis === "pct" ? Math.min(100, Math.max(0, v)) : v);
          ctx.fillStyle = s.color;
          ctx.beginPath();
          ctx.arc(hover.x, y, 3.5, 0, Math.PI * 2);
          ctx.fill();
        });
      }

      setRangeLabel();
    }

    function hideTip() {
      hover = null;
      tip.hidden = true;
      draw();
    }

    function updateHover(clientX, clientY) {
      if (dragging) return;
      const rect = canvas.getBoundingClientRect();
      const x = clientX - rect.left;
      const y = clientY - rect.top;
      const plotW = Math.max(1, rect.width - PAD.l - PAD.r);
      const plotH = Math.max(1, rect.height - PAD.t - PAD.b);

      if (x < PAD.l || x > PAD.l + plotW || y < PAD.t || y > PAD.t + plotH) {
        hideTip();
        return;
      }

      const span = Math.max(viewEnd - viewStart, 1);
      const tAt = viewStart + ((x - PAD.l) / plotW) * span;
      const inView = points.filter((p) => p.t >= viewStart && p.t <= viewEnd);
      const p = nearestPoint(inView.length ? inView : points, tAt);
      if (!p) {
        hideTip();
        return;
      }

      const hx = PAD.l + ((p.t - viewStart) / span) * plotW;
      hover = { t: p.t, p, x: hx };

      const lines = [];
      SERIES.forEach((s) => {
        if (!isEnabled(s.key) || p[s.key] == null) return;
        const v = p[s.key];
        if (s.axis === "pct") {
          const shown = Math.min(100, Math.max(0, v));
          const extra = v > 100.05 ? ` <span class="hd-gpu-tip-elapsed">(raw ${v.toFixed(0)}%)</span>` : "";
          lines.push(
            `<span style="color:${s.color}"><strong>${shown.toFixed(1)}%</strong> ${s.label}</span>${extra}`
          );
        } else {
          lines.push(
            `<span style="color:${s.color}"><strong>${formatRate(v)}</strong> ${s.label}${s.tip}</span>`
          );
        }
      });
      if (lines.length === 0) lines.push('<span class="hd-gpu-tip-elapsed">No enabled series</span>');

      tip.innerHTML =
        lines.join("<br>") +
        `<br>${formatTime(p.t, true)}` +
        `<br><span class="hd-gpu-tip-elapsed">elapsed ${formatElapsed(p.t - fullStart)}</span>`;
      tip.hidden = false;

      const rootRect = root.getBoundingClientRect();
      let left = clientX - rootRect.left + 12;
      let top = clientY - rootRect.top + 16;
      tip.style.left = "0px";
      tip.style.top = "0px";
      const tipW = tip.offsetWidth || 140;
      const tipH = tip.offsetHeight || 64;
      if (left + tipW > rootRect.width - 4) left = clientX - rootRect.left - tipW - 12;
      if (top + tipH > rootRect.height - 4) top = clientY - rootRect.top - tipH - 12;
      tip.style.left = Math.max(4, left) + "px";
      tip.style.top = Math.max(4, top) + "px";

      draw();
    }

    function clampView(start, end) {
      let s = start;
      let e = end;
      let span = e - s;
      if (span < minSpan) {
        const mid = (s + e) / 2;
        s = mid - minSpan / 2;
        e = mid + minSpan / 2;
        span = minSpan;
      }
      const fullSpan = Math.max(fullEnd - fullStart, minSpan);
      if (span > fullSpan) {
        s = fullStart;
        e = fullStart + fullSpan;
      } else {
        if (s < fullStart) {
          e += fullStart - s;
          s = fullStart;
        }
        if (e > fullEnd) {
          s -= e - fullEnd;
          e = fullEnd;
          if (s < fullStart) s = fullStart;
        }
      }
      viewStart = s;
      viewEnd = e;
    }

    function zoomAt(frac, factor) {
      followLive = false;
      const span = viewEnd - viewStart;
      const center = viewStart + span * frac;
      const next = span * factor;
      clampView(center - next * frac, center + next * (1 - frac));
      draw();
    }

    function setData(seriesPoints, startUnix, endUnix, opts) {
      const keepZoom = opts && opts.keepZoom;
      points = Array.isArray(seriesPoints)
        ? seriesPoints.map(normalizePoint).filter((p) => p && Number.isFinite(p.t)).sort((a, b) => a.t - b.t)
        : [];
      fullStart = Number(startUnix) || fullStart;
      fullEnd = Number(endUnix) || Math.max(fullEnd, fullStart + 1);
      minSpan = Math.max(medianDelta(points), 5);
      root.setAttribute("data-start-unix", String(fullStart));
      root.setAttribute("data-end-unix", String(fullEnd));
      root.setAttribute("data-series", serializePoints(points));
      if (!keepZoom || followLive) {
        followLive = true;
        clampView(fullStart, fullEnd);
      } else {
        clampView(viewStart, viewEnd);
      }
      draw();
    }

    toggles.addEventListener("change", (ev) => {
      const inp = ev.target;
      if (!inp || !inp.getAttribute || inp.getAttribute("data-series-key") == null) return;
      enabled[inp.getAttribute("data-series-key")] = !!inp.checked;
      draw();
    });

    root.querySelector("[data-zoom-in]")?.addEventListener("click", () => zoomAt(0.5, 0.6));
    root.querySelector("[data-zoom-out]")?.addEventListener("click", () => {
      followLive = false;
      zoomAt(0.5, 1.6);
    });
    root.querySelector("[data-zoom-reset]")?.addEventListener("click", () => {
      followLive = true;
      clampView(fullStart, fullEnd);
      draw();
    });

    canvas.addEventListener(
      "wheel",
      (ev) => {
        ev.preventDefault();
        followLive = false;
        const rect = canvas.getBoundingClientRect();
        const x = ev.clientX - rect.left;
        const plotW = Math.max(1, rect.width - PAD.l - PAD.r);
        const frac = Math.min(1, Math.max(0, (x - PAD.l) / plotW));
        zoomAt(frac, ev.deltaY < 0 ? 0.75 : 1.35);
      },
      { passive: false }
    );

    canvas.addEventListener("mousedown", (ev) => {
      if (ev.button !== 0) return;
      dragging = true;
      followLive = false;
      dragOriginX = ev.clientX;
      dragOriginStart = viewStart;
      dragOriginEnd = viewEnd;
      canvas.style.cursor = "grabbing";
      tip.hidden = true;
    });
    window.addEventListener("mouseup", () => {
      dragging = false;
      canvas.style.cursor = "crosshair";
    });
    window.addEventListener("mousemove", (ev) => {
      if (!dragging) return;
      const rect = canvas.getBoundingClientRect();
      const plotW = Math.max(1, rect.width - PAD.l - PAD.r);
      const span = dragOriginEnd - dragOriginStart;
      const dx = ev.clientX - dragOriginX;
      const dt = (-dx / plotW) * span;
      clampView(dragOriginStart + dt, dragOriginEnd + dt);
      draw();
    });

    canvas.addEventListener("mousemove", (ev) => {
      if (dragging) return;
      updateHover(ev.clientX, ev.clientY);
    });
    canvas.addEventListener("mouseleave", hideTip);

    canvas.style.cursor = "crosshair";
    window.addEventListener("resize", draw);
    clampView(fullStart, fullEnd);
    draw();

    root._hdGpuChart = { setData, draw };
  }

  function boot(root) {
    const scope = root && root.querySelectorAll ? root : document;
    scope.querySelectorAll(".hd-gpu-chart").forEach((el) => {
      if (el.getAttribute("data-chart-ready") === "1") return;
      el.setAttribute("data-chart-ready", "1");
      initChart(el);
    });
  }

  if (document.readyState === "loading") {
    document.addEventListener("DOMContentLoaded", () => boot(document));
  } else {
    boot(document);
  }

  window.HeimdallGpuRunCharts = {
    init: boot,
    setData: function (el, seriesPoints, startUnix, endUnix, opts) {
      if (!el) return;
      if (!el._hdGpuChart) {
        el.removeAttribute("data-chart-ready");
        boot(el.parentElement || document);
      }
      if (el._hdGpuChart) el._hdGpuChart.setData(seriesPoints, startUnix, endUnix, opts);
    }
  };
})();
