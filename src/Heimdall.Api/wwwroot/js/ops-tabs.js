(function () {
  "use strict";

  var pane = document.getElementById("hd-ops-pane");
  if (!pane) return;

  var body = pane.querySelector("[data-hd-ops-body]");
  var loading = pane.querySelector("[data-hd-ops-loading]");
  var tabs = document.querySelectorAll("[data-hd-ops-tab]");
  var cache = Object.create(null);
  var active = pane.getAttribute("data-hd-ops-active") || "machines";
  var basePath = pane.getAttribute("data-hd-ops-base") || "/Fleet";
  var abort = null;

  function setLoading(on) {
    if (loading) loading.hidden = !on;
    if (body) body.hidden = on;
  }

  function activateTab(key) {
    tabs.forEach(function (a) {
      var on = a.getAttribute("data-hd-ops-tab") === key;
      a.classList.toggle("active", on);
      a.setAttribute("aria-selected", on ? "true" : "false");
    });
    pane.setAttribute("data-hd-ops-active", key);
    active = key;
  }

  function runScripts(container) {
    var scripts = container.querySelectorAll("script");
    scripts.forEach(function (old) {
      var s = document.createElement("script");
      if (old.src) {
        s.src = old.src;
        s.async = false;
      } else {
        s.textContent = old.textContent;
      }
      old.parentNode.replaceChild(s, old);
    });
    if (window.HeimdallTable && typeof window.HeimdallTable.init === "function") {
      window.HeimdallTable.init(container);
    }
  }

  function loadTab(key, src, pushUrl) {
    activateTab(key);
    if (pushUrl) {
      var url = basePath + "?tab=" + encodeURIComponent(key);
      history.pushState({ hdOpsTab: key, hdOpsBase: basePath }, "", url);
    }

    if (cache[key]) {
      setLoading(false);
      body.innerHTML = cache[key];
      runScripts(body);
      return;
    }

    if (abort) abort.abort();
    abort = new AbortController();
    setLoading(true);
    body.innerHTML = "";

    fetch(src, {
      credentials: "same-origin",
      headers: {
        "X-Fleet-Partial": "1",
        "X-Ops-Partial": "1",
        Accept: "text/html"
      },
      signal: abort.signal
    })
      .then(function (r) {
        if (!r.ok) throw new Error("HTTP " + r.status);
        return r.text();
      })
      .then(function (html) {
        cache[key] = html;
        setLoading(false);
        body.innerHTML = html;
        runScripts(body);
      })
      .catch(function (err) {
        if (err && err.name === "AbortError") return;
        setLoading(false);
        body.hidden = false;
        body.innerHTML =
          '<div class="alert alert-danger">Failed to load this tab. <a href="' +
          basePath +
          "?tab=" +
          encodeURIComponent(key) +
          '">Reload</a></div>';
      });
  }

  tabs.forEach(function (a) {
    a.addEventListener("click", function (e) {
      e.preventDefault();
      var key = a.getAttribute("data-hd-ops-tab");
      var src = a.getAttribute("data-hd-ops-src");
      if (!key || !src) return;
      if (key === active && body && !body.hidden && body.innerHTML) return;
      loadTab(key, src, true);
    });
  });

  window.addEventListener("popstate", function () {
    var params = new URLSearchParams(location.search);
    var key = params.get("tab") || active;
    var link = document.querySelector('[data-hd-ops-tab="' + key + '"]');
    if (link) loadTab(key, link.getAttribute("data-hd-ops-src"), false);
  });

  // First paint: load only the active tab.
  var initial = document.querySelector('[data-hd-ops-tab="' + active + '"]');
  if (initial) {
    loadTab(active, initial.getAttribute("data-hd-ops-src"), false);
  }
})();
