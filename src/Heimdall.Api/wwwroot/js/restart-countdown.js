(function () {
    "use strict";

    var POLL_MS = 15000;

    function pad(n) {
        return n < 10 ? "0" + n : String(n);
    }

    function formatRemaining(ms) {
        if (ms <= 0) return null;
        var totalSec = Math.ceil(ms / 1000);
        var min = Math.floor(totalSec / 60);
        var sec = totalSec % 60;
        return pad(min) + ":" + pad(sec);
    }

    function formatContact(isoUtc) {
        if (!isoUtc) return "";
        try {
            var d = new Date(isoUtc);
            return d.toLocaleString(undefined, {
                day: "2-digit",
                month: "2-digit",
                year: "numeric",
                hour: "2-digit",
                minute: "2-digit"
            }).replace(",", " -");
        } catch (_) {
            return "";
        }
    }

    function rdpBadgeClass(responding) {
        if (responding === true) return "badge-rdp";
        if (responding === false) return "badge-expired";
        return "badge-ended";
    }

    function updateCountdown(el) {
        var until = el.getAttribute("data-until");
        if (!until) return;

        var target = Date.parse(until);
        if (isNaN(target)) return;

        var valueEl = el.querySelector(".hd-countdown-value");
        var expiredEl = el.querySelector(".hd-countdown-expired");
        if (!valueEl) return;

        var remaining = target - Date.now();
        var text = formatRemaining(remaining);

        if (text) {
            valueEl.textContent = text;
            valueEl.hidden = false;
            if (expiredEl) expiredEl.hidden = true;
        } else {
            valueEl.hidden = true;
            if (expiredEl) expiredEl.hidden = false;
        }
    }

    function applyStatus(row, data) {
        if (!row || !data) return;

        var progress = row.querySelector(".hd-restart-progress");
        if (progress) {
            var badge = progress.querySelector(".hd-restart-badge");
            if (badge) {
                badge.textContent = data.label || "";
                badge.title = data.detail || "";
            }

            var countdownWrap = progress.querySelector(".hd-restart-countdown");
            if (countdownWrap) {
                if (data.showCountdown && data.countdownUntilUtc) {
                    countdownWrap.hidden = false;
                    countdownWrap.setAttribute("data-until", data.countdownUntilUtc);
                    updateCountdown(countdownWrap);
                } else {
                    countdownWrap.hidden = true;
                }
            }
        }

        var rdpCell = row.querySelector(".hd-remote-rdp");
        if (!rdpCell) return;

        if (data.phase === "Verifying" || data.phase === "Acknowledged") {
            rdpCell.innerHTML =
                '<span class="badge-pill badge-rdp">Testing RDP…</span>';
            return;
        }

        if (data.rdpResponding === true || data.rdpResponding === false) {
            var ok = data.rdpResponding === true;
            var label = ok ? "Accepting" : "Unreachable";
            var cls = rdpBadgeClass(data.rdpResponding);
            rdpCell.innerHTML =
                '<span class="badge-pill ' + cls + '" title="' + (data.rdpError || "") + '">' +
                label + "</span>";

            if (window.HeimdallRemoteMachines) {
                var hostname = row.getAttribute("data-hostname");
                window.HeimdallRemoteMachines.saveRdpProbe(hostname, data.rdpResponding, data.rdpError);
            }
        }
    }

    function pollRow(row) {
        var hostname = row.getAttribute("data-hostname");
        if (!hostname) return;

        fetch("/api/remote/" + encodeURIComponent(hostname) + "/restart-status", {
            credentials: "same-origin"
        })
            .then(function (r) { return r.ok ? r.json() : null; })
            .then(function (data) {
                if (!data) return;
                applyStatus(row, data);
                if (!data.isActive) {
                    row.removeAttribute("data-restart-poll");
                }
            })
            .catch(function () { /* ignore */ });
    }

    function tickCountdowns() {
        document.querySelectorAll(".hd-restart-countdown[data-until]").forEach(updateCountdown);
    }

    function init() {
        tickCountdowns();
        setInterval(tickCountdowns, 1000);

        var pollRows = document.querySelectorAll("tr[data-restart-poll]");
        if (pollRows.length === 0) return;

        pollRows.forEach(pollRow);
        setInterval(function () {
            document.querySelectorAll("tr[data-restart-poll]").forEach(pollRow);
        }, POLL_MS);
    }

    if (document.readyState === "loading") {
        document.addEventListener("DOMContentLoaded", init);
    } else {
        init();
    }
})();
