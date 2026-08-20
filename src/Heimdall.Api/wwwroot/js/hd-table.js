(() => {
  const SORT_PREFIX = 'hd-sort:';
  const SCROLL_PREFIX = 'hd-scroll:';

  function columnIndex(th) {
    if (th.dataset.sortCol !== undefined && th.dataset.sortCol !== '') {
      const n = parseInt(th.dataset.sortCol, 10);
      if (!Number.isNaN(n)) return n;
    }
    return th.cellIndex;
  }

  function compareRows(type, va, vb, asc) {
    if (type === 'num' || type === 'date') {
      let na = parseFloat(va);
      let nb = parseFloat(vb);
      if (Number.isNaN(na)) na = -Infinity;
      if (Number.isNaN(nb)) nb = -Infinity;
      return asc ? na - nb : nb - na;
    }
    const sa = String(va).toLowerCase();
    const sb = String(vb).toLowerCase();
    return asc ? sa.localeCompare(sb) : sb.localeCompare(sa);
  }

  function pageKey() {
    return location.pathname + location.search;
  }

  function tableSortKey(table) {
    const id = table.id || table.getAttribute('data-hd-sort-key');
    if (id) return SORT_PREFIX + pageKey() + ':' + id;
    const tables = [...document.querySelectorAll('table.hd-sortable')];
    const idx = tables.indexOf(table);
    return SORT_PREFIX + pageKey() + ':t' + (idx >= 0 ? idx : 0);
  }

  function saveSort(table, colIdx, dir) {
    try {
      sessionStorage.setItem(tableSortKey(table), JSON.stringify({ colIdx, dir }));
    } catch {
      /* ignore */
    }
  }

  function loadSort(table) {
    try {
      const raw = sessionStorage.getItem(tableSortKey(table));
      if (!raw) return null;
      return JSON.parse(raw);
    } catch {
      return null;
    }
  }

  function sortTableByHeader(table, th, forceDir) {
    const type = th.dataset.sort;
    const colIdx = columnIndex(th);
    const tbody = table.tBodies[0];
    if (!tbody) return;
    const headers = [...table.querySelectorAll('thead th[data-sort]')];
    // Primary rows only — follow rows (e.g. Flood Live charts under a machine) stay attached after sort.
    const followByKey = new Map();
    tbody.querySelectorAll('tr[data-follow-for]').forEach(r => {
      followByKey.set(r.getAttribute('data-follow-for'), r);
    });
    const rows = [...tbody.querySelectorAll('tr')].filter(
      r => !r.hasAttribute('data-follow-for') && r.children.length > 1
    );
    let asc;
    if (forceDir === 'asc' || forceDir === 'desc') {
      asc = forceDir === 'asc';
    } else {
      // First click / empty dir → descending; then toggle asc ↔ desc.
      asc = th.dataset.dir === 'desc';
    }
    headers.forEach(h => {
      h.dataset.dir = '';
      h.classList.remove('sorted-asc', 'sorted-desc');
    });
    th.dataset.dir = asc ? 'asc' : 'desc';
    th.classList.add(asc ? 'sorted-asc' : 'sorted-desc');
    rows.sort((a, b) => {
      const ca = a.children[colIdx];
      const cb = b.children[colIdx];
      const va = ca?.dataset.sortValue ?? ca?.textContent ?? '';
      const vb = cb?.dataset.sortValue ?? cb?.textContent ?? '';
      return compareRows(type, va, vb, asc);
    });
    rows.forEach(r => {
      tbody.appendChild(r);
      const id = r.getAttribute('data-machine-id');
      if (id && followByKey.has(id)) tbody.appendChild(followByKey.get(id));
    });
    saveSort(table, colIdx, th.dataset.dir);
  }

  function restoreSort(table) {
    const saved = loadSort(table);
    if (!saved || saved.colIdx == null) return false;
    const headers = [...table.querySelectorAll('thead th[data-sort]')];
    const th = headers.find(h => columnIndex(h) === saved.colIdx) || headers[saved.colIdx];
    if (!th) return false;
    sortTableByHeader(table, th, saved.dir === 'asc' ? 'asc' : 'desc');
    return true;
  }

  function initSort(root) {
    root.querySelectorAll('table.hd-sortable').forEach(table => {
      const headers = [...table.querySelectorAll('thead th[data-sort]')];
      headers.forEach(th => {
        if (th.dataset.sortBound === 'true') return;
        th.dataset.sortBound = 'true';
        th.style.cursor = 'pointer';
        if (!th.getAttribute('title')) th.title = 'Sort';
        th.addEventListener('click', () => sortTableByHeader(table, th));
      });
      if (!restoreSort(table)) {
        const def = headers.find(h => h.dataset.defaultDesc !== undefined);
        if (def) sortTableByHeader(table, def, 'desc');
      }
    });
  }

  function scrollStorageKey() {
    return SCROLL_PREFIX + pageKey();
  }

  function saveScroll() {
    try {
      sessionStorage.setItem(scrollStorageKey(), String(Math.round(window.scrollY || 0)));
    } catch {
      /* ignore */
    }
  }

  function restoreScroll() {
    try {
      const raw = sessionStorage.getItem(scrollStorageKey());
      if (raw == null || raw === '') return;
      const y = parseInt(raw, 10);
      if (!Number.isFinite(y) || y < 0) return;
      requestAnimationFrame(() => {
        window.scrollTo(0, y);
        // Tab partials paint late — nudge once more.
        setTimeout(() => window.scrollTo(0, y), 50);
      });
    } catch {
      /* ignore */
    }
  }

  let scrollTimer = null;
  function bindScrollPersist() {
    if (window.__hdScrollPersistBound) return;
    window.__hdScrollPersistBound = true;
    window.addEventListener('scroll', () => {
      if (scrollTimer) clearTimeout(scrollTimer);
      scrollTimer = setTimeout(saveScroll, 150);
    }, { passive: true });
    window.addEventListener('pagehide', saveScroll);
  }

  function initTextFilter(root) {
    root.querySelectorAll('[data-filter-for]').forEach(input => {
      if (input.dataset.filterBound === 'true') return;
      input.dataset.filterBound = 'true';
      input.addEventListener('input', () => {
        const tableId = input.getAttribute('data-filter-for');
        const table = document.getElementById(tableId);
        if (!table) return;
        const q = input.value.trim().toLowerCase();
        table.querySelectorAll('tbody tr').forEach(tr => {
          if (tableId === 'inventory-table') return;
          tr.style.display = !q || tr.textContent.toLowerCase().includes(q) ? '' : 'none';
        });
      });
    });
  }

  function initToasts(root) {
    const scope = root || document;
    scope.querySelectorAll('.hd-toast[data-autodismiss]').forEach((toast) => {
      if (toast.dataset.hdToastBound === 'true') return;
      toast.dataset.hdToastBound = 'true';
      const ms = parseInt(toast.getAttribute('data-autodismiss') || '5000', 10);
      const dismiss = () => {
        toast.classList.add('hd-toast-hide');
        setTimeout(() => toast.remove(), 350);
      };
      const timer = setTimeout(dismiss, Number.isFinite(ms) ? ms : 5000);
      toast.querySelector('.hd-toast-close')?.addEventListener('click', () => {
        clearTimeout(timer);
        dismiss();
      });
    });
  }

  function init(root) {
    const scope = root || document;
    initSort(scope);
    initTextFilter(scope);
    initToasts(scope);
    bindScrollPersist();
    restoreScroll();
  }

  window.HeimdallTable = {
    initSort,
    initTextFilter,
    initToasts,
    init,
    restoreSort,
    saveScroll,
    restoreScroll
  };

  if (document.readyState === 'loading') {
    document.addEventListener('DOMContentLoaded', () => init(document));
  } else {
    init(document);
  }
})();
