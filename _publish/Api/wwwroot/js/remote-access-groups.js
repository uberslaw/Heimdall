(function () {
    "use strict";

    function initPicker(picker) {
        var filter = picker.querySelector("[data-hd-machine-filter]");
        var list = picker.querySelector("[data-hd-machine-checklist]");
        if (!filter || !list) return;

        filter.addEventListener("input", function () {
            var term = filter.value.trim().toLowerCase();
            list.querySelectorAll(".hd-process-item").forEach(function (item) {
                var name = item.querySelector(".hd-process-name");
                var text = name ? name.textContent.toLowerCase() : "";
                item.style.display = term === "" || text.indexOf(term) !== -1 ? "" : "none";
            });
        });
    }

    function init() {
        document.querySelectorAll("[data-hd-machine-picker]").forEach(initPicker);
    }

    if (document.readyState === "loading") {
        document.addEventListener("DOMContentLoaded", init);
    } else {
        init();
    }
})();
