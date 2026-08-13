// First-party site usage: dwell time + link clicks → POST /api/usage/beacon (sendBeacon on unload).
(function () {
    "use strict";

    var root = document.documentElement;
    if (root.getAttribute("data-hd-usage") !== "1")
        return;

    var path = root.getAttribute("data-hd-usage-path") || (window.location.pathname || "/");
    var query = root.getAttribute("data-hd-usage-query") || "";
    var pageViewId = root.getAttribute("data-hd-pageview-id") || "";
    var sessionId = root.getAttribute("data-hd-session-id") || readCookie("hd_usage_sid") || "";
    var heartbeatSec = parseInt(root.getAttribute("data-hd-usage-heartbeat") || "30", 10);
    if (!isFinite(heartbeatSec) || heartbeatSec < 10)
        heartbeatSec = 30;
    if (heartbeatSec > 120)
        heartbeatSec = 120;

    var started = Date.now();
    var lastSentSeconds = 0;
    var queue = [];
    var flushing = false;

    function readCookie(name) {
        var parts = ("; " + document.cookie).split("; " + name + "=");
        if (parts.length < 2)
            return "";
        return decodeURIComponent(parts.pop().split(";").shift() || "");
    }

    function enqueue(evt) {
        queue.push(evt);
        if (queue.length > 30)
            queue.shift();
    }

    function buildPayload(extraEvents) {
        var events = (extraEvents || []).concat(queue.splice(0, queue.length));
        if (events.length === 0)
            return null;
        return {
            sessionId: sessionId,
            pageViewId: pageViewId,
            events: events
        };
    }

    function send(payload, useBeacon) {
        if (!payload)
            return;
        var body = JSON.stringify(payload);
        if (useBeacon && navigator.sendBeacon) {
            try {
                var blob = new Blob([body], { type: "application/json" });
                if (navigator.sendBeacon("/api/usage/beacon", blob))
                    return;
            } catch (e) { /* fall through */ }
        }
        try {
            fetch("/api/usage/beacon", {
                method: "POST",
                headers: { "Content-Type": "application/json" },
                body: body,
                credentials: "same-origin",
                keepalive: true
            }).catch(function () { });
        } catch (e2) { /* ignore */ }
    }

    function durationEvent(force) {
        var secs = Math.floor((Date.now() - started) / 1000);
        if (secs < 0)
            secs = 0;
        if (!force && secs <= lastSentSeconds)
            return null;
        if (!force && secs - lastSentSeconds < 5 && secs < heartbeatSec)
            return null;
        lastSentSeconds = secs;
        return {
            type: "duration",
            path: path,
            query: query,
            pageViewId: pageViewId,
            durationSeconds: secs
        };
    }

    function flush(useBeacon, forceDuration) {
        if (flushing && !useBeacon)
            return;
        flushing = true;
        var extra = [];
        var dur = durationEvent(!!forceDuration);
        if (dur)
            extra.push(dur);
        var payload = buildPayload(extra);
        send(payload, useBeacon);
        flushing = false;
    }

    document.addEventListener("click", function (ev) {
        var el = ev.target;
        if (!el || !el.closest)
            return;
        var a = el.closest("a[href]");
        if (!a)
            return;
        var href = a.getAttribute("href") || "";
        if (!href || href.charAt(0) === "#" || href.indexOf("javascript:") === 0)
            return;
        var text = (a.innerText || a.textContent || "").replace(/\s+/g, " ").trim();
        if (text.length > 80)
            text = text.slice(0, 80);
        enqueue({
            type: "click",
            path: path,
            query: query,
            pageViewId: pageViewId,
            href: href.length > 500 ? href.slice(0, 500) : href,
            text: text
        });
        // Best-effort flush soon so navigations still record the click.
        setTimeout(function () { flush(false, false); }, 0);
    }, true);

    document.addEventListener("visibilitychange", function () {
        if (document.visibilityState === "hidden")
            flush(true, true);
    });

    window.addEventListener("pagehide", function () {
        flush(true, true);
    });

    setInterval(function () {
        if (document.visibilityState === "visible")
            flush(false, false);
    }, heartbeatSec * 1000);
})();
