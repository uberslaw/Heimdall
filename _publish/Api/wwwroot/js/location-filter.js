window.HeimdallLocationFilter = (function () {
  function syncChildren(checkbox, checked) {
    const container = checkbox.closest('.hd-tree-country, .hd-tree-city');
    if (!container) return;
    const childrenWrap = container.querySelector(':scope > .hd-tree-children');
    if (!childrenWrap) return;
    childrenWrap.querySelectorAll('.hd-tree-check').forEach(c => {
      c.checked = checked;
      c.indeterminate = false;
    });
  }

  function syncAncestors(checkbox) {
    let node = checkbox.closest('.hd-tree-city, .hd-tree-country');
    while (node) {
      const parentCheck = node.querySelector(':scope > .hd-tree-node > .hd-tree-check');
      const childChecks = [...node.querySelectorAll(':scope > .hd-tree-children .hd-tree-check')];
      if (parentCheck && childChecks.length) {
        const all = childChecks.every(c => c.checked);
        const some = childChecks.some(c => c.checked || c.indeterminate);
        parentCheck.checked = all;
        parentCheck.indeterminate = !all && some;
      }
      node = node.parentElement?.closest('.hd-tree-city, .hd-tree-country') || null;
    }
  }

  function collect(tree) {
    const countries = [];
    const cities = [];

    tree.querySelectorAll('.hd-tree-country').forEach(countryEl => {
      const countryCheck = countryEl.querySelector(':scope > .hd-tree-node > .hd-tree-check');
      if (countryCheck?.checked && !countryCheck.indeterminate) {
        countries.push(countryCheck.value);
        return;
      }
      countryEl.querySelectorAll(':scope > .hd-tree-children > .hd-tree-city .hd-tree-check').forEach(c => {
        if (c.checked) cities.push(c.value);
      });
    });

    return { countries, cities };
  }

  function writeHidden(tree, data) {
    const box = tree.querySelector('.hd-tree-hidden-inputs');
    if (!box) return;
    const countryName = tree.dataset.countryInput || 'SelectedCountries';
    const cityName = tree.dataset.cityInput || 'SelectedCities';
    box.innerHTML = '';
    const add = (name, values) => {
      values.forEach(v => {
        const input = document.createElement('input');
        input.type = 'hidden';
        input.name = name;
        input.value = v;
        box.appendChild(input);
      });
    };
    add(countryName, data.countries);
    add(cityName, data.cities);
  }

  function init(tree, options) {
    if (!tree) return;
    const autoSubmit = options?.autoSubmit !== false;

    tree.querySelectorAll('.hd-tree-check').forEach(cb => syncAncestors(cb));
    writeHidden(tree, collect(tree));

    tree.addEventListener('change', e => {
      const cb = e.target;
      if (!cb.classList?.contains('hd-tree-check')) return;
      if (cb.dataset.level === 'country' || cb.dataset.level === 'city') {
        syncChildren(cb, cb.checked);
      }
      syncAncestors(cb);
      writeHidden(tree, collect(tree));
      if (autoSubmit) {
        const form = tree.closest('form');
        if (form) form.requestSubmit();
      }
    });

    const form = tree.closest('form');
    if (form) {
      form.addEventListener('submit', () => writeHidden(tree, collect(tree)));
    }
  }

  return { init, collect };
})();
