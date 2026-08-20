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

  function chartRowHtml(c, colCount) {
    return (
      '<tr class="hd-live-chart-row" data-follow-for="' +
      c.machineId +
      '" data-live-chart-machine="' +
      c.machineId +
      '">' +
      '<td colspan="' +
      colCount +
      '">' +
      '<div class="hd-flood-live-chart-wrap">' +
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
      '<p class="hd-gpu-chart-hint text-secondary small mb-0">Scroll to zoom · drag to pan when zoomed</p>' +
      "</div></div></td></tr>"
    );
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
    var lastPayload = null;
    var nextAt = Date.now() + FALLBACK_MS;
    var usingSse = false;
    var fallbackTimer = null;
    var countdownTimer = null;
    // machineId -> whether chart panel is shown (default true when chart first appears)
    var chartVisible = {};

    function tableBody() {
      return root.querySelector("#live-fleet-table tbody");
    }

    function colCount() {
      var table = root.querySelector("#live-fleet-table");
      var ths = table && table.querySelectorAll("thead tr:first-child th");
      return ths && ths.length ? ths.length : 15;
    }

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

    function updateMeta(filtered, total) {
      var meta = root.querySelector('[data-live="meta"]');
      if (!meta) return;
      meta.setAttribute("data-filtered", String(filtered));
      meta.setAttribute("data-total", String(total));
      meta.title =
        filtered +
        " / " +
        total +
        " enrolled" +
        (usingSse ? " · live stream" : " · polling");
      tickCountdown();
    }

    function tickCountdown() {
      var cd = root.querySelector("[data-live-meta-countdown]");
      if (!cd) return;
      var remaining = Math.max(0, Math.ceil((nextAt - Date.now()) / 1000));
      cd.textContent = "Live update in " + remaining + "s";
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
      function licenseCellHtml(cm, claimed) {
        if (claimed == null || claimed === undefined) return String(cm || 0);
        var mismatch = Number(claimed) !== Number(cm || 0);
        return (
          '<span class="hd-lic-cm">' +
          String(cm || 0) +
          '</span><span class="hd-lic-claim' +
          (mismatch ? " hd-lic-mismatch" : "") +
          '" title="Agent claim (estimate)">/' +
          String(claimed) +
          "</span>"
        );
      }
      function licenseTitle(hpc, classic, hpcDetail, classicDetail, claimedHpc, claimedClassic, claimDetail) {
        var seats = Math.max(hpc || 0, classic || 0);
        var parts = [
          "Seats in use: " +
            seats +
            " = max(HPC " +
            (hpc || 0) +
            ", Classic " +
            (classic || 0) +
            "). TUFLOW GPU/HPC typically holds both products — do not add them."
        ];
        if (hpcDetail) parts.push("HPC: " + hpcDetail);
        else if (!(hpc > 0)) parts.push("No HPC checkout at this machine LastIp");
        if (classicDetail) parts.push("Classic: " + classicDetail);
        else if (!(classic > 0)) parts.push("No Classic checkout at this machine LastIp");
        var claimed = effectiveClaim(claimedHpc, claimedClassic);
        if (claimed != null) {
          parts.push(
            "Agent claim: " +
              claimed +
              " (max HPC " +
              (claimedHpc != null ? claimedHpc : "—") +
              " / Classic " +
              (claimedClassic != null ? claimedClassic : "—") +
              ")" +
              (claimDetail ? " — " + claimDetail : "")
          );
          if (Number(claimed) !== Number(seats))
            parts.push(
              "Mismatch: CodeMeter is source of truth; claim is from local process args (-nt/GPU)."
            );
        }
        return parts.join(" · ");
      }
      function effectiveClaim(hpc, classic) {
        if (hpc == null && classic == null) return null;
        return Math.max(hpc || 0, classic || 0);
      }
      function metric(key, text, sort, detail) {
        var cell = tr.querySelector('[data-live="' + key + '"]');
        if (!cell) return;
        cell.textContent = text;
        cell.setAttribute("data-sort-value", String(sort));
        if (detail) {
          cell.title = "CodeMeter (by client IP only): " + detail;
        }
      }
      function licenseMetric(hpc, classic, hpcDetail, classicDetail, claimedHpc, claimedClassic, claimDetail) {
        var cell = tr.querySelector('[data-live="seats"]');
        if (!cell) return;
        var seats = Math.max(hpc || 0, classic || 0);
        var claimed = effectiveClaim(claimedHpc, claimedClassic);
        cell.innerHTML = licenseCellHtml(seats, claimed);
        cell.setAttribute("data-sort-value", String(seats));
        cell.title = licenseTitle(
          hpc,
          classic,
          hpcDetail,
          classicDetail,
          claimedHpc,
          claimedClassic,
          claimDetail
        );
        cell.classList.toggle(
          "hd-lic-cell-warn",
          claimed != null && Number(claimed) !== Number(seats)
        );
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
      licenseMetric(
        row.hpcSeats,
        row.classicSeats,
        row.hpcSeatDetail,
        row.classicSeatDetail,
        row.claimedHpcSeats,
        row.claimedClassicSeats,
        row.tuflowClaimDetail
      );
      tr.hidden = false;
    }

    function licenseStripHtml(lic) {
      if (!lic) {
        return '<div class="hd-lic-strip" data-live-lic-body><span class="hd-lic-chip hd-lic-chip-label">CodeMeter</span><span class="hd-lic-chip is-muted">—</span></div>';
      }
      if (!lic.enabled) {
        return '<div class="hd-lic-strip" data-live-lic-body><span class="hd-lic-chip hd-lic-chip-label">CodeMeter</span><span class="hd-lic-chip is-muted">Off</span></div>';
      }
      if (!lic.available) {
        return '<div class="hd-lic-strip" data-live-lic-body><span class="hd-lic-chip hd-lic-chip-label">CodeMeter</span><span class="hd-lic-chip hd-lic-chip-warn">Waiting for first poll…</span></div>';
      }
      function poolChip(key, used, total, avail, title) {
        var v = used == null ? "—/" + total : used + "/" + total;
        var free =
          avail != null
            ? '<span class="hd-lic-free">' + avail + " free</span>"
            : "";
        return (
          '<span class="hd-lic-chip" title="' +
          title +
          '"><span class="hd-lic-k">' +
          key +
          '</span><span class="hd-lic-v">' +
          v +
          "</span>" +
          free +
          "</span>"
        );
      }
      function age() {
        if (!lic.queriedAtUtc) return "—";
        var sec = Math.max(
          0,
          Math.floor((Date.now() - new Date(lic.queriedAtUtc).getTime()) / 1000)
        );
        return sec < 90 ? sec + "s ago" : Math.floor(sec / 60) + "m ago";
      }
      function nextPollLabel() {
        if (!lic.queriedAtUtc) return "—";
        var interval = Math.max(15, Math.min(600, Number(lic.pollIntervalSeconds) || 60));
        var started =
          new Date(lic.queriedAtUtc).getTime() -
          Math.max(0, Number(lic.pollDurationMs) || 0);
        var rem = Math.ceil((started + interval * 1000 - Date.now()) / 1000);
        if (rem <= 0) return "due";
        return rem + "s";
      }
      var pollDur =
        lic.pollDurationMs >= 1000
          ? (lic.pollDurationMs / 1000).toFixed(1) + "s"
          : Math.round(lic.pollDurationMs || 0) + "ms";
      var html =
        '<div class="hd-lic-strip" data-live-lic-body>' +
        '<span class="hd-lic-chip hd-lic-chip-label">CodeMeter</span>' +
        poolChip(
          "HPC",
          lic.hpcUsed,
          lic.hpcTotal,
          lic.hpcAvailable,
          "HPC pool Used=/Total from CodeMeter (product 926). Separate from Classic — do not add to Classic or to the Seats column."
        ) +
        poolChip(
          "Classic",
          lic.classicUsed,
          lic.classicTotal,
          lic.classicAvailable,
          "Classic pool Used=/Total from CodeMeter (product 920). Separate from HPC — do not add to HPC or to the Seats column."
        ) +
        '<span class="hd-lic-chip hd-lic-chip-meta" title="How long the last CodeMeter query took · age of that result" data-live-lic-last>' +
        '<span class="hd-lic-k">Last poll</span><span class="hd-lic-v">' +
        pollDur +
        " · " +
        age() +
        "</span></span>" +
        '<span class="hd-lic-chip hd-lic-chip-meta" title="Estimated time until the next CodeMeter poll starts" data-live-lic-next>' +
        '<span class="hd-lic-k">Next poll</span><span class="hd-lic-v" data-live-lic-next-val>' +
        nextPollLabel() +
        "</span></span>";
      if (lic.partial) {
        html +=
          '<span class="hd-lic-chip hd-lic-chip-warn" title="Some license servers failed or timed out">Partial</span>';
      }
      var outside = lic.unmatchedEffective || 0;
      if (outside > 0) {
        var tip =
          lic.unmatchedDetail ||
          "Seats outside Flood enrollment: " +
            outside +
            " (max HPC/Classic per IP; not additive). CodeMeter products: HPC " +
            (lic.unmatchedHpc || 0) +
            " · Classic " +
            (lic.unmatchedClassic || 0);
        var tipAttr = String(tip)
          .replace(/&/g, "&amp;")
          .replace(/"/g, "&quot;")
          .replace(/</g, "&lt;")
          .replace(/\n/g, "&#10;");
        html +=
          '<span class="hd-lic-chip hd-lic-chip-outside" title="' +
          tipAttr +
          '"><span class="hd-lic-k">In use by other offices</span><span class="hd-lic-v">' +
          outside +
          "</span></span>";
      }
      html += "</div>";
      return html;
    }

    function updateLicenses(payload) {
      var el = root.querySelector('[data-live="licenses"]');
      if (!el) return;
      var lic = payload.licenses;
      var poll =
        lic && lic.pollDurationMs >= 1000
          ? (lic.pollDurationMs / 1000).toFixed(1) + "s poll"
          : lic
            ? Math.round(lic.pollDurationMs || 0) + "ms poll"
            : "";
      var note = (lic && lic.statusNote) || "";
      el.title = [note, poll].filter(Boolean).join(" · ");
      el.classList.toggle("is-muted", !(lic && lic.enabled));
      el.classList.toggle("text-secondary", !(lic && lic.enabled));
      el.innerHTML = licenseStripHtml(lic);
    }

    function ensureGpuToggle(tr, hasChart) {
      var cell = tr.querySelector('[data-live="status"]');
      if (!cell) return;
      var btn = cell.querySelector("[data-gpu-toggle]");
      if (!hasChart) {
        if (btn) btn.remove();
        return;
      }
      var id = Number(tr.getAttribute("data-machine-id"));
      var show = chartVisible[id] === true;
      if (!btn) {
        btn = document.createElement("button");
        btn.type = "button";
        btn.className =
          "btn btn-sm btn-outline-secondary hd-run-gpu-toggle hd-live-gpu-toggle";
        btn.setAttribute("data-gpu-toggle", "");
        btn.title = "Show or hide this machine’s live utilization chart";
        cell.appendChild(btn);
      }
      btn.setAttribute("aria-pressed", show ? "true" : "false");
      btn.textContent = show ? "Hide GPU" : "Show GPU";
      // Bright + periodic pulse only while collapsed and a run chart is available.
      btn.classList.toggle("is-entice", !show);
    }

    function applyRows(payload) {
      var f = filterState();
      var rows = payload.rows || [];
      var filtered = rows.filter(function (r) {
        return matchesFilter(r, f.q, f.status);
      });
      var tbody = tableBody();
      if (!tbody) return;
      var byId = {};
      rows.forEach(function (r) {
        byId[r.machineId] = r;
      });
      var chartIds = {};
      (payload.charts || []).forEach(function (c) {
        chartIds[c.machineId] = true;
      });
      var seen = {};
      tbody.querySelectorAll("tr[data-machine-id]").forEach(function (tr) {
        var id = Number(tr.getAttribute("data-machine-id"));
        seen[id] = true;
        var row = byId[id];
        var chartRow = tbody.querySelector(
          'tr[data-follow-for="' + id + '"]'
        );
        if (!row || !matchesFilter(row, f.q, f.status)) {
          tr.hidden = true;
          if (chartRow) chartRow.hidden = true;
          ensureGpuToggle(tr, false);
          return;
        }
        patchRow(tr, row);
        ensureGpuToggle(tr, !!chartIds[id]);
        if (chartRow) {
          var showChart = !!chartIds[id] && chartVisible[id] === true;
          chartRow.hidden = !showChart;
        }
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
      var tbody = tableBody();
      if (!tbody) return;
      var charts = payload.charts || [];
      var ids = {};
      charts.forEach(function (c) {
        ids[c.machineId] = c;
      });
      tbody.querySelectorAll("tr[data-live-chart-machine]").forEach(function (el) {
        var id = Number(el.getAttribute("data-live-chart-machine"));
        if (!ids[id]) {
          el.remove();
          var main = tbody.querySelector('tr[data-machine-id="' + id + '"]');
          if (main) ensureGpuToggle(main, false);
        }
      });
      charts.forEach(function (c) {
        var main = tbody.querySelector(
          'tr[data-machine-id="' + c.machineId + '"]'
        );
        if (!main || main.hidden) return;
        ensureGpuToggle(main, true);
        var card = tbody.querySelector(
          'tr[data-live-chart-machine="' + c.machineId + '"]'
        );
        if (!card) {
          main.insertAdjacentHTML("afterend", chartRowHtml(c, colCount()));
          card = tbody.querySelector(
            'tr[data-live-chart-machine="' + c.machineId + '"]'
          );
          if (card && window.HeimdallGpuRunCharts)
            window.HeimdallGpuRunCharts.init(card);
        } else if (card.previousElementSibling !== main) {
          main.after(card);
        }
        var show = chartVisible[c.machineId] === true;
        card.hidden = !show;
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
          if (lastPayload) {
            applyRows(lastPayload);
            syncCharts(lastPayload);
          }
          nextAt = Date.now() + FALLBACK_MS;
        })
        .catch(function () {});
    }

    // Show/Hide GPU on the machine stats row — toggles the chart row underneath.
    root.addEventListener("click", function (ev) {
      var btn = ev.target.closest && ev.target.closest("[data-gpu-toggle]");
      if (!btn || !root.contains(btn)) return;
      ev.preventDefault();
      var main = btn.closest("tr[data-machine-id]");
      if (!main) return;
      var id = Number(main.getAttribute("data-machine-id"));
      var show = btn.getAttribute("aria-pressed") !== "true";
      chartVisible[id] = show;
      btn.setAttribute("aria-pressed", show ? "true" : "false");
      btn.textContent = show ? "Hide GPU" : "Show GPU";
      btn.classList.toggle("is-entice", !show);
      var chartRow = tableBody() && tableBody().querySelector(
        'tr[data-follow-for="' + id + '"]'
      );
      if (chartRow) chartRow.hidden = !show;
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
