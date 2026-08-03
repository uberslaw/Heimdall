(function () {
    "use strict";

    var HEARTBEAT_MS = 20000;
    var POLL_MS = 10000;
    var STALE_LIVE_SEC = 20;

    var groupId = window.HeimdallStaffGroupId;
    if (!groupId && groupId !== 0) return;

    function makeViewerId() {
        try {
            if (window.crypto && window.crypto.randomUUID) return window.crypto.randomUUID();
        } catch (_) { /* fall through */ }
        return "v-" + Date.now() + "-" + Math.random().toString(16).slice(2);
    }

    function viewerKey() { return "hd_viewer_" + groupId; }

    function getViewerId() {
        try {
            var existing = sessionStorage.getItem(viewerKey());
            if (existing) return existing;
            var created = makeViewerId();
            sessionStorage.setItem(viewerKey(), created);
            return created;
        } catch (_) {
            // sessionStorage unavailable (e.g. privacy mode) — fall back to a per-load id.
            if (!window.__hdViewerFallback) window.__hdViewerFallback = makeViewerId();
            return window.__hdViewerFallback;
        }
    }

    var viewerId = getViewerId();

    function heartbeatUrl() { return "/api/staff/groups/" + groupId + "/viewer/heartbeat"; }
    function leaveUrl() { return "/api/staff/groups/" + groupId + "/viewer/leave"; }
    function metricsUrl() { return "/api/staff/groups/" + groupId + "/metrics"; }

    function sendHeartbeat() {
        fetch(heartbeatUrl(), {
            method: "POST",
            credentials: "same-origin",
            keepalive: true,
            headers: { "Content-Type": "application/json" },
            body: JSON.stringify({ viewerId: viewerId })
        }).catch(function () { /* transient network error — next heartbeat will retry */ });
    }

    function sendLeave() {
        var payload = JSON.stringify({ viewerId: viewerId });
        try {
            if (navigator.sendBeacon) {
                var blob = new Blob([payload], { type: "application/json" });
                navigator.sendBeacon(leaveUrl(), blob);
                return;
            }
        } catch (_) { /* fall back to fetch below */ }
        fetch(leaveUrl(), {
            method: "POST",
            credentials: "same-origin",
            keepalive: true,
            headers: { "Content-Type": "application/json" },
            body: payload
        }).catch(function () { /* best effort */ });
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

    function renderTopList(ul, items, suffix) {
        if (!ul) return;
        ul.innerHTML = "";
        (items || []).forEach(function (p) {
            var li = document.createElement("li");
            var valueText = suffix === "%" ? formatPercent(p.value) : formatBytesPerSec(p.value);
            if (suffix === "MB") valueText = p.value.toFixed(1) + " MB";
            li.textContent = p.processName + " ";
            var span = document.createElement("span");
            span.className = "text-secondary";
            span.textContent = valueText;
            li.appendChild(span);
            ul.appendChild(li);
        });
    }

    function samplingStatus(m) {
        if (!m.isSamplingActive) return { label: "Not sampling", cls: "badge-ended" };
        if (!m.sampledAtUtc) return { label: "Waiting for first sample…", cls: "badge-warn" };
        var ageSec = (Date.now() - Date.parse(m.sampledAtUtc)) / 1000;
        if (ageSec < STALE_LIVE_SEC) return { label: "Live", cls: "badge-active" };
        var d = new Date(m.sampledAtUtc);
        var when = d.toLocaleString(undefined, { day: "2-digit", month: "2-digit", hour: "2-digit", minute: "2-digit" });
        return { label: "Last sample " + when, cls: "badge-active" };
    }

    function applyMetric(row, m) {
        var cells = {
            cpu: row.querySelector('[data-hd-metric="cpu"]'),
            gpu: row.querySelector('[data-hd-metric="gpu"]'),
            ram: row.querySelector('[data-hd-metric="ram"]'),
            diskRead: row.querySelector('[data-hd-metric="diskRead"]'),
            diskWrite: row.querySelector('[data-hd-metric="diskWrite"]')
        };

        if (cells.cpu) {
            cells.cpu.querySelector(".hd-metric-value").textContent = formatPercent(m.cpuPercent);
            renderTopList(cells.cpu.querySelector('[data-hd-top="cpu"]'), m.topCpuProcesses, "%");
        }
        if (cells.gpu) {
            cells.gpu.querySelector(".hd-metric-value").textContent = formatPercent(m.gpuPercent);
            renderTopList(cells.gpu.querySelector('[data-hd-top="gpu"]'), m.topGpuProcesses, "%");
        }
        if (cells.ram) {
            cells.ram.querySelector(".hd-metric-value").textContent = formatRam(m);
            renderTopList(cells.ram.querySelector('[data-hd-top="ram"]'), m.topRamProcesses, "MB");
        }
        if (cells.diskRead) {
            var readBadge = cells.diskRead.querySelector("[data-hd-level]");
            if (readBadge) {
                readBadge.textContent = m.diskReadLevel || "Low";
                readBadge.className = "badge-pill hd-staff-pill " + levelBadgeClass(m.diskReadLevel);
            }
            renderTopList(cells.diskRead.querySelector('[data-hd-top="diskRead"]'), m.topDiskReadProcesses, "bps");
        }
        if (cells.diskWrite) {
            var writeBadge = cells.diskWrite.querySelector("[data-hd-level]");
            if (writeBadge) {
                writeBadge.textContent = m.diskWriteLevel || "Low";
                writeBadge.className = "badge-pill hd-staff-pill " + levelBadgeClass(m.diskWriteLevel);
            }
            renderTopList(cells.diskWrite.querySelector('[data-hd-top="diskWrite"]'), m.topDiskWriteProcesses, "bps");
        }

        var samplingCell = row.querySelector("[data-hd-sampling-status]");
        if (samplingCell) {
            var status = samplingStatus(m);
            samplingCell.innerHTML = '<span class="badge-pill hd-staff-pill hd-staff-sampling-pill ' + status.cls + '">' + status.label + "</span>";
        }
    }

    function displayNameForHost(hostname) {
        var rows = document.querySelectorAll("[data-hd-staff-row]");
        for (var i = 0; i < rows.length; i++) {
            if (rows[i].getAttribute("data-hostname") === hostname) {
                return rows[i].getAttribute("data-display-name") || hostname;
            }
        }
        return hostname;
    }

    function renderFavoritesPanel(metricsByHost) {
        var panel = document.querySelector("[data-hd-favorites-panel]");
        if (!panel) return;

        var html = "";
        Object.keys(metricsByHost).forEach(function (hostname) {
            var m = metricsByHost[hostname];
            if (!m.favoriteProcesses || m.favoriteProcesses.length === 0) return;
            var label = displayNameForHost(hostname);
            html += '<div class="mb-2"><strong>' + label + "</strong>";
            if (label !== hostname) {
                html += ' <span class="text-secondary small">(' + hostname + ")</span>";
            }
            html += '<ul class="hd-metric-top hd-staff-metric-top">';
            m.favoriteProcesses.forEach(function (f) {
                html += "<li>" + f.processName + " — CPU " + formatPercent(f.cpuPercent) +
                    ", GPU " + formatPercent(f.gpuPercent) +
                    ", RAM " + (typeof f.ramMb === "number" ? f.ramMb.toFixed(1) + " MB" : "—") + "</li>";
            });
            html += "</ul></div>";
        });

        panel.innerHTML = html || '<p class="text-secondary mb-0">No favourite processes currently running on your machines.</p>';
    }

    function pollMetrics() {
        fetch(metricsUrl(), { credentials: "same-origin" })
            .then(function (r) { return r.ok ? r.json() : null; })
            .then(function (data) {
                if (!data) return;
                document.querySelectorAll("[data-hd-staff-row]").forEach(function (row) {
                    var hostname = row.getAttribute("data-hostname");
                    if (hostname && data[hostname]) applyMetric(row, data[hostname]);
                });
                renderFavoritesPanel(data);
            })
            .catch(function () { /* ignore transient errors — next poll retries */ });
    }

    function init() {
        sendHeartbeat();
        pollMetrics();
        setInterval(sendHeartbeat, HEARTBEAT_MS);
        setInterval(pollMetrics, POLL_MS);

        document.addEventListener("pagehide", sendLeave);
        window.addEventListener("beforeunload", sendLeave);
    }

    if (document.readyState === "loading") {
        document.addEventListener("DOMContentLoaded", init);
    } else {
        init();
    }
})();
