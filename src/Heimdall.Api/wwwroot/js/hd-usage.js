// First-party site usage: dwell time + actionable clicks → POST /api/usage/beacon (sendBeacon on unload).
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

    function clip(s, max) {
        s = (s || "").replace(/\s+/g, " ").trim();
        if (s.length > max)
            s = s.slice(0, max);
        return s;
    }

    function isSensitiveControl(el) {
        if (!el || !el.getAttribute)
            return true;
        var type = (el.getAttribute("type") || "").toLowerCase();
        if (type === "password" || type === "hidden" || type === "file")
            return true;
        var name = (el.getAttribute("name") || el.id || "").toLowerCase();
        if (/password|passwd|secret|token|apikey|api_key|authorization/.test(name))
            return true;
        return false;
    }

    function findTrackable(start) {
        if (!start || !start.closest)
            return null;
        // Explicit opt-in wins.
        var tracked = start.closest("[data-hd-track]");
        if (tracked)
            return tracked;
        return start.closest(
            "a[href], button, [role='button'], input[type='submit'], input[type='button'], summary, [data-bs-toggle], .nav-link, [data-ops-tab], [data-flood-tab]"
        );
    }

    function labelFor(el) {
        var track = el.getAttribute("data-hd-track");
        if (track && track.trim())
            return clip(track, 120);
        var aria = el.getAttribute("aria-label");
        if (aria && aria.trim())
            return clip(aria, 120);
        var title = el.getAttribute("title");
        if (title && title.trim())
            return clip(title, 120);
        var value = el.getAttribute("value");
        if (el.tagName === "INPUT" && value && value.trim())
            return clip(value, 120);
        var text = clip(el.innerText || el.textContent || "", 120);
        if (text)
            return text;
        if (el.id)
            return clip(el.id, 120);
        if (el.getAttribute("name"))
            return clip(el.getAttribute("name"), 120);
        return clip(el.tagName.toLowerCase(), 40);
    }

    function hrefFor(el) {
        if (el.tagName === "A") {
            var href = el.getAttribute("href") || "";
            if (!href || href.charAt(0) === "#" || href.indexOf("javascript:") === 0)
                return null;
            return href.length > 500 ? href.slice(0, 500) : href;
        }
        var formaction = el.getAttribute("formaction");
        if (formaction)
            return formaction.length > 500 ? formaction.slice(0, 500) : formaction;
        var track = el.getAttribute("data-hd-track");
        if (track && track.trim())
            return "track:" + clip(track, 200);
        if (el.id)
            return "control:#" + clip(el.id, 200);
        var name = el.getAttribute("name");
        if (name)
            return "control:" + clip(name, 200);
        return "control:" + el.tagName.toLowerCase();
    }

    document.addEventListener("click", function (ev) {
        var el = findTrackable(ev.target);
        if (!el || isSensitiveControl(el))
            return;

        var href = hrefFor(el);
        var text = labelFor(el);
        if (!href && !text)
            return;

        // Ignore pure in-page hash links with no useful label beyond "#".
        if (el.tagName === "A" && !href)
            return;

        enqueue({
            type: "click",
            path: path,
            query: query,
            pageViewId: pageViewId,
            href: href || "",
            text: text || ""
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
