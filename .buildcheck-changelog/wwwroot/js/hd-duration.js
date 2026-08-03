// Shared "clickable large duration" control. Pairs with Heimdall.Api.Services.DurationDisplay (server-side
// default render) and the .hd-duration CSS class. Any element with class="hd-duration" and a
// data-seconds="<total seconds>" attribute becomes clickable: it starts on the largest sensible
// whole-number unit, then cycles seconds -> minutes -> hours -> days -> weeks -> months -> years -> back
// to the auto default on each click. Works for both server-rendered spans and ones inserted later by JS
// (e.g. session-drilldown.js) via HeimdallDuration.init(container).
(function () {
    "use strict";

    var UNITS = [
        { labelLong: "second", secondsPer: 1 },
        { labelLong: "minute", secondsPer: 60 },
        { labelLong: "hour", secondsPer: 3600 },
        { labelLong: "day", secondsPer: 86400 },
        { labelLong: "week", secondsPer: 604800 },
        { labelLong: "month", secondsPer: 2629746 }, // average Gregorian month (30.4368 days)
        { labelLong: "year", secondsPer: 31556952 }  // average Gregorian year (365.2425 days)
    ];

    function pickAutoUnitIndex(totalSeconds) {
        for (var i = 0; i < UNITS.length - 1; i++) {
            if (totalSeconds < UNITS[i + 1].secondsPer) return i;
        }
        return UNITS.length - 1;
    }

    function formatUnit(totalSeconds, unitIndex) {
        var unit = UNITS[unitIndex];
        var value = Math.round(totalSeconds / unit.secondsPer);
        var label = value === 1 ? unit.labelLong : (unit.labelLong + "s");
        return value.toLocaleString() + " " + label;
    }

    function getState(el) {
        var attr = el.getAttribute("data-hd-duration-state");
        return attr === null ? -1 : parseInt(attr, 10);
    }

    function render(el) {
        var totalSeconds = parseFloat(el.getAttribute("data-seconds")) || 0;
        var state = getState(el);
        var text = state < 0
            ? formatUnit(totalSeconds, pickAutoUnitIndex(totalSeconds))
            : formatUnit(totalSeconds, state % UNITS.length);
        el.textContent = text;
    }

    function cycle(el) {
        var state = getState(el) + 1;
        if (state >= UNITS.length) state = -1;
        el.setAttribute("data-hd-duration-state", state);
        render(el);
    }

    function init(root) {
        (root || document).querySelectorAll(".hd-duration").forEach(function (el) {
            if (el.dataset.hdDurationInit) return;
            el.dataset.hdDurationInit = "1";
            render(el);
            if (!el.hasAttribute("title")) {
                el.setAttribute("title", "Click to cycle units (seconds \u2192 minutes \u2192 hours \u2192 days \u2192 weeks \u2192 months \u2192 years)");
            }
            el.addEventListener("click", function (e) {
                e.stopPropagation();
                cycle(el);
            });
        });
    }

    document.addEventListener("DOMContentLoaded", function () { init(document); });
    window.HeimdallDuration = { init: init };
})();
