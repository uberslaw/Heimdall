(() => {
  function initSort(root) {
    root.querySelectorAll('table.hd-sortable').forEach(table => {
      const headers = [...table.querySelectorAll('thead th[data-sort]')];
      headers.forEach((th, colIdx) => {
        if (th.dataset.sortBound === 'true') return;
        th.dataset.sortBound = 'true';
        th.style.cursor = 'pointer';
        th.title = 'Sort';
        th.addEventListener('click', () => {
          const type = th.dataset.sort;
          const tbody = table.tBodies[0];
          const rows = [...tbody.querySelectorAll('tr')].filter(r => r.children.length > 1);
          const asc = th.dataset.dir !== 'asc';
          headers.forEach(h => {
            h.dataset.dir = '';
            h.classList.remove('sorted-asc', 'sorted-desc');
          });
          th.dataset.dir = asc ? 'asc' : 'desc';
          th.classList.add(asc ? 'sorted-asc' : 'sorted-desc');
          rows.sort((a, b) => {
            const ca = a.children[colIdx];
            const cb = b.children[colIdx];
            let va = ca?.dataset.sortValue ?? ca?.textContent ?? '';
            let vb = cb?.dataset.sortValue ?? cb?.textContent ?? '';
            if (type === 'num' || type === 'date') {
              va = parseFloat(va);
              vb = parseFloat(vb);
              if (Number.isNaN(va)) va = -Infinity;
              if (Number.isNaN(vb)) vb = -Infinity;
              return asc ? va - vb : vb - va;
            }
            va = String(va).toLowerCase();
            vb = String(vb).toLowerCase();
            return asc ? va.localeCompare(vb) : vb.localeCompare(va);
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

  window.HeimdallTable = { initSort, initTextFilter };
})();
