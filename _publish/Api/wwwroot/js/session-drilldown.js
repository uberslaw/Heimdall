(function () {
    "use strict";

    var HEARTBEAT_MS = 20000;
    var POLL_MS = 10000;
    var STALE_LIVE_SEC = 20;

    var modalEl = document.getElementById("session-drilldown-modal");
    if (!modalEl || typeof bootstrap === "undefined") return;

    var modal = new bootstrap.Modal(modalEl);
    var bodyEl = document.getElementById("session-drilldown-body");
    var titleEl = document.getElementById("session-drilldown-title");

    var heartbeatTimer = null;
    var pollTimer = null;
    var currentHostname = null;

    function makeViewerId() {
        try {
            if (window.crypto && window.crypto.randomUUID) return window.crypto.randomUUID();
        } catch (_) { /* fall through */ }
        return "v-" + Date.now() + "-" + Math.random().toString(16).slice(2);
    }

    function getViewerId() {
        try {
            var existing = sessionStorage.getItem("hd_drilldown_viewer");
            if (existing) return existing;
            var created = makeViewerId();
            sessionStorage.setItem("hd_drilldown_viewer", created);
            return created;
        } catch (_) {
            // sessionStorage unavailable (e.g. privacy mode) — fall back to a per-load id.
            if (!window.__hdDrilldownViewerFallback) window.__hdDrilldownViewerFallback = makeViewerId();
            return window.__hdDrilldownViewerFallback;
        }
    }

    var viewerId = getViewerId();

    function heartbeatUrl(host) { return "/api/sessions/drilldown/" + encodeURIComponent(host) + "/viewer/heartbeat"; }
    function leaveUrl(host) { return "/api/sessions/drilldown/" + encodeURIComponent(host) + "/viewer/leave"; }
    function dataUrl(host) { return "/api/sessions/drilldown/" + encodeURIComponent(host); }

    function sendHeartbeat(host) {
        fetch(heartbeatUrl(host), {
            method: "POST",
            credentials: "same-origin",
            keepalive: true,
            headers: { "Content-Type": "application/json" },
            body: JSON.stringify({ viewerId: viewerId })
        }).catch(function () { /* transient network error — next heartbeat will retry */ });
    }

    function sendLeave(host) {
        var payload = JSON.stringify({ viewerId: viewerId });
        try {
            if (navigator.sendBeacon) {
                navigator.sendBeacon(leaveUrl(host), new Blob([payload], { type: "application/json" }));
                return;
            }
        } catch (_) { /* fall back to fetch below */ }
        fetch(leaveUrl(host), {
            method: "POST",
            credentials: "same-origin",
            keepalive: true,
            headers: { "Content-Type": "application/json" },
            body: payload
        }).catch(function () { /* best effort */ });
    }

    function escapeHtml(s) {
        return String(s === null || s === undefined ? "" : s).replace(/[&<>"']/g, function (c) {
            return { "&": "&amp;", "<": "&lt;", ">": "&gt;", "\"": "&quot;", "'": "&#39;" }[c];
        });
    }

    /** Strip DOMAIN\ (incl. Global\) for display — mirrors UsernameDisplay.Format. */
    function formatUsername(username, domain) {
        var u = (username || "").trim();
        if (!u) return "—";
        var slash = u.indexOf("\\");
        if (slash > 0 && slash < u.length - 1) return u.slice(slash + 1).trim() || "—";
        return u;
    }

    function formatPercent(v) {
        return typeof v === "number" ? v.toFixed(1).replace(/\.0$/, "") + "%" : "—";
    }

    function formatRam(m) {
        if (typeof m.ramPercent !== "number") return "—";
        if (typeof m.ramUsedGb === "number" && typeof m.ramTotalGb === "number") {
            return formatPercent(m.ramPercent) + " (" + m.ramUsedGb.toFixed(1) + "/" + m.ramTotalGb.toFixed(1) + " GB)";
        }
        return formatPercent(m.ramPercent);
    }

    function formatBytesPerSec(bytesPerSec) {
        var mb = bytesPerSec / (1024 * 1024);
        if (mb >= 1) return mb.toFixed(1) + " MB/s";
        return (bytesPerSec / 1024).toFixed(1) + " KB/s";
    }

    function levelBadgeClass(level) {
        if (level === "High") return "badge-expired";
        if (level === "Med") return "badge-warn";
        return "badge-active";
    }

    function samplingStatus(m) {
        if (!m.isSamplingActive) return { label: "Not sampling — starting now, check back in a few seconds", cls: "badge-ended" };
        if (!m.sampledAtUtc) return { label: "Waiting for first sample…", cls: "badge-warn" };
        var ageSec = (Date.now() - Date.parse(m.sampledAtUtc)) / 1000;
        if (ageSec < STALE_LIVE_SEC) return { label: "Live", cls: "badge-active" };
        var d = new Date(m.sampledAtUtc);
        var when = d.toLocaleString(undefined, { day: "2-digit", month: "2-digit", hour: "2-digit", minute: "2-digit" });
        return { label: "Last sample " + when, cls: "badge-active" };
    }

    // Renders a clickable large-duration span — see wwwroot/js/hd-duration.js — which HeimdallDuration.init()
    // activates after this HTML is inserted into the modal body.
    function durationSpan(totalSeconds) {
        var seconds = Math.max(0, totalSeconds || 0);
        return '<span class="hd-duration" data-seconds="' + seconds + '"></span>';
    }

    function renderTopList(items, suffix) {
        var list = items || [];
        if (list.length === 0) return '<li class="text-secondary">—</li>';
        return list.map(function (p) {
            var valueText = suffix === "%" ? formatPercent(p.value)
                : suffix === "MB" ? p.value.toFixed(1) + " MB"
                    : formatBytesPerSec(p.value);
            return "<li>" + escapeHtml(p.processName) + ' <span class="text-secondary">' + valueText + "</span></li>";
        }).join("");
    }

    function renderUsers(users) {
        if (!users || users.length === 0) {
            return '<p class="text-secondary mb-0">No open sessions right now.</p>';
        }
        var rows = users.map(function (u) {
            var who = escapeHtml(formatUsername(u.username, u.domain));
            var client = u.clientName || u.clientAddress || "—";
            var disc = "—";
            if (u.stateLabel === "Disconnected") {
                disc = u.disconnectedSeconds > 0
                    ? durationSpan(u.disconnectedSeconds) + ' <span class="text-secondary small">(cumulative, this session)</span>'
                    : "just now";
            }
            return "<tr>" +
                "<td>" + who + "</td>" +
                '<td><span class="badge-pill ' + u.sessionTypeBadgeClass + '">' + escapeHtml(u.sessionTypeLabel) + "</span></td>" +
                '<td><span class="badge-pill ' + u.stateBadgeClass + '">' + escapeHtml(u.stateLabel) + "</span></td>" +
                "<td>" + disc + "</td>" +
                "<td>" + escapeHtml(client) + "</td>" +
                "</tr>";
        }).join("");
        return '<table class="hd-table"><thead><tr>' +
            "<th>User</th><th>Type</th><th>State</th><th>Disconnected for</th><th>Client</th>" +
            "</tr></thead><tbody>" + rows + "</tbody></table>";
    }

    function metricCol(label, valueHtml, topHtml) {
        return '<div class="col-6 col-md-4">' +
            '<div class="hd-metric-cell">' +
            '<div class="text-secondary small">' + label + "</div>" +
            '<div class="hd-metric-value">' + valueHtml + "</div>" +
            '<ul class="hd-metric-top">' + topHtml + "</ul>" +
            "</div></div>";
    }

    function renderMetric(m) {
        if (!m) {
            return '<p class="text-secondary mb-0">No resource data for this host yet.</p>';
        }
        var status = samplingStatus(m);
        var gpuNote = (typeof m.gpuPercent !== "number" && typeof m.cpuPercent === "number")
            ? '<div class="text-secondary small">n/a on this host</div>' : "";
        return "" +
            '<div class="mb-2"><span class="badge-pill ' + status.cls + '">' + status.label + "</span></div>" +
            '<div class="row g-3">' +
            metricCol("CPU", formatPercent(m.cpuPercent), renderTopList(m.topCpuProcesses, "%")) +
            metricCol("GPU", formatPercent(m.gpuPercent), renderTopList(m.topGpuProcesses, "%") + gpuNote) +
            metricCol("RAM", formatRam(m), renderTopList(m.topRamProcesses, "MB")) +
            metricCol("Disk reads", '<span class="badge-pill ' + levelBadgeClass(m.diskReadLevel) + '">' + m.diskReadLevel + "</span>", renderTopList(m.topDiskReadProcesses, "bps")) +
            metricCol("Disk writes", '<span class="badge-pill ' + levelBadgeClass(m.diskWriteLevel) + '">' + m.diskWriteLevel + "</span>", renderTopList(m.topDiskWriteProcesses, "bps")) +
            "</div>";
    }

    function render(hostname, data) {
        titleEl.textContent = "Open on " + hostname;
        bodyEl.innerHTML =
            '<h6 class="hd-section-title">Users with this open right now</h6>' +
            renderUsers(data.users) +
            '<h6 class="hd-section-title mt-3">Resource activity (big 4 + top 3 processes)</h6>' +
            renderMetric(data.metric);
        if (window.HeimdallDuration) window.HeimdallDuration.init(bodyEl);
    }

    function load(hostname) {
        fetch(dataUrl(hostname), { credentials: "same-origin" })
            .then(function (r) { return r.ok ? r.json() : null; })
            .then(function (data) {
                if (!data || hostname !== currentHostname) return;
                render(hostname, data);
            })
            .catch(function () {
                if (hostname !== currentHostname) return;
                bodyEl.innerHTML = '<p class="text-secondary mb-0">Could not load session details for this host — will retry.</p>';
            });
    }

    function stopWatching() {
        if (heartbeatTimer) { clearInterval(heartbeatTimer); heartbeatTimer = null; }
        if (pollTimer) { clearInterval(pollTimer); pollTimer = null; }
        if (currentHostname) {
            sendLeave(currentHostname);
            currentHostname = null;
        }
    }

    function startWatching(hostname) {
        stopWatching();
        currentHostname = hostname;
        sendHeartbeat(hostname);
        load(hostname);
        heartbeatTimer = setInterval(function () { sendHeartbeat(hostname); }, HEARTBEAT_MS);
        pollTimer = setInterval(function () { load(hostname); }, POLL_MS);
    }

    document.addEventListener("click", function (e) {
        var btn = e.target.closest(".js-open-drilldown");
        if (!btn) return;
        var hostname = btn.getAttribute("data-hostname");
        if (!hostname) return;

        titleEl.textContent = "Open on " + hostname;
        bodyEl.innerHTML = '<p class="text-secondary mb-0">Loading…</p>';
        modal.show();
        startWatching(hostname);
    });

    modalEl.addEventListener("hidden.bs.modal", stopWatching);
    document.addEventListener("pagehide", stopWatching);
    window.addEventListener("beforeunload", stopWatching);
})();
