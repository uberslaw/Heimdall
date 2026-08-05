window.HeimdallTree = (function () {
  function syncChildren(checkbox, checked) {
    const container = checkbox.closest('.hd-tree-region, .hd-tree-office');
    if (!container) return;
    const childrenWrap = container.querySelector(':scope > .hd-tree-children');
    if (!childrenWrap) return;
    childrenWrap.querySelectorAll('.hd-tree-check').forEach(c => {
      c.checked = checked;
      c.indeterminate = false;
    });
  }

  function syncAncestors(checkbox) {
    let node = checkbox.closest('.hd-tree-office, .hd-tree-region');
    while (node) {
      const parentCheck = node.querySelector(':scope > .hd-tree-node > .hd-tree-check');
      const childChecks = [...node.querySelectorAll(':scope > .hd-tree-children .hd-tree-check')];
      if (parentCheck && childChecks.length) {
        const all = childChecks.every(c => c.checked);
        const some = childChecks.some(c => c.checked || c.indeterminate);
        parentCheck.checked = all;
        parentCheck.indeterminate = !all && some;
      }
      node = node.parentElement?.closest('.hd-tree-office, .hd-tree-region') || null;
    }
  }

  function collect(tree) {
    const regions = [];
    const offices = [];
    const machines = [];

    tree.querySelectorAll('.hd-tree-region').forEach(regionEl => {
      const regionCheck = regionEl.querySelector(':scope > .hd-tree-node > .hd-tree-check');
      if (regionCheck?.checked && !regionCheck.indeterminate) {
        regions.push(regionCheck.value);
        return; // whole region — do not also emit children
      }
      regionEl.querySelectorAll(':scope > .hd-tree-children > .hd-tree-office').forEach(officeEl => {
        const officeCheck = officeEl.querySelector(':scope > .hd-tree-node > .hd-tree-check');
        if (officeCheck?.checked && !officeCheck.indeterminate) {
          offices.push(officeCheck.value);
          return;
        }
        officeEl.querySelectorAll('.hd-tree-machine .hd-tree-check').forEach(m => {
          if (m.checked) machines.push(m.value);
        });
      });
    });

    return { regions, offices, machines };
  }

  function writeHidden(tree, data) {
    const box = tree.querySelector('.hd-tree-hidden-inputs');
    if (!box) return;
    const regionName = tree.dataset.regionInput || 'SelectedRegions';
    const officeName = tree.dataset.officeInput || 'SelectedOffices';
    const machineName = tree.dataset.machineInput || 'SelectedMachines';
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
    add(regionName, data.regions);
    add(officeName, data.offices);
    add(machineName, data.machines);
  }

  function init(tree) {
    if (!tree) return;
    tree.addEventListener('change', e => {
      const cb = e.target;
      if (!cb.classList?.contains('hd-tree-check')) return;
      if (cb.dataset.level === 'region' || cb.dataset.level === 'office') {
        syncChildren(cb, cb.checked);
      }
      syncAncestors(cb);
      writeHidden(tree, collect(tree));
    });

    const form = tree.closest('form');
    if (form) {
      form.addEventListener('submit', () => writeHidden(tree, collect(tree)));
    }
  }

  return { init, collect };
})();
