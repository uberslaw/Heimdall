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

  function basenameFromFile(file) {
    if (!file || !file.name) return '';
    let name = file.name.replace(/^.*[\\/]/, '');
    name = name.replace(/\.exe$/i, '');
    return name.trim();
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

  function submitAdd(kind, name) {
    const n = (name || '').trim();
    if (!n) return;
    document.getElementById('add-list-kind').value = kind;
    document.getElementById('add-process-name').value = n;
    document.getElementById('form-add-process').submit();
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
      submitAdd(kind, name);
    });

    root.querySelector('.hd-process-input')?.addEventListener('keydown', e => {
      if (e.key === 'Enter') {
        e.preventDefault();
        root.querySelector('.hd-process-add')?.click();
      }
    });

    const fileInput = root.querySelector('.hd-process-file');
    root.querySelector('.hd-process-browse')?.addEventListener('click', () => {
      if (!canMutate) return;
      fileInput?.click();
    });
    fileInput?.addEventListener('change', () => {
      const file = fileInput.files?.[0];
      const name = basenameFromFile(file);
      fileInput.value = '';
      if (!name) return;
      const text = root.querySelector('.hd-process-input');
      if (text) text.value = name;
      submitAdd(kind, name);
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

  function wireAppBrowse() {
    const btn = document.getElementById('browse-new-app');
    const file = document.getElementById('browse-new-app-file');
    const process = document.getElementById('new-app-process');
    const display = document.getElementById('new-app-display');
    if (!btn || !file) return;
    btn.addEventListener('click', () => file.click());
    file.addEventListener('change', () => {
      const name = basenameFromFile(file.files?.[0]);
      file.value = '';
      if (!name) return;
      if (process) process.value = name;
      if (display && !display.value.trim()) display.value = name;
    });
  }

  window.HeimdallProcessList = { init, basenameFromFile, wireAppBrowse };
})();
