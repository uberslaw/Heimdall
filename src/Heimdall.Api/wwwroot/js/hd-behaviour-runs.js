/**
 * Flood → Run behaviour: Runs card filters (user / machine / date / status / limit),
 * table-count drill-in, and show/hide GPU (per-run + all).
 */
(function () {
  "use strict";

  function norm(s) {
    return (s || "").trim().toLowerCase();
  }

  function pageRoot(fromEl) {
    return (
      (fromEl && fromEl.closest && fromEl.closest("[data-hd-ops-body]")) ||
      document
    );
  }

  function runsPanel(root) {
    return (root && root.querySelector && root.querySelector("#hd-behaviour-runs")) ||
      document.getElementById("hd-behaviour-runs");
  }

  function getControls(panel) {
    if (!panel) return null;
    return {
      panel,
      user: panel.querySelector("[data-runs-user]"),
      machine: panel.querySelector("[data-runs-machine]"),
      from: panel.querySelector("[data-runs-from]"),
      to: panel.querySelector("[data-runs-to]"),
      status: panel.querySelector("[data-runs-status]"),
      limit: panel.querySelector("[data-runs-limit]"),
      label: panel.querySelector("[data-runs-filter-label]"),
      empty: panel.querySelector("[data-runs-filter-empty]"),
      list: panel.querySelector("[data-runs-list]"),
      gpuAll: panel.querySelector("[data-gpu-toggle-all]")
    };
  }

  function setSelectValue(select, value) {
    if (!select) return;
    const want = value || "";
    let found = false;
    for (let i = 0; i < select.options.length; i++) {
      if (select.options[i].value === want) {
        select.selectedIndex = i;
        found = true;
        break;
      }
    }
    if (!found && want) {
      // Case-insensitive match for table drill-in values.
      const n = norm(want);
      for (let i = 0; i < select.options.length; i++) {
        if (norm(select.options[i].value) === n) {
          select.selectedIndex = i;
          found = true;
          break;
        }
      }
    }
    if (!found) select.value = "";
  }

  function setGpuVisible(block, show) {
    const panel = block.querySelector("[data-gpu-panel]");
    const btn = block.querySelector("[data-gpu-toggle]");
    if (panel) panel.hidden = !show;
    if (btn) {
      btn.setAttribute("aria-pressed", show ? "true" : "false");
      btn.textContent = show ? "Hide chart" : "Show chart";
    }
    block.setAttribute("data-gpu-shown", show ? "1" : "0");
  }

  function syncGpuAllButton(panel) {
    const btn = panel.querySelector("[data-gpu-toggle-all]");
    if (!btn) return;
    const blocks = panel.querySelectorAll(".hd-run-block:not([hidden])");
    let anyHidden = false;
    blocks.forEach((b) => {
      if (b.getAttribute("data-gpu-shown") !== "1") anyHidden = true;
    });
    // If any visible run has GPU hidden, primary action is show-all; else hide-all.
    const showAllNext = anyHidden || blocks.length === 0;
    btn.setAttribute("aria-pressed", showAllNext ? "false" : "true");
    btn.textContent = showAllNext ? "Show all charts" : "Hide all charts";
  }

  function applyFilters(root, opts) {
    opts = opts || {};
    const panel = runsPanel(root);
    const c = getControls(panel);
    if (!c || !c.list) return;

    const user = (c.user && c.user.value) || "";
    const machine = (c.machine && c.machine.value) || "";
    const from = (c.from && c.from.value) || "";
    const to = (c.to && c.to.value) || "";
    const status = (c.status && c.status.value) || "all";
    const limitRaw = c.limit ? Number(c.limit.value) : 25;
    const limit = Number.isFinite(limitRaw) ? limitRaw : 25;

    const blocks = Array.from(c.list.querySelectorAll(".hd-run-block"));
    let matched = 0;
    let shown = 0;

    blocks.forEach((el) => {
      const matchUser = !user || norm(el.getAttribute("data-run-user")) === norm(user);
      const matchMachine =
        !machine || norm(el.getAttribute("data-run-machine")) === norm(machine);
      const start = el.getAttribute("data-run-start") || "";
      const matchFrom = !from || (start && start >= from);
      const matchTo = !to || (start && start <= to);
      const isOpen = el.getAttribute("data-run-open") === "1";
      const matchStatus =
        status === "all" ||
        (status === "completed" && !isOpen) ||
        (status === "open" && isOpen);

      const pass = matchUser && matchMachine && matchFrom && matchTo && matchStatus;
      if (pass) matched++;

      const withinLimit = limit <= 0 || matched <= limit;
      const show = pass && withinLimit;
      el.hidden = !show;
      if (show) shown++;
    });

    if (c.label) {
      const bits = [];
      if (user) bits.push("user “" + user + "”");
      if (machine) bits.push("machine “" + machine + "”");
      if (from || to) bits.push("dates " + (from || "…") + " → " + (to || "…"));
      if (status !== "all") bits.push(status);
      if (limit > 0) bits.push("limit " + limit);
      c.label.textContent =
        bits.length === 0
          ? "Showing " + shown + " of " + blocks.length + " listed runs"
          : "Filtered (" + bits.join(" · ") + ") · " + shown + " shown" +
            (matched > shown ? " (" + matched + " matched)" : "");
    }
    if (c.empty) c.empty.hidden = shown > 0;

    // Highlight matching table drill-in buttons when status+dim match.
    const rootDoc = pageRoot(panel);
    rootDoc.querySelectorAll(".hd-runs-filter-btn").forEach((btn) => {
      const dim = btn.getAttribute("data-runs-filter-dim");
      const val = btn.getAttribute("data-runs-filter-value") || "";
      const st = btn.getAttribute("data-runs-filter-status") || "all";
      const on =
        (dim === "user" && user && norm(val) === norm(user) &&
          ((st === "completed" && status === "completed") ||
            (st === "all" && status === "all"))) ||
        (dim === "machine" && machine && norm(val) === norm(machine) &&
          ((st === "completed" && status === "completed") ||
            (st === "all" && status === "all")));
      btn.classList.toggle("is-active", !!on);
    });

    syncGpuAllButton(panel);
    if (opts.scroll) {
      panel.scrollIntoView({ behavior: "smooth", block: "start" });
    }
    window.dispatchEvent(new Event("resize"));
  }

  function clearFilters(root) {
    const panel = runsPanel(root);
    const c = getControls(panel);
    if (!c) return;
    if (c.user) c.user.value = "";
    if (c.machine) c.machine.value = "";
    if (c.from) c.from.value = "";
    if (c.to) c.to.value = "";
    if (c.status) c.status.value = "all";
    if (c.limit) c.limit.value = "0";
    applyFilters(root);
  }

  function drillFromTable(root, dim, value, status) {
    const panel = runsPanel(root);
    const c = getControls(panel);
    if (!c) return;
    if (dim === "user") {
      setSelectValue(c.user, value);
      if (c.machine) c.machine.value = "";
    } else {
      setSelectValue(c.machine, value);
      if (c.user) c.user.value = "";
    }
    if (c.status) c.status.value = status === "completed" ? "completed" : "all";
    if (c.from) c.from.value = "";
    if (c.to) c.to.value = "";
    applyFilters(root, { scroll: true });
  }

  function onClick(ev) {
    const t = ev.target;
    if (!t || !t.closest) return;

    const tableBtn = t.closest(".hd-runs-filter-btn");
    if (tableBtn) {
      ev.preventDefault();
      drillFromTable(
        pageRoot(tableBtn),
        tableBtn.getAttribute("data-runs-filter-dim") || "user",
        tableBtn.getAttribute("data-runs-filter-value") || "",
        tableBtn.getAttribute("data-runs-filter-status") || "all"
      );
      return;
    }

    const clear = t.closest("[data-runs-filter-clear]");
    if (clear) {
      ev.preventDefault();
      clearFilters(pageRoot(clear));
      return;
    }

    const gpuAll = t.closest("[data-gpu-toggle-all]");
    if (gpuAll) {
      ev.preventDefault();
      const panel = runsPanel(pageRoot(gpuAll));
      if (!panel) return;
      const show = gpuAll.getAttribute("aria-pressed") !== "true";
      panel.querySelectorAll(".hd-run-block").forEach((b) => setGpuVisible(b, show));
      gpuAll.setAttribute("aria-pressed", show ? "true" : "false");
      gpuAll.textContent = show ? "Hide all charts" : "Show all charts";
      window.dispatchEvent(new Event("resize"));
      return;
    }

    const gpuOne = t.closest("[data-gpu-toggle]");
    if (gpuOne) {
      ev.preventDefault();
      const block = gpuOne.closest(".hd-run-block");
      if (!block) return;
      const show = gpuOne.getAttribute("aria-pressed") !== "true";
      setGpuVisible(block, show);
      const panel = runsPanel(pageRoot(gpuOne));
      if (panel) syncGpuAllButton(panel);
      window.dispatchEvent(new Event("resize"));
    }
  }

  function onChange(ev) {
    const t = ev.target;
    if (!t || !t.closest) return;
    if (
      t.matches(
        "[data-runs-user], [data-runs-machine], [data-runs-from], [data-runs-to], [data-runs-status], [data-runs-limit]"
      )
    ) {
      applyFilters(pageRoot(t));
    }
  }

  function initPanel(panel) {
    if (!panel || panel.getAttribute("data-runs-ready") === "1") return;
    panel.setAttribute("data-runs-ready", "1");
    panel.querySelectorAll(".hd-run-block").forEach((b) => {
      if (!b.hasAttribute("data-gpu-shown")) setGpuVisible(b, true);
    });
    applyFilters(panel);
  }

  function boot(root) {
    if (document.documentElement.getAttribute("data-behaviour-runs-bound") !== "1") {
      document.documentElement.setAttribute("data-behaviour-runs-bound", "1");
      document.addEventListener("click", onClick);
      document.addEventListener("change", onChange);
    }
    const scope = root && root.querySelector ? root : document;
    const panel =
      (scope.id === "hd-behaviour-runs" && scope) ||
      scope.querySelector("#hd-behaviour-runs") ||
      document.getElementById("hd-behaviour-runs");
    if (panel) {
      // Fresh partial HTML: allow re-init even if a previous panel had the flag.
      if (panel.getAttribute("data-runs-ready") === "1" && !panel.querySelector("[data-runs-list]")) {
        return;
      }
      panel.removeAttribute("data-runs-ready");
      initPanel(panel);
    }
  }

  if (document.readyState === "loading") {
    document.addEventListener("DOMContentLoaded", () => boot(document));
  } else {
    boot(document);
  }

  window.HeimdallBehaviourRuns = { init: boot };
})();
