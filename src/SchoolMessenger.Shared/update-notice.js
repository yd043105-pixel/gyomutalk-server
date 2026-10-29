'use strict';
// Bundled release notes only. Never fetch GitHub or include account/message data.
window.UpdateNotice = (() => {
  let dialog, offered = false, loading, generation = 0;
  const key = 'yy-update-confirmed-sequence';
  function confirmed() {
    try { const value = Number(localStorage.getItem(key)); return Number.isSafeInteger(value) && value >= 0 ? value : 0; } catch { return 0; }
  }
  async function show(audience, manual = false) {
    if (dialog?.open || (!manual && offered)) return;
    const ticket = generation;
    try {
      loading ??= fetch('/updates.json', { cache: 'no-store', credentials: 'same-origin', signal: AbortSignal.timeout(10000) })
        .then(response => { if (!response.ok) throw Error('업데이트 내역 연결 실패'); return response.json(); });
      const notes = await loading;
      if (ticket !== generation || dialog?.open || (!manual && offered)) return;
      if (!Number.isSafeInteger(notes.sequence) || notes.sequence < 1 || !notes.audiences[audience]) return;
      offered = true;
      if (!manual && notes.sequence <= confirmed()) return;
      const element = (tag, text) => { const item = document.createElement(tag); if (text !== undefined) item.textContent = text; return item; };
      dialog ??= element('dialog');
      dialog.className = 'update-notice'; dialog.setAttribute('aria-labelledby', 'update-notice-title');
      const title = element('h2', '업데이트 내역 · ' + notes.version); title.id = 'update-notice-title';
      const content = element('div'); content.className = 'update-notice-content'; content.append(element('p', notes.releasedOn));
      for (const [label, items] of Object.entries(notes.audiences[audience])) {
        const list = element('ul'); content.append(element('h3', label), list);
        for (const item of items) list.append(element('li', item));
      }
      const actions = element('div'); actions.className = 'update-notice-actions';
      const later = element('button', '나중에'), accept = element('button', '확인');
      later.type = accept.type = 'button'; accept.className = 'primary';
      later.onclick = () => dialog.close();
      accept.onclick = () => {
        try { localStorage.setItem(key, String(Math.max(confirmed(), notes.sequence))); }
        catch { /* Browsers that block storage may show the notice again next visit. */ }
        dialog.close();
      };
      actions.append(later, accept); dialog.replaceChildren(title, content, actions);
      if (!dialog.isConnected) document.body.append(dialog);
      dialog.showModal(); accept.focus();
    } catch {
      loading = undefined;
      // An optional notice must never prevent login or normal school work.
      if (manual) window.alert('업데이트 내역을 불러오지 못했습니다. 잠시 후 다시 시도하세요.');
    }
  }
  function reset() { generation++; offered = false; loading = undefined; dialog?.close(); }
  return { show, reset };
})();
