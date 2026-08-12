(() => {
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

  function initSort(root) {
    root.querySelectorAll('table.hd-sortable').forEach(table => {
      const headers = [...table.querySelectorAll('thead th[data-sort]')];
      headers.forEach(th => {
        if (th.dataset.sortBound === 'true') return;
        th.dataset.sortBound = 'true';
        th.style.cursor = 'pointer';
        if (!th.getAttribute('title')) th.title = 'Sort';
        th.addEventListener('click', () => {
          const type = th.dataset.sort;
          const colIdx = columnIndex(th);
          const tbody = table.tBodies[0];
          if (!tbody) return;
          const rows = [...tbody.querySelectorAll('tr')].filter(r => r.children.length > 1);
          // First click / empty dir → descending; then toggle asc ↔ desc.
          const asc = th.dataset.dir === 'desc';
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
          rows.forEach(r => tbody.appendChild(r));
        });
        if (th.dataset.defaultDesc !== undefined) th.click();
      });
    });
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
  }

  window.HeimdallTable = { initSort, initTextFilter, initToasts, init };

  if (document.readyState === 'loading') {
    document.addEventListener('DOMContentLoaded', () => init(document));
  } else {
    init(document);
  }
})();
