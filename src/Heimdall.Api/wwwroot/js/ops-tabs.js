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

  function tabSrc(key) {
    var link = document.querySelector('[data-hd-ops-tab="' + key + '"]');
    return link ? link.getAttribute("data-hd-ops-src") : null;
  }

  function loadTab(key, src, pushUrl) {
    activateTab(key);
    if (!src) return;
    if (pushUrl) {
      var url = new URL(location.href);
      url.searchParams.set("tab", key);
      history.pushState({ hdOpsTab: key, hdOpsBase: basePath }, "", url.pathname + url.search);
    }

    var cached = cache[src];
    if (cached) {
      setLoading(false);
      body.innerHTML = cached;
      runScripts(body);
      if (window.HeimdallTable && typeof window.HeimdallTable.restoreScroll === "function") {
        window.HeimdallTable.restoreScroll();
      }
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
        cache[src] = html;
        setLoading(false);
        body.innerHTML = html;
        runScripts(body);
        if (window.HeimdallTable && typeof window.HeimdallTable.restoreScroll === "function") {
          window.HeimdallTable.restoreScroll();
        }
      })
      .catch(function (err) {
        if (err && err.name === "AbortError") return;
        setLoading(false);
        body.hidden = false;
        body.innerHTML =
          '<div class="hd-toast hd-toast-error">Failed to load this tab. <a href="' +
          basePath +
          "?tab=" +
          encodeURIComponent(key) +
          '">Reload</a></div>';
      });
  }

  function applyGetForm(form) {
    var action = form.getAttribute("action") || location.pathname;
    var params = new URLSearchParams(new FormData(form));
    params.set("partial", "1");
    var path = action.split("?")[0];
    var src = path + "?" + params.toString();
    var link = document.querySelector('[data-hd-ops-tab="' + active + '"]');
    if (link) link.setAttribute("data-hd-ops-src", src);
    Object.keys(cache).forEach(function (k) {
      if (k.indexOf(path) === 0) delete cache[k];
    });
    var fleet = new URL(location.href);
    params.forEach(function (v, k) {
      if (k === "partial") return;
      fleet.searchParams.set(k, v);
    });
    fleet.searchParams.set("tab", active);
    history.replaceState({ hdOpsTab: active, hdOpsBase: basePath }, "", fleet.pathname + fleet.search);
    loadTab(active, src, false);
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

  if (body) {
    body.addEventListener("submit", function (e) {
      var form = e.target;
      if (!form || form.tagName !== "FORM") return;
      if ((form.getAttribute("method") || "get").toLowerCase() !== "get") return;
      if (form.getAttribute("data-hd-ops-full") === "1") return;
      e.preventDefault();
      applyGetForm(form);
    });
  }

  window.addEventListener("popstate", function () {
    var params = new URLSearchParams(location.search);
    var key = params.get("tab") || active;
    var src = tabSrc(key);
    if (src) loadTab(key, src, false);
  });

  window.HeimdallOpsTabs = {
    invalidate: function (key) {
      if (key) {
        var src = tabSrc(key);
        if (src) delete cache[src];
        Object.keys(cache).forEach(function (k) {
          if (k.indexOf(key) >= 0) delete cache[k];
        });
      } else {
        Object.keys(cache).forEach(function (k) {
          delete cache[k];
        });
      }
    },
    refreshActive: function () {
      var src = tabSrc(active);
      if (!src) return;
      delete cache[src];
      loadTab(active, src, false);
    }
  };

  var initial = document.querySelector('[data-hd-ops-tab="' + active + '"]');
  if (initial) {
    loadTab(active, initial.getAttribute("data-hd-ops-src"), false);
  }
})();
