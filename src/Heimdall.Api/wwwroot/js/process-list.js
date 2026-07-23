(() => {
  function selectedNames(root) {
    return [...root.querySelectorAll('.hd-process-check:checked')].map(cb => cb.value);
  }

  function fillSelected(container, names) {
    container.innerHTML = '';
    names.forEach(n => {
      const input = document.createElement('input');
      input.type = 'hidden';
      input.name = 'SelectedProcesses';
      input.value = n;
      container.appendChild(input);
    });
  }

  function syncActions(root) {
    const selected = selectedNames(root);
    const actions = root.querySelector('.hd-process-actions');
    const pauseControls = root.querySelector('.hd-pause-controls');
    if (!actions) return;
    const show = selected.length > 0 && root.dataset.canMutate === 'true';
    actions.hidden = !show;
    if (pauseControls) pauseControls.hidden = !show;
  }

  function init(root) {
    if (!root) return;
    const kind = root.dataset.listKind || 'Include';
    const canMutate = root.dataset.canMutate === 'true';

    root.addEventListener('change', e => {
      if (e.target.classList?.contains('hd-process-check')) syncActions(root);
    });

    root.querySelector('.hd-process-add')?.addEventListener('click', () => {
      if (!canMutate) return;
      const input = root.querySelector('.hd-process-input');
      const name = (input?.value || '').trim();
      if (!name) {
        input?.focus();
        return;
      }
      document.getElementById('add-list-kind').value = kind;
      document.getElementById('add-process-name').value = name;
      document.getElementById('form-add-process').submit();
    });

    root.querySelector('.hd-process-input')?.addEventListener('keydown', e => {
      if (e.key === 'Enter') {
        e.preventDefault();
        root.querySelector('.hd-process-add')?.click();
      }
    });

    root.querySelector('.hd-process-remove')?.addEventListener('click', () => {
      const names = selectedNames(root);
      if (!names.length) return;
      document.getElementById('remove-list-kind').value = kind;
      fillSelected(document.getElementById('remove-selected'), names);
      document.getElementById('form-remove-process').submit();
    });

    root.querySelector('.hd-process-pause')?.addEventListener('click', () => {
      const names = selectedNames(root);
      if (!names.length) return;
      document.getElementById('pause-list-kind').value = kind;
      document.getElementById('pause-preset').value = root.querySelector('.hd-pause-preset')?.value || '';
      document.getElementById('pause-days').value = root.querySelector('.hd-pause-days')?.value || '0';
      document.getElementById('pause-hours').value = root.querySelector('.hd-pause-hours')?.value || '0';
      document.getElementById('pause-minutes').value = root.querySelector('.hd-pause-minutes')?.value || '0';
      fillSelected(document.getElementById('pause-selected'), names);
      document.getElementById('form-pause-process').submit();
    });

    root.querySelector('.hd-process-unpause')?.addEventListener('click', () => {
      const names = selectedNames(root);
      if (!names.length) return;
      document.getElementById('unpause-list-kind').value = kind;
      fillSelected(document.getElementById('unpause-selected'), names);
      document.getElementById('form-unpause-process').submit();
    });

    syncActions(root);
  }

  window.HeimdallProcessList = { init };
})();
