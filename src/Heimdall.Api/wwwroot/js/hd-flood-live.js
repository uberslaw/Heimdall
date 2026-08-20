/**
 * Flood Live: shared SSE stream for table + Active GPU charts.
 * Falls back to 30s HTML partial refresh if EventSource is unavailable.
 */
(function () {
  "use strict";

  const ONLINE_MS = 60 * 1000;
  const FALLBACK_MS = 30000;

  function pad2(n) {
    return String(n).padStart(2, "0");
  }

  function formatGauge(v) {
    if (v == null || !Number.isFinite(Number(v))) return "—";
    return Number(v).toFixed(1).replace(/\.0$/, "") + "%";
  }

  function formatHours(h) {
    if (h == null || !Number.isFinite(Number(h))) return "0.0";
    const n = Number(h);
    if (Math.abs(n) < 0.05) return "0.0";
    return n.toFixed(1);
  }

  function formatDataFromMb(mb) {
    if (mb == null || !Number.isFinite(Number(mb))) return "—";
    if (Math.abs(mb) < 0.0005) return "0\u00A0MB";
    return formatDataSize(Number(mb) * 1024 * 1024);
  }

  function formatDataRateFromMBps(mbps) {
    if (mbps == null || !Number.isFinite(Number(mbps))) return "—";
    if (Math.abs(mbps) < 0.0005) return "0";
    return formatDataSize(Number(mbps) * 1024 * 1024) + "/s";
  }

  function formatDataSize(bytes) {
    var v = Math.abs(bytes);
    var units = ["B", "KB", "MB", "GB", "TB", "PB"];
    var u = 0;
    while (v >= 1000 && u < units.length - 1) {
      v /= 1000;
      u++;
    }
    var digits = v >= 100 ? 0 : v >= 10 ? 1 : 2;
    return v.toFixed(digits) + "\u00A0" + units[u];
  }

  function localParts(isoOrDate) {
    var d = isoOrDate instanceof Date ? isoOrDate : new Date(isoOrDate);
    return {
      time: pad2(d.getHours()) + ":" + pad2(d.getMinutes()),
      date: pad2(d.getDate()) + "/" + pad2(d.getMonth() + 1)
    };
  }

  function statusHtml(row) {
    var state = (row.detectedRunState || "").toLowerCase();
    if (
      (state === "active" || state === "watching") &&
      row.detectedRunStartedUtc
    ) {
      var s = localParts(row.detectedRunStartedUtc);
      return (
        '<span class="hd-active-stamp hd-status-text hd-status-active" title="Detected run start">' +
        '<span class="hd-active-stamp-time">' +
        s.time +
        "</span>" +
        '<span class="hd-active-stamp-date">' +
        s.date +
        "</span></span>"
      );
    }
    if (state === "ended" && row.detectedRunEndedUtc) {
      var e = localParts(row.detectedRunEndedUtc);
      return (
        '<span class="hd-active-stamp hd-status-text hd-status-off" title="Detected run stop">' +
        '<span class="hd-active-stamp-time">' +
        e.time +
        "</span>" +
        '<span class="hd-active-stamp-date">' +
        e.date +
        "</span></span>"
      );
    }
    var st = (row.status || "").toLowerCase();
    if (st === "active")
      return '<span class="hd-status-text hd-status-active">Active</span>';
    if (st === "idle")
      return '<span class="hd-status-text">Idle</span>';
    if (st === "notrunning")
      return '<span class="text-secondary" title="TUFLOW not running">N/A</span>';
    return '<span class="text-secondary">—</span>';
  }

  function activeSortValue(row) {
    var tier = 10000000000;
    var state = (row.detectedRunState || "").toLowerCase();
    if (
      (state === "active" || state === "watching") &&
      row.detectedRunStartedUtc
    )
      return 2 * tier + Math.floor(new Date(row.detectedRunStartedUtc).getTime() / 1000);
    if (state === "ended" && row.detectedRunEndedUtc)
      return 1 * tier + Math.floor(new Date(row.detectedRunEndedUtc).getTime() / 1000);
    var st = (row.status || "").toLowerCase();
    if (st === "active") return 2;
    if (st === "idle") return 1;
    if (st === "notrunning") return 0;
    return -1;
  }

  function userDisplay(u) {
    if (!u) return "—";
    var i = u.lastIndexOf("\\");
    return i >= 0 ? u.slice(i + 1) : u;
  }

  function matchesFilter(row, q, statusFilter) {
    var st = (row.status || "").toLowerCase();
    if (statusFilter && statusFilter !== "all") {
      if (statusFilter === "active" && st !== "active") return false;
      if (statusFilter === "idle" && st !== "idle") return false;
      if (
        (statusFilter === "notrunning" ||
          statusFilter === "not-running" ||
          statusFilter === "na") &&
        st !== "notrunning"
      )
        return false;
    }
    if (!q) return true;
    var hay = [
      row.hostname,
      row.displayName,
      row.friendlyName,
      row.username,
      row.lastIp
    ]
      .filter(Boolean)
      .join(" ")
      .toLowerCase();
    return hay.indexOf(q) >= 0;
  }

  function chartCardHtml(c) {
    var user = userDisplay(c.username);
    var startLabel = c.startUnix
      ? localParts(new Date(c.startUnix * 1000)).time +
        " " +
        localParts(new Date(c.startUnix * 1000)).date
      : "—";
    return (
      '<div class="hd-run-block hd-flood-live-chart" data-live-chart-machine="' +
      c.machineId +
      '">' +
      '<div class="hd-run-meta-row">' +
      '<div class="hd-flood-live-chart-meta">' +
      "<strong>" +
      escapeHtml(c.label) +
      "</strong>" +
      '<span class="text-secondary small ms-2">' +
      escapeHtml(user) +
      "</span>" +
      '<span class="text-secondary small ms-2">start ' +
      startLabel +
      "</span>" +
      "</div>" +
      '<button type="button" class="btn btn-sm btn-outline-secondary hd-run-gpu-toggle" data-gpu-toggle aria-pressed="true">Hide GPU</button>' +
      "</div>" +
      '<div class="hd-gpu-chart" data-gpu-panel data-start-unix="' +
      c.startUnix +
      '" data-end-unix="' +
      c.endUnix +
      '" data-series="[]">' +
      '<div class="hd-gpu-chart-toolbar">' +
      '<span class="hd-gpu-chart-title">Live util over run</span>' +
      '<span class="hd-gpu-chart-range text-secondary small" data-range-label></span>' +
      '<div class="hd-gpu-chart-actions">' +
      '<button type="button" class="btn btn-sm btn-outline-secondary" data-zoom-out title="Zoom out">−</button>' +
      '<button type="button" class="btn btn-sm btn-outline-secondary" data-zoom-in title="Zoom in">+</button>' +
      '<button type="button" class="btn btn-sm btn-outline-secondary" data-zoom-reset title="Show full run">Reset</button>' +
      "</div></div>" +
      '<canvas class="hd-gpu-chart-canvas" width="900" height="140" aria-label="Utilization over time"></canvas>' +
      '<p class="hd-gpu-chart-hint text-secondary small mb-0">Shared live stream · scroll to zoom</p>' +
      "</div></div>"
    );
  }

  function escapeHtml(s) {
    return String(s || "")
      .replace(/&/g, "&amp;")
      .replace(/</g, "&lt;")
      .replace(/>/g, "&gt;")
      .replace(/"/g, "&quot;");
  }

  function boot(root) {
    root = root && root.querySelector ? root.querySelector("[data-flood-live-root]") || root : null;
    if (!root || !root.hasAttribute || !root.hasAttribute("data-flood-live-root")) {
      root = document.querySelector("[data-flood-live-root]");
    }
    if (!root || root.dataset.floodLiveBound === "1") return;
    root.dataset.floodLiveBound = "1";

    var liveOnly = root.getAttribute("data-live-only") === "1";
    var form = root.querySelector("[data-flood-live-filter]");
    var chartsHost = root.querySelector("[data-flood-live-charts]");
    var lastPayload = null;
    var nextAt = Date.now() + FALLBACK_MS;
    var usingSse = false;
    var fallbackTimer = null;
    var countdownTimer = null;

    function filterState() {
      var q = "";
      var status = "all";
      if (form) {
        var qEl = form.querySelector('[name="q"]');
        var sEl = form.querySelector('[name="statusFilter"]');
        if (qEl) q = (qEl.value || "").trim().toLowerCase();
        if (sEl) status = (sEl.value || "all").toLowerCase();
      }
      return { q: q, status: status };
    }

    function updateMeta(filtered, total, label) {
      var meta = root.querySelector('[data-live="meta"]');
      if (!meta) return;
      meta.setAttribute("data-filtered", String(filtered));
      meta.setAttribute("data-total", String(total));
      var countEl = meta.querySelector("[data-live-meta-count]");
      if (countEl) countEl.textContent = filtered + " / " + total + " enrolled";
      var cd = meta.querySelector("[data-live-meta-countdown]");
      if (cd && label) cd.textContent = label;
    }

    function tickCountdown() {
      var cd = root.querySelector("[data-live-meta-countdown]");
      if (!cd) return;
      if (usingSse) {
        cd.textContent = "Live stream · shared update";
        return;
      }
      var remaining = Math.max(0, Math.ceil((nextAt - Date.now()) / 1000));
      cd.textContent = "Next update in " + remaining + "s";
    }

    function patchRow(tr, row) {
      var offline =
        !row.lastSeenUtc ||
        Date.now() - new Date(row.lastSeenUtc).getTime() > ONLINE_MS;
      var hostCell = tr.querySelector('[data-live="host"]');
      if (hostCell) {
        hostCell.setAttribute("data-sort-value", row.displayName || row.hostname);
        hostCell.title = offline
          ? "Last agent check-in: offline"
          : row.hostname;
        var link = hostCell.querySelector(".hd-live-host");
        if (link) {
          link.textContent = row.displayName || row.hostname;
          link.classList.toggle("hd-live-host-offline", offline);
        } else {
          var plain = hostCell.querySelector("[data-live-host-text]");
          if (plain) plain.textContent = row.displayName || row.hostname;
        }
      }
      var userCell = tr.querySelector('[data-live="user"]');
      if (userCell) {
        var u = userDisplay(row.username);
        userCell.textContent = u;
        userCell.setAttribute("data-sort-value", u === "—" ? "" : u);
      }
      var statusCell = tr.querySelector('[data-live="status"]');
      if (statusCell) {
        statusCell.innerHTML = statusHtml(row);
        statusCell.setAttribute("data-sort-value", String(activeSortValue(row)));
      }
      function metric(key, text, sort) {
        var cell = tr.querySelector('[data-live="' + key + '"]');
        if (!cell) return;
        cell.textContent = text;
        cell.setAttribute("data-sort-value", String(sort));
      }
      metric("gpu", formatGauge(row.gpuPercent), row.gpuPercent == null ? -1 : row.gpuPercent);
      metric("cpu", formatGauge(row.cpuPercent), row.cpuPercent == null ? -1 : row.cpuPercent);
      metric("gpumem", formatDataFromMb(row.gpuMemoryUsedMb), row.gpuMemoryUsedMb == null ? -1 : row.gpuMemoryUsedMb);
      metric("ram", formatDataFromMb(row.ramUsedMb), row.ramUsedMb == null ? -1 : row.ramUsedMb);
      metric("diskr", formatDataRateFromMBps(row.diskReadMBps), row.diskReadMBps == null ? -1 : row.diskReadMBps);
      metric("diskw", formatDataRateFromMBps(row.diskWriteMBps), row.diskWriteMBps == null ? -1 : row.diskWriteMBps);
      metric("netin", formatDataRateFromMBps(row.networkInMBps), row.networkInMBps == null ? -1 : row.networkInMBps);
      metric("netout", formatDataRateFromMBps(row.networkOutMBps), row.networkOutMBps == null ? -1 : row.networkOutMBps);
      metric("runtime", formatHours(row.todayRuntimeHours) + "\u00A0h", row.todayRuntimeHours);
      metric("activeh", formatHours(row.todayActiveHours) + "\u00A0h", row.todayActiveHours);
      metric("gpuh", formatHours(row.todayGpuHours) + "\u00A0h", row.todayGpuHours);
      metric("hpc", String(row.hpcSeats || 0), row.hpcSeats || 0);
      metric("classic", String(row.classicSeats || 0), row.classicSeats || 0);
      tr.hidden = false;
    }

    function formatLicenseStrip(lic) {
      if (!lic) return "CodeMeter: —";
      if (!lic.enabled) return "CodeMeter licenses: off";
      if (!lic.available) return "CodeMeter: waiting for first poll…";
      function pool(used, total, avail) {
        if (used == null) return "—/" + total;
        var s = used + "/" + total;
        if (avail != null) s += " (" + avail + " free)";
        return s;
      }
      function age() {
        if (!lic.queriedAtUtc) return "";
        var sec = Math.max(0, Math.floor((Date.now() - new Date(lic.queriedAtUtc).getTime()) / 1000));
        return sec < 90 ? sec + "s ago" : Math.floor(sec / 60) + "m ago";
      }
      var poll =
        lic.pollDurationMs >= 1000
          ? (lic.pollDurationMs / 1000).toFixed(1) + "s"
          : Math.round(lic.pollDurationMs || 0) + "ms";
      var text =
        "HPC " +
        pool(lic.hpcUsed, lic.hpcTotal, lic.hpcAvailable) +
        " · Classic " +
        pool(lic.classicUsed, lic.classicTotal, lic.classicAvailable) +
        " · poll " +
        poll +
        " · " +
        age();
      if (lic.partial) text += " · partial";
      var other = (lic.unmatchedHpc || 0) + (lic.unmatchedClassic || 0);
      if (other > 0) text += " · other " + other;
      return text;
    }

    function updateLicenses(payload) {
      var el = root.querySelector('[data-live="licenses"]');
      if (!el) return;
      var lic = payload.licenses;
      el.title = (lic && lic.statusNote) || "";
      el.classList.toggle("text-secondary", !(lic && lic.enabled));
      var textEl = el.querySelector("[data-live-lic-text]");
      if (textEl) textEl.textContent = formatLicenseStrip(lic);
    }

    function applyRows(payload) {
      var f = filterState();
      var rows = payload.rows || [];
      var filtered = rows.filter(function (r) {
        return matchesFilter(r, f.q, f.status);
      });
      var tbody = root.querySelector("#live-fleet-table tbody");
      if (!tbody) return;
      var byId = {};
      rows.forEach(function (r) {
        byId[r.machineId] = r;
      });
      var seen = {};
      tbody.querySelectorAll("tr[data-machine-id]").forEach(function (tr) {
        var id = Number(tr.getAttribute("data-machine-id"));
        seen[id] = true;
        var row = byId[id];
        if (!row || !matchesFilter(row, f.q, f.status)) {
          tr.hidden = true;
          return;
        }
        patchRow(tr, row);
      });
      // New machines: force HTML fallback once so structure stays correct.
      var missing = filtered.some(function (r) {
        return !seen[r.machineId];
      });
      if (missing) {
        softRefreshHtml();
        return;
      }
      updateMeta(filtered.length, payload.enrolledCount || rows.length);
    }

    function syncCharts(payload) {
      if (!chartsHost) return;
      var charts = payload.charts || [];
      var ids = {};
      charts.forEach(function (c) {
        ids[c.machineId] = c;
      });
      chartsHost.querySelectorAll("[data-live-chart-machine]").forEach(function (el) {
        var id = Number(el.getAttribute("data-live-chart-machine"));
        if (!ids[id]) el.remove();
      });
      var empty = chartsHost.querySelector("[data-flood-live-charts-empty]");
      if (charts.length === 0) {
        if (empty) empty.hidden = false;
        return;
      }
      if (empty) empty.hidden = true;
      charts.forEach(function (c) {
        var card = chartsHost.querySelector(
          '[data-live-chart-machine="' + c.machineId + '"]'
        );
        if (!card) {
          chartsHost.insertAdjacentHTML("beforeend", chartCardHtml(c));
          card = chartsHost.querySelector(
            '[data-live-chart-machine="' + c.machineId + '"]'
          );
          var chartEl = card && card.querySelector(".hd-gpu-chart");
          if (chartEl && window.HeimdallGpuRunCharts)
            window.HeimdallGpuRunCharts.init(card);
        }
        var chartEl = card.querySelector(".hd-gpu-chart");
        if (chartEl && window.HeimdallGpuRunCharts) {
          window.HeimdallGpuRunCharts.setData(
            chartEl,
            c.series || [],
            c.startUnix,
            c.endUnix,
            { keepZoom: true }
          );
        }
      });
    }

    function onPayload(payload) {
      lastPayload = payload;
      applyRows(payload);
      updateLicenses(payload);
      syncCharts(payload);
      nextAt = Date.now() + FALLBACK_MS;
      tickCountdown();
    }

    function softRefreshHtml() {
      var params = new URLSearchParams();
      params.set("tab", "live");
      params.set("partial", "1");
      if (form) {
        var q = form.querySelector('[name="q"]');
        var status = form.querySelector('[name="statusFilter"]');
        if (q && q.value) params.set("q", q.value);
        if (status && status.value) params.set("statusFilter", status.value);
      }
      fetch("/HistoricalDashboard?" + params.toString(), {
        credentials: "same-origin",
        headers: {
          "X-Fleet-Partial": "1",
          "X-Ops-Partial": "1",
          Accept: "text/html"
        }
      })
        .then(function (r) {
          return r.ok ? r.text() : Promise.reject();
        })
        .then(function (html) {
          var doc = new DOMParser().parseFromString(html, "text/html");
          var next = doc.querySelector("[data-flood-live-root]");
          var bodyOld = root.querySelector("[data-flood-live-body]");
          var bodyNew = next && next.querySelector("[data-flood-live-body]");
          if (bodyOld && bodyNew) {
            bodyOld.innerHTML = bodyNew.innerHTML;
            if (window.HeimdallTable) window.HeimdallTable.initSort(bodyOld);
          }
          if (lastPayload) applyRows(lastPayload);
          nextAt = Date.now() + FALLBACK_MS;
        })
        .catch(function () {});
    }

    // Per-run GPU toggle (same pattern as behaviour).
    root.addEventListener("click", function (ev) {
      var btn = ev.target.closest && ev.target.closest("[data-gpu-toggle]");
      if (!btn || !root.contains(btn)) return;
      ev.preventDefault();
      var block = btn.closest(".hd-run-block");
      if (!block) return;
      var panel = block.querySelector("[data-gpu-panel]");
      var show = btn.getAttribute("aria-pressed") !== "true";
      if (panel) panel.hidden = !show;
      btn.setAttribute("aria-pressed", show ? "true" : "false");
      btn.textContent = show ? "Hide GPU" : "Show GPU";
      window.dispatchEvent(new Event("resize"));
    });

    if (form) {
      form.addEventListener("submit", function (e) {
        e.preventDefault();
        if (lastPayload) applyRows(lastPayload);
        else softRefreshHtml();
      });
      var status = form.querySelector('[name="statusFilter"]');
      if (status) {
        status.addEventListener("change", function () {
          if (lastPayload) applyRows(lastPayload);
        });
      }
    }

    function startFallback() {
      usingSse = false;
      if (fallbackTimer) clearInterval(fallbackTimer);
      fallbackTimer = setInterval(softRefreshHtml, FALLBACK_MS);
      tickCountdown();
    }

    if (typeof EventSource !== "undefined") {
      try {
        var es = new EventSource("/api/flood/live/stream");
        es.addEventListener("live", function (ev) {
          try {
            usingSse = true;
            if (fallbackTimer) {
              clearInterval(fallbackTimer);
              fallbackTimer = null;
            }
            onPayload(JSON.parse(ev.data));
          } catch (_) {}
        });
        es.onerror = function () {
          // Browser will retry EventSource; if still dead, use HTML fallback.
          if (es.readyState === EventSource.CLOSED) startFallback();
          else usingSse = false;
        };
        // Safety: if no SSE within 8s, start HTML fallback until first event.
        setTimeout(function () {
          if (!lastPayload) startFallback();
        }, 8000);
      } catch (_) {
        startFallback();
      }
    } else {
      startFallback();
    }

    countdownTimer = setInterval(tickCountdown, 1000);
    tickCountdown();
  }

  function init(scope) {
    var root =
      (scope && scope.querySelector && scope.querySelector("[data-flood-live-root]")) ||
      document.querySelector("[data-flood-live-root]");
    if (root) boot(root);
  }

  if (document.readyState === "loading") {
    document.addEventListener("DOMContentLoaded", function () {
      init(document);
    });
  } else {
    init(document);
  }

  window.HeimdallFloodLive = { init: init };
})();
