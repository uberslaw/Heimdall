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

    function showFlash(text, isError) {
        var existing = document.querySelector(".alert-success, .alert-danger");
        var el = existing;
        if (!el) {
            el = document.createElement("div");
            var panel = document.querySelector(".hd-remote-panel");
            if (panel && panel.parentNode) {
                panel.parentNode.insertBefore(el, panel);
            } else {
                document.body.prepend(el);
            }
        }
        el.className = isError ? "alert alert-danger" : "alert alert-success";
        el.textContent = text;
    }

    function updatePingCell(row, reachable, detail) {
        var form = row.querySelector(".hd-ping-form");
        if (!form) return;
        var icon = form.querySelector(".hd-ping-icon");
        if (!icon) {
            icon = document.createElement("span");
            form.appendChild(icon);
        }
        icon.className = "hd-ping-icon " + (reachable ? "hd-ping-ok" : "hd-ping-fail");
        icon.title = detail || "";
        icon.setAttribute("aria-label", reachable ? "Reachable" : "Unreachable");
        icon.textContent = reachable ? "✓" : "✗";
    }

    function antiforgeryToken(form) {
        var input = form.querySelector('input[name="__RequestVerificationToken"]');
        if (input && input.value) return input.value;
        var page = document.querySelector('input[name="__RequestVerificationToken"]');
        return page ? page.value : "";
    }

    function ensureAntiforgery(form) {
        if (form.querySelector('input[name="__RequestVerificationToken"]')) return;
        var page = document.querySelector('input[name="__RequestVerificationToken"]');
        if (!page) return;
        var clone = page.cloneNode(true);
        form.appendChild(clone);
    }

    async function postRowAction(form, handler) {
        ensureAntiforgery(form);
        var token = antiforgeryToken(form);
        var body = new URLSearchParams(new FormData(form));
        body.set("ajax", "1");
        if (token) body.set("__RequestVerificationToken", token);

        var res = await fetch("?handler=" + handler + "&ajax=1", {
            method: "POST",
            headers: {
                "Content-Type": "application/x-www-form-urlencoded",
                "RequestVerificationToken": token,
                "X-Requested-With": "XMLHttpRequest",
                "Accept": "application/json"
            },
            body: body
        });
        var data = await res.json().catch(function () { return {}; });
        if (!res.ok || !data.ok) {
            throw new Error(data.error || "Request failed");
        }
        return data;
    }

    function bindAjaxForms() {
        document.querySelectorAll("form[data-handler]").forEach(function (form) {
            form.addEventListener("submit", function (e) {
                e.preventDefault();
                var handler = form.getAttribute("data-handler");
                if (!handler) return;

                var confirmMsg = form.getAttribute("data-confirm");
                if (confirmMsg && !window.confirm(confirmMsg)) return;

                var btn = form.querySelector("button[type=submit]");
                var row = form.closest("tr");
                if (btn) btn.disabled = true;

                postRowAction(form, handler)
                    .then(function (data) {
                        if (data.action === "ping" && row) {
                            updatePingCell(row, data.reachable, data.detail);
                        }
                        if (data.action === "probeRdp" && row) {
                            var rdpCell = row.querySelector(".hd-remote-rdp");
                            renderRdpBadge(rdpCell, data.rdpResponding, data.error);
                            row.setAttribute("data-rdp-responding", data.rdpResponding ? "True" : "False");
                            saveRdpProbe(data.hostname, data.rdpResponding, data.error);
                        }
                        if (data.message) showFlash(data.message, false);
                        if (data.reload) {
                            window.location.reload();
                            return;
                        }
                    })
                    .catch(function (err) {
                        showFlash(err.message || "Action failed", true);
                    })
                    .finally(function () {
                        if (btn && handler !== "RestartRds") btn.disabled = false;
                    });
            });
        });
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
        bindAjaxForms();
    }

    if (document.readyState === "loading") {
        document.addEventListener("DOMContentLoaded", init);
    } else {
        init();
    }
})();
