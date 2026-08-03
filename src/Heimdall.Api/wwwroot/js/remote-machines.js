(function () {
    "use strict";

    var STORAGE_KEY = "heimdall-remote-probes";

    function readStore() {
        try {
            var raw = sessionStorage.getItem(STORAGE_KEY);
            return raw ? JSON.parse(raw) : {};
        } catch (_) {
            return {};
        }
    }

    function writeStore(store) {
        try {
            sessionStorage.setItem(STORAGE_KEY, JSON.stringify(store));
        } catch (_) { /* ignore quota */ }
    }

    function rdpBadgeClass(responding) {
        if (responding === true) return "badge-rdp";
        if (responding === false) return "badge-expired";
        return "badge-ended";
    }

    function rdpLabel(responding) {
        return responding ? "Accepting" : "Unreachable";
    }

    function isTestingCell(rdpCell) {
        var badge = rdpCell.querySelector(".badge-rdp");
        return badge && badge.textContent.indexOf("Testing") >= 0;
    }

    function renderRdpBadge(rdpCell, responding, error) {
        if (!rdpCell || isTestingCell(rdpCell)) return;

        var cls = rdpBadgeClass(responding);
        var title = error ? ' title="' + String(error).replace(/"/g, "&quot;") + '"' : "";
        rdpCell.innerHTML =
            '<span class="badge-pill ' + cls + '"' + title + ">" +
            rdpLabel(responding === true) +
            "</span>";
    }

    function saveRdpProbe(hostname, responding, error) {
        if (!hostname || responding !== true && responding !== false) return;

        var store = readStore();
        store[hostname] = {
            rdpResponding: responding,
            rdpError: error || null,
            probedAt: new Date().toISOString()
        };
        writeStore(store);
    }

    function hydrateRdpFromSession() {
        var store = readStore();
        document.querySelectorAll(".hd-remote-table tbody tr[data-hostname]").forEach(function (row) {
            if (row.classList.contains("hd-remote-offline")) return;

            var hostname = row.getAttribute("data-hostname");
            var rdpCell = row.querySelector(".hd-remote-rdp");
            if (!hostname || !rdpCell || rdpCell.textContent.trim()) return;

            var entry = store[hostname];
            if (!entry || entry.rdpResponding !== true && entry.rdpResponding !== false) return;

            renderRdpBadge(rdpCell, entry.rdpResponding, entry.rdpError);
        });
    }

    function seedStoreFromServer() {
        var store = readStore();

        document.querySelectorAll(".hd-remote-table tbody tr[data-hostname]").forEach(function (row) {
            var hostname = row.getAttribute("data-hostname");
            if (!hostname) return;

            var respondingRaw = row.getAttribute("data-rdp-responding");
            if (respondingRaw !== "True" && respondingRaw !== "False") return;

            var responding = respondingRaw === "True";
            var badge = row.querySelector(".hd-remote-rdp .badge-pill");
            var error = badge ? badge.getAttribute("title") : null;
            store[hostname] = {
                rdpResponding: responding,
                rdpError: error || null,
                probedAt: new Date().toISOString()
            };
        });

        writeStore(store);
    }

    window.HeimdallRemoteMachines = {
        saveRdpProbe: saveRdpProbe,
        renderRdpBadge: renderRdpBadge,
        rdpLabel: rdpLabel,
        rdpBadgeClass: rdpBadgeClass
    };

    function init() {
        seedStoreFromServer();
        hydrateRdpFromSession();
    }

    if (document.readyState === "loading") {
        document.addEventListener("DOMContentLoaded", init);
    } else {
        init();
    }
})();
