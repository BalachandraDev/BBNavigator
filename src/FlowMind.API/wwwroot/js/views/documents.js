import { h, clear, fill, badge, statusInfo, toast, formatDate, empty, confirmDialog, closeOnOutside } from '../dom.js';
import { api } from '../api.js';
import { icon } from '../chatkit.js';

const MAX_BYTES = 25 * 1024 * 1024;
const ALLOWED = ['pdf', 'docx', 'txt'];
const STATUS_FOR_RETRY = ['AwaitingOcr', 'AwaitingAnalysis', 'Failed', 'Extracted'];

// Where a document is on its way from upload to being usable by the assistant.
const STAGES = ['Upload', 'Extract', 'Analyse', 'Review', 'Live'];
const STAGE_OF = { Uploaded: 0, Queued: 0, Processing: 1, AwaitingOcr: 1, Extracted: 1, Analyzing: 2, AwaitingAnalysis: 2, PendingReview: 3, Approved: 4 };
const ATTENTION = ['Failed', 'AwaitingOcr', 'AwaitingAnalysis'];
const FILTERS = [
  ['all', 'All', () => true],
  ['busy', 'In progress', d => statusInfo(d.status).busy],
  ['review', 'Needs review', d => d.status === 'PendingReview'],
  ['approved', 'Live', d => d.status === 'Approved'],
  ['attention', 'Needs attention', d => ATTENTION.includes(d.status)],
];
const extOf = name => (String(name).split('.').pop() || '').toLowerCase();

function stepper(status) {
  const failed = status === 'Failed', warn = status === 'AwaitingOcr' || status === 'AwaitingAnalysis';
  const cur = status === 'Approved' ? STAGES.length : (STAGE_OF[status] ?? 1);
  return h('ol', { class: 'stepper', 'aria-label': 'Progress' }, STAGES.map((s, i) => h('li', { class: i < cur ? 'done' : i === cur ? (failed ? 'now bad' : warn ? 'now warn' : 'now') : '' }, h('i'), h('span', null, s))));
}

export async function documentsView(main, ctx) {
  const listHost = h('div', { class: 'doclist' });
  const chipsHost = h('div', { class: 'chips-row', role: 'group', 'aria-label': 'Filter documents' });
  const uploadMsg = h('p', { class: 'hint', role: 'status' });
  let docs = [];
  let links = [];
  let timer = null;
  let filter = 'all', query = '';
  const expanded = new Set();
  let alive = true;
  ctx.onCleanup(() => { alive = false; clearTimeout(timer); });

  // ---- upload ----
  const file = h('input', { type: 'file', id: 'file', accept: '.pdf,.docx,.txt', class: 'sr-only' });
  const zone = h('div', { class: 'dropzone' },
    h('div', { class: 'dz-icon' }, icon('upload', 26)),
    h('div', { class: 'dz-text' },
      h('strong', null, 'Drop a document here'), h('span', null, 'or choose a file from your computer'),
      h('div', { class: 'dz-types' }, ['PDF', 'DOCX', 'TXT'].map(t => h('span', null, t)), h('span', { class: 'muted' }, 'up to 25 MB'))),
    h('label', { class: 'btn btn-primary', for: 'file' }, icon('plus', 16), 'Choose file'), file,
    h('p', { class: 'hint dz-note' }, 'Use synthetic or non-confidential documents until your company approves the AI providers.'),
    uploadMsg);
  ['dragenter', 'dragover'].forEach(ev => zone.addEventListener(ev, e => { e.preventDefault(); zone.classList.add('over'); }));
  ['dragleave', 'drop'].forEach(ev => zone.addEventListener(ev, e => { e.preventDefault(); zone.classList.remove('over'); }));
  zone.addEventListener('drop', e => { if (e.dataTransfer.files[0]) upload(e.dataTransfer.files[0]); });
  file.addEventListener('change', () => { if (file.files[0]) upload(file.files[0]); file.value = ''; });

  async function upload(f) {
    const ext = extOf(f.name);
    if (!ALLOWED.includes(ext)) { uploadMsg.textContent = 'That file type is not supported. Use PDF, DOCX or TXT.'; return; }
    if (f.size > MAX_BYTES) { uploadMsg.textContent = 'That file is larger than 25 MB.'; return; }
    uploadMsg.textContent = 'Uploading ' + f.name + '…';
    const form = new FormData();
    form.append('ApplicationId', ctx.appId); form.append('File', f);
    try {
      const r = await api('POST', '/documents', { form });
      uploadMsg.textContent = '';
      toast(r.duplicate ? 'This exact file was already uploaded; nothing new was processed.' : 'Uploaded. Text extraction has started.', r.duplicate ? 'info' : 'ok');
      await refresh();
    } catch (e) { uploadMsg.textContent = ''; toast(e.message, 'bad'); }
  }

  // ---- list ----
  async function refresh() {
    try { docs = await api('GET', '/documents?applicationId=' + encodeURIComponent(ctx.appId)); }
    catch (e) { if (!alive) return; toast(e.message, 'bad'); return; }
    try { links = await api('GET', '/bot-links'); } catch { links = []; }   // which share links each document is mapped to
    if (!alive) return;
    draw();
    clearTimeout(timer);
    if (docs.some(d => statusInfo(d.status).busy)) timer = setTimeout(refresh, 4000);
  }

  function drawChips() {
    fill(chipsHost, FILTERS.map(([key, label, test]) => {
      const n = docs.filter(test).length;
      return h('button', { type: 'button', class: 'fchip' + (key === 'attention' && n ? ' warn' : ''), 'aria-pressed': String(filter === key), onclick: () => { filter = key; draw(); } },
        label, h('b', null, String(n)));
    }));
  }

  function draw() {
    drawChips();
    clear(listHost);
    if (!docs.length) { listHost.append(empty('No documents yet', 'Drop your first document above. It is read, analysed, then waits for your approval.', 'upload')); return; }
    const test = FILTERS.find(f => f[0] === filter)[2], q = query.trim().toLowerCase();
    const shown = docs.filter(d => test(d) && (!q || String(d.fileName).toLowerCase().includes(q)));
    if (!shown.length) { listHost.append(empty('Nothing matches', 'Try another filter or clear the search.', 'search')); return; }
    shown.forEach(d => listHost.append(card(d)));
    expanded.forEach(id => loadDetail(id));
  }

  function card(d) {
    const st = statusInfo(d.status), ext = extOf(d.fileName);
    return h('article', { class: 'doc tone-' + st.tone },
      h('div', { class: 'doc-top' },
        h('span', { class: 'ftile ft-' + (ALLOWED.includes(ext) ? ext : 'x') }, (ext || 'file').slice(0, 4).toUpperCase()),
        h('div', { class: 'doc-info' }, h('strong', { class: 'doc-name', title: d.fileName }, d.fileName), h('small', null, 'Uploaded ' + formatDate(d.createdUtc))),
        h('span', { class: 'badge badge-' + st.tone }, st.busy ? h('span', { class: 'spinner', 'aria-hidden': 'true' }) : null, st.label)),
      stepper(d.status),
      linkRow(d),
      h('div', { class: 'actions doc-actions' }, actions(d)),
      expanded.has(d.id) ? h('div', { class: 'doc-detail', id: 'detail-' + d.id }, 'Loading…') : null);
  }

  /** Which share links can answer from this document: links limited to it, and links that use every approved document. */
  function linkRow(d) {
    if (d.status !== 'Approved') return null;
    const mine = links.filter(l => l.applicationId === d.applicationId);
    const scoped = mine.filter(l => l.documentIds.includes(d.id));
    const open = mine.filter(l => l.documentIds.length === 0);
    return h('div', { class: 'doc-links' }, icon('share', 14), h('span', { class: 'muted' }, 'Share links:'),
      scoped.map(l => h('span', { class: 'chip' }, l.name)),
      open.length ? h('span', { class: 'chip soft', title: open.map(l => l.name).join(', ') }, `${open.length} using all documents`) : null,
      !scoped.length && !open.length ? h('span', { class: 'muted' }, 'not on any share link yet') : null);
  }

  /** Add or remove this document from the share links that are limited to chosen documents. */
  function mapDialog(d) {
    const mine = links.filter(l => l.applicationId === d.applicationId);
    const scopedLinks = mine.filter(l => l.documentIds.length > 0), openLinks = mine.filter(l => l.documentIds.length === 0);
    const wanted = new Map(scopedLinks.map(l => [l.id, l.documentIds.includes(d.id)]));
    const err = h('p', { class: 'error-text', role: 'alert' });
    const dlg = h('dialog', { class: 'dialog', 'aria-label': 'Share links for ' + d.fileName });
    const rows = [
      ...scopedLinks.map(l => {
        const only = l.documentIds.length === 1 && l.documentIds[0] === d.id;
        const box = h('input', { type: 'checkbox', checked: wanted.get(l.id), disabled: only, onchange: e => wanted.set(l.id, e.target.checked) });
        return h('label', { class: 'docpick' }, box, h('span', null, h('strong', null, l.name),
          h('small', null, only ? 'This is the only document of this link. Add another one before removing it.' : `${l.documentIds.length} documents mapped`)));
      }),
      ...openLinks.map(l => h('label', { class: 'docpick off' }, h('input', { type: 'checkbox', checked: true, disabled: true }),
        h('span', null, h('strong', null, l.name), h('small', null, 'Uses all approved documents, so this one is included. To limit it, open Share links and choose documents.')))),
    ];
    fill(dlg,
      h('h2', null, 'Share links for this document'), h('p', { class: 'muted' }, d.fileName),
      rows.length ? h('div', { class: 'pickpanel' }, h('div', { class: 'checklist' }, rows))
        : h('p', null, 'There are no share links for this application yet. Create one under Share links.'),
      err,
      h('div', { class: 'actions' },
        h('button', { class: 'btn', type: 'button', onclick: () => dlg.close() }, 'Cancel'),
        h('button', { class: 'btn', type: 'button', onclick: () => { dlg.close(); ctx.navigate('share'); } }, 'Open Share links'),
        scopedLinks.length ? h('button', { class: 'btn btn-primary', type: 'button', onclick: async () => {
          try {
            for (const l of scopedLinks) {
              const has = l.documentIds.includes(d.id), want = wanted.get(l.id);
              if (has === want) continue;
              const ids = want ? [...l.documentIds, d.id] : l.documentIds.filter(x => x !== d.id);
              await api('PATCH', '/bot-links/' + l.id, { body: { documentIds: ids } });
            }
            dlg.close(); toast('Share links updated.', 'ok'); await refresh();
          } catch (e) { err.textContent = e.message; }
        } }, 'Save') : null));
    dlg.addEventListener('close', () => dlg.remove());
    closeOnOutside(dlg);
    document.body.append(dlg); dlg.showModal();
  }

  // ---- add a small update to an uploaded document (instead of uploading the whole file again) ----
  // from = { title, question, gapKey } when it is opened from an unanswered question in Insights: the question becomes the heading of the update
  // and is marked handled once the update is saved.
  function updateDialog(d, from = {}) {
    const MAX_TEXT = 20000, MAX_FILE = 2 * 1024 * 1024;
    let mode = 'text';
    const dlg = h('dialog', { class: 'dialog', 'aria-label': 'Add an update to ' + d.fileName });
    const title = h('input', { type: 'text', id: 'upd-title', maxlength: 120, placeholder: 'e.g. Discharge changes in version 4.2', value: (from.title || '').slice(0, 120) });
    const text = h('textarea', { id: 'upd-text', rows: 9, maxlength: MAX_TEXT, placeholder: 'Write or paste only what is new or changed. For example: "From version 4.2 the discharge screen has an Insurance tab. Fill it before printing."' });
    const count = h('small', { class: 'hint' }, `0 / ${MAX_TEXT.toLocaleString()} characters`);
    text.addEventListener('input', () => { count.textContent = `${text.value.length.toLocaleString()} / ${MAX_TEXT.toLocaleString()} characters`; });
    const file = h('input', { type: 'file', id: 'upd-file', accept: '.pdf,.docx,.txt' });
    const textPane = h('div', { class: 'field' }, h('label', { for: 'upd-text' }, 'New or changed text'), text, count);
    const filePane = h('div', { class: 'field', hidden: true }, h('label', { for: 'upd-file' }, 'File with only the new or changed part'), file,
      h('small', { class: 'hint' }, 'PDF, DOCX or TXT with real text, up to 2 MB and 20 pages. Scanned pages are not supported here.'));
    const err = h('p', { class: 'error-text', role: 'alert' });
    const mBtn = (m, label) => h('button', { class: 'btn btn-sm', type: 'button', 'aria-pressed': String(m === mode), onclick: () => setMode(m) }, label);
    const bText = mBtn('text', 'Paste text'), bFile = mBtn('file', 'Upload a small file');
    function setMode(m) {
      mode = m; err.textContent = '';
      textPane.hidden = m !== 'text'; filePane.hidden = m !== 'file';
      [[bText, 'text'], [bFile, 'file']].forEach(([b, k]) => { b.setAttribute('aria-pressed', String(k === m)); b.classList.toggle('btn-primary', k === m); });
    }
    bText.classList.add('btn-primary');
    const go = h('button', { class: 'btn btn-primary', type: 'button', onclick: submit }, 'Add update');

    async function submit() {
      err.textContent = '';
      let call;
      if (mode === 'text') {
        if (!text.value.trim()) { err.textContent = 'Write the update text first.'; return; }
        call = () => api('POST', `/documents/${d.id}/updates/text`, { body: { title: title.value.trim(), text: text.value } });
      } else {
        const f = file.files[0];
        if (!f) { err.textContent = 'Choose a file first.'; return; }
        if (!ALLOWED.includes(extOf(f.name))) { err.textContent = 'That file type is not supported. Use PDF, DOCX or TXT.'; return; }
        if (f.size > MAX_FILE) { err.textContent = 'That file is larger than 2 MB. For bigger changes upload the whole document again.'; return; }
        const form = new FormData(); form.append('Title', title.value.trim()); form.append('File', f);
        call = () => api('POST', `/documents/${d.id}/updates/file`, { form });
      }
      go.disabled = true; go.textContent = 'Adding…';
      try {
        const r = await call();
        if (from.gapKey) { try { await api('POST', '/insights/gaps/state', { body: { applicationId: ctx.appId, key: from.gapKey, status: 'handled' } }); } catch { /* the update itself succeeded */ } }
        dlg.close();
        toast(`Update added (${r.addedPages} page${r.addedPages === 1 ? '' : 's'}). It is being analysed. Then review the new items and approve.`, 'ok');
        await refresh();
      } catch (e) { err.textContent = e.message; go.disabled = false; go.textContent = 'Add update'; }
    }

    fill(dlg,
      h('div', { class: 'dlg-head' }, h('h2', null, 'Add an update'),
        h('button', { class: 'btn btn-sm icon-btn', type: 'button', 'aria-label': 'Close', title: 'Close', onclick: () => dlg.close() }, icon('close', 16))),
      h('p', { class: 'muted' }, d.fileName),
      from.question ? h('p', { class: 'notice', role: 'note' }, 'Fixing a question people asked: ', h('strong', null, from.question), '. Write what the answer is, so the assistant can answer it next time.') : null,
      h('p', null, 'Add only what changed. It is attached to the end of this document and only the new text is analysed, so you do not need to upload the whole file again. Everything already approved stays approved.'),
      h('div', { class: 'field' }, h('label', { for: 'upd-title' }, 'Short title (optional)'), title),
      h('div', { class: 'actions', style: 'justify-content:flex-start' }, bText, bFile),
      textPane, filePane, err,
      h('div', { class: 'actions' }, h('button', { class: 'btn', type: 'button', onclick: () => dlg.close() }, 'Cancel'), go));
    dlg.addEventListener('close', () => dlg.remove());
    closeOnOutside(dlg);
    document.body.append(dlg); dlg.showModal(); text.focus();
  }

  function actions(d) {
    const b = (label, fn, cls = '', ic = null) => h('button', { class: 'btn btn-sm ' + cls, type: 'button', onclick: fn }, ic ? icon(ic, 14) : null, label);
    const a = [];
    if (d.status === 'PendingReview') {
      a.push(b('Review', () => ctx.navigate('review', { documentId: d.id }), 'btn-primary', 'review'));
      a.push(b('Approve', () => approve(d), '', 'check'));
    }
    if (STATUS_FOR_RETRY.includes(d.status)) a.push(b('Retry', () => retry(d), d.status === 'Failed' ? 'btn-primary' : '', 'refresh'));
    if (d.status === 'Approved') { a.push(b('Share links', () => mapDialog(d), 'btn-primary', 'share')); a.push(b('Re-index', () => reindex(d), '', 'refresh')); }
    if (d.status === 'Approved' || d.status === 'PendingReview') a.push(b('Add update', () => updateDialog(d), '', 'plus'));
    if (d.status !== 'Uploaded' && d.status !== 'Queued') a.push(b('View text', () => showPages(d), '', 'doc'));
    a.push(b(expanded.has(d.id) ? 'Hide details' : 'Details', () => { expanded.has(d.id) ? expanded.delete(d.id) : expanded.add(d.id); draw(); }, '', 'info'));
    a.push(h('span', { class: 'spacer' }), b('Delete', () => remove(d), 'btn-danger', 'trash'));
    return a;
  }

  async function loadDetail(id) {
    const cell = document.getElementById('detail-' + id);
    if (!cell) return;
    try {
      const s = await api('GET', `/documents/${id}/status`);
      const info = statusInfo(s.status);
      const job = s.latestJob;
      fill(cell,
        info.hint ? h('p', null, info.hint) : null,
        h('dl', { class: 'kv' },
          h('dt', null, 'Version'), h('dd', null, s.versionNumber ?? '—'),
          h('dt', null, 'Pages'), h('dd', null, `${s.pageCount ?? 0} (${s.pagesDigital} digital, ${s.pagesOcr} OCR, ${s.pagesAwaitingOcr} waiting for OCR)`),
          job ? [h('dt', null, 'Last step'), h('dd', null, `${job.stage}: ${job.status}` + (job.completedUtc ? ' · ' + formatDate(job.completedUtc) : ''))] : null,
          job && job.error ? [h('dt', null, 'Note'), h('dd', null, job.error)] : null));
    } catch (e) { if (cell.isConnected) cell.textContent = e.message; }
  }

  async function showPages(d) {
    const dlg = h('dialog', { class: 'dialog', 'aria-label': 'Extracted text of ' + d.fileName }, h('h2', null, d.fileName), h('p', { class: 'muted' }, 'Loading…'));
    dlg.addEventListener('close', () => dlg.remove());
    closeOnOutside(dlg);
    document.body.append(dlg); dlg.showModal();
    // The title bar stays visible while the pages scroll, so Close is always one click away (also Esc, or click outside).
    const head = () => h('div', { class: 'dlg-head' }, h('h2', null, d.fileName),
      h('button', { class: 'btn btn-sm icon-btn', type: 'button', 'aria-label': 'Close', title: 'Close', onclick: () => dlg.close() }, icon('close', 16)));
    fill(dlg, head(), h('p', { class: 'muted' }, 'Loading…'));
    try {
      const pages = await api('GET', `/documents/${d.id}/pages?take=100`);
      fill(dlg, head(), h('p', { class: 'muted' }, 'Text extracted from each page. Page numbers here are the ones used in answers.'),
        pages.length ? pages.map(p => h('div', { class: 'section' },
          h('h3', null, 'Page ' + p.pageNumber + ' ', badge(p.source === 'Ocr' ? 'OCR' : p.source === 'NeedsOcr' ? 'Needs OCR' : 'Digital', p.source === 'NeedsOcr' ? 'warn' : 'info')),
          h('div', { class: 'page-text' }, p.text || '(no text)'))) : h('p', null, 'No pages extracted yet.'),
        h('div', { class: 'actions' }, h('button', { class: 'btn', type: 'button', onclick: () => dlg.close() }, 'Close')));
    } catch (e) { fill(dlg, h('p', null, e.message), h('button', { class: 'btn', type: 'button', onclick: () => dlg.close() }, 'Close')); }
  }

  async function approve(d) {
    try {
      const r = await api('POST', `/documents/${d.id}/approve`);
      toast(`Approved ${r.approved} extracted items. The document is now being indexed for chat.`, 'ok');
      await refresh();
    } catch (e) { toast(e.message, 'bad'); }
  }
  async function retry(d) { try { await api('POST', `/documents/${d.id}/retry`); toast('Queued again. Finished work is not repeated.', 'ok'); await refresh(); } catch (e) { toast(e.message, 'bad'); } }
  async function reindex(d) { try { await api('POST', `/documents/${d.id}/reindex`); toast('Indexing queued. Already indexed sections are not re-sent.', 'ok'); expanded.add(d.id); draw(); } catch (e) { toast(e.message, 'bad'); } }
  async function remove(d) {
    if (!await confirmDialog(`Delete "${d.fileName}"? Its file, extracted text and search index are removed. This cannot be undone.`)) return;
    try { await api('DELETE', `/documents/${d.id}`); toast('Document deleted.', 'ok'); await refresh(); } catch (e) { toast(e.message, 'bad'); }
  }

  const search = h('input', { type: 'search', class: 'search', placeholder: 'Search documents', 'aria-label': 'Search documents', oninput: e => { query = e.target.value; draw(); } });
  main.append(
    h('div', { class: 'page-head' }, h('div', null, h('h2', null, 'Documents'),
      h('p', null, 'Upload documentation for ', h('strong', null, ctx.app?.name || 'this application'), '. Each file is read, analysed, then waits for your approval.'))),
    zone,
    h('section', { class: 'block' },
      h('div', { class: 'listbar' }, chipsHost, h('label', { class: 'searchbox' }, icon('search', 16), search)),
      listHost));
  await refresh();

  // Arrived from Insights ("Add update" next to an unanswered question): open the update dialog for that document.
  const q = ctx.routeQuery();
  if (q.get('addUpdate')) {
    const target = docs.find(x => x.id === q.get('addUpdate'));
    const from = { title: q.get('title') || '', question: q.get('question') || '', gapKey: q.get('gap') || '' };
    history.replaceState(null, '', '#/documents');   // a reload must not reopen it
    if (target && (target.status === 'Approved' || target.status === 'PendingReview')) updateDialog(target, from);
    else toast('That document cannot take an update right now. Wait until it is approved or ready for review.', 'info');
  }
}
