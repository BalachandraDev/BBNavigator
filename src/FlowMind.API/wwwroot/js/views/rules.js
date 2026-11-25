import { h, fill, toast, empty, confirmDialog, closeOnOutside } from '../dom.js';
import { api } from '../api.js';
import { icon } from '../chatkit.js';

// Access rules (company admins). Pick a document, then tick who may see each part of it. One row at the top ("Whole document") sets everything at once;
// the rows below fine-tune single modules and pages. The server enforces every rule: a restricted key is only ever searched and answered from the
// pages its audience may see. Nothing changes for anyone until "Save changes" is pressed.

const STARTERS = [
  ['Manager', 'manager, supervisor, administrator, head'],
  ['Nurse', 'nurse, nursing, staff nurse, sister'],
  ['Ward', 'ward, ward staff, ward user'],
  ['Doctor', 'doctor, physician, consultant, clinician'],
  ['Front desk', 'front desk, reception, receptionist, registration'],
  ['Pharmacy', 'pharmacist, pharmacy'],
  ['Billing', 'billing, cashier, accounts'],
  ['Lab', 'lab, laboratory, technician'],
];

const serverEff = rule => !rule ? { state: 'und', ids: new Set() } : rule.everyone ? { state: 'all', ids: new Set() } : { state: 'aud', ids: new Set(rule.audienceIds) };
const sameEff = (a, b) => a.state === b.state && (a.state !== 'aud' || (a.ids.size === b.ids.size && [...a.ids].every(i => b.ids.has(i))));
const storedDoc = () => { try { return sessionStorage.getItem('flowmind.rules.doc'); } catch { return null; } };
const storeDoc = id => { try { sessionStorage.setItem('flowmind.rules.doc', id); } catch { /* cosmetic */ } };

export async function rulesView(main, ctx) {
  let alive = true;
  ctx.onCleanup(() => { alive = false; window.onbeforeunload = null; });

  let auds = [], ov = null, found = [];
  let selDoc = storedDoc();
  let setupMode = false;
  let filter = 'all', searchText = '';
  const draft = new Map();           // row key -> unsaved effective state (with kind and id, so it survives switching documents)
  const suggested = new Map();       // row key -> notes read from the document
  const collapsed = new Set();
  let rows = new Map();              // rows of the document on screen

  const body = h('div', { class: 'rl-wrap' });
  const saveBar = h('div', { class: 'rl-savebar', role: 'region', 'aria-label': 'Unsaved changes', hidden: true });

  async function loadAll() {
    const appId = ctx.appId;
    const [a, o, f] = await Promise.all([
      api('GET', '/access/audiences'),
      appId ? api('GET', '/access/overview?applicationId=' + encodeURIComponent(appId)) : Promise.resolve(null),
      api('GET', '/access/roles-found').catch(() => []),
    ]);
    auds = a; ov = o; found = f;
  }
  async function reload() {
    try { await loadAll(); } catch (e) { if (alive) fill(body, h('div', { class: 'notice warn', role: 'alert' }, e.message || 'Could not load this page.')); return; }
    if (!alive) return;
    if (!auds.length) setupMode = true;   // first time: stay on the setup card while the groups are added, until Continue
    draft.clear(); suggested.clear(); updateBar(); paint();
  }

  // ---- documents: the unit this page is organised by ---------------------------------------------------------------------
  function groups() {
    if (!ov) return [];
    const byDoc = new Map(ov.documents.map(d => [d.id, { id: d.id, name: d.name, mods: new Map(), loose: [], leftover: d.otherPages > 0 ? d : null }]));
    const other = { id: 'none', name: 'Not tied to a document', mods: new Map(), loose: [], leftover: null };
    for (const m of ov.modules) {
      for (const p of m.pages) {
        // A page is listed under every document it draws on (with that document's page numbers), and under its own source document.
        const cited = (p.cited || []).filter(c => byDoc.has(c.documentId));
        const ids = new Set(cited.map(c => c.documentId));
        if (!ids.size || (p.source.documentId && byDoc.has(p.source.documentId))) ids.add(byDoc.has(p.source.documentId) ? p.source.documentId : 'none');
        for (const id of ids) {
          const g = byDoc.get(id) || other;
          const here = cited.find(c => c.documentId === id);
          const others = [...ids].filter(x => x !== id).map(x => byDoc.get(x)?.name).filter(Boolean);
          const view = { ...p, here: here ? 'p. ' + here.pages : null, alsoIn: others };
          if (m.id) { if (!g.mods.has(m.id)) g.mods.set(m.id, { mod: m, pages: [] }); g.mods.get(m.id).pages.push(view); } else g.loose.push(view);
        }
      }
      if (m.id && !m.pages.length) { const g = byDoc.get(m.source.documentId) || other; if (!g.mods.has(m.id)) g.mods.set(m.id, { mod: m, pages: [] }); }
    }
    const all = [...byDoc.values(), other].filter(g => g.mods.size || g.loose.length || g.leftover);
    for (const g of all) {
      const items = [...g.mods.values()].flatMap(x => [x.mod, ...x.pages]).concat(g.loose, g.leftover ? [g.leftover] : []);
      g.total = items.length; g.open = items.filter(i => !i.rule).length;
    }
    return all;
  }

  function paint() {
    const gs = groups();
    if (!gs.some(g => g.id === selDoc)) selDoc = (gs.find(g => g.open > 0) || gs[0])?.id || null;
    fill(body,
      h('div', { class: 'page-head split' },
        h('div', null, h('h2', null, 'Access rules'), h('p', null, 'Choose a document, then tick who may see each part of it.')),
        h('div', { class: 'actions' },
          h('button', { class: 'btn', type: 'button', onclick: openAudiences }, icon('user', 15), `Audiences${auds.length ? ' (' + auds.length + ')' : ''}`),
          h('a', { class: 'btn', href: '#/access' }, icon('key', 15), 'Keys'))),
      content(gs));
    updateBar();
  }

  function content(gs) {
    if (!ctx.appId) return empty('Create an application first', 'Access rules belong to an application and its documents.', 'building');
    if (!auds.length || setupMode) return setupCard();
    if (!gs.length) return h('div', { class: 'rl-stack' }, empty('Nothing to decide yet', 'Upload a module document and approve it. Its modules and pages will appear here to tick.', 'doc'),
      h('div', { class: 'actions rl-center' }, h('a', { class: 'btn btn-primary', href: '#/documents' }, icon('upload', 15), 'Go to Documents')));
    const g = gs.find(x => x.id === selDoc);
    return h('div', { class: 'rl-stack' },
      h('div', { class: 'rl-docs', role: 'tablist', 'aria-label': 'Documents' }, gs.map(d => h('button', { type: 'button', role: 'tab', class: 'rl-doc', 'aria-selected': String(d.id === selDoc),
        title: d.name, onclick: () => { selDoc = d.id; storeDoc(d.id); filter = 'all'; searchText = ''; paint(); } },
        icon('doc', 18), h('span', { class: 'rl-doc-t' }, h('strong', null, d.name), h('small', null, d.open ? `${d.open} of ${d.total} not decided` : `All ${d.total} decided`)),
        d.open ? h('span', { class: 'rl-dot und', 'aria-hidden': 'true' }) : h('span', { class: 'rl-ok', 'aria-hidden': 'true' }, icon('check', 12))))),
      gridFor(g));
  }

  // ---- first time: no audiences yet -----------------------------------------------------------------------------------------
  function setupCard() {
    const have = new Set(auds.map(a => a.name.toLowerCase()));
    const fromDocs = found.filter(r => !have.has(r.role.toLowerCase()));
    const offered = new Set(fromDocs.map(r => r.role.toLowerCase()));
    const starters = STARTERS.filter(([n]) => !have.has(n.toLowerCase()) && !offered.has(n.toLowerCase()));
    return h('section', { class: 'rl-setup' },
      h('span', { class: 'rl-setup-ic' }, icon('shield', 26)),
      h('h3', null, 'Who will use your documents?'),
      h('p', { class: 'muted' }, 'Add the groups of people, like Manager or Nurse. Then you choose, document by document, which parts each group may see.'),
      auds.length ? h('div', { class: 'rl-qrow' }, h('small', null, 'Added'),
        h('div', { class: 'chips' }, auds.map(a => h('span', { class: 'chip chip-done' }, icon('check', 12), a.name)))) : null,
      fromDocs.length ? h('div', { class: 'rl-qrow' }, h('small', null, 'Mentioned in your documents'), chips(fromDocs.map(r => [r.role, '']))) : null,
      starters.length ? h('div', { class: 'rl-qrow' }, h('small', null, 'Common groups'), chips(starters)) : null,
      ownForm(),
      h('div', { class: 'actions rl-center' },
        h('button', { class: 'btn btn-primary', type: 'button', disabled: !auds.length, onclick: () => { setupMode = false; paint(); } }, 'Continue', icon('chevron', 15))));
  }
  // "Or type your own": a plain field, so a group that is not in the suggestions is one line away.
  function ownForm() {
    const own = h('input', { type: 'text', maxlength: 80, autocomplete: 'off', placeholder: 'Or type your own, for example Lab technician', 'aria-label': 'Name of a new audience' });
    return h('form', { class: 'rl-create-row rl-own', onsubmit: async e => {
      e.preventDefault();
      const name = own.value.trim();
      if (name.length < 2) { toast('Type a name of at least 2 letters.', 'info'); own.focus(); return; }
      await createAudience({ name, aliases: '', description: '' });
    } }, own, h('button', { class: 'btn', type: 'submit' }, icon('plus', 15), 'Add'));
  }
  const chips = list => h('div', { class: 'chips' }, list.map(([n, al]) => h('button', { type: 'button', class: 'chip chip-add', onclick: () => createAudience({ name: n, aliases: al, description: '' }) }, icon('plus', 12), n)));

  // ---- the grid for one document ---------------------------------------------------------------------------------------------
  function gridFor(g) {
    rows = new Map();
    const colCount = auds.length + 3;
    const tbody = h('tbody');
    const noMatch = h('p', { class: 'muted rl-nomatch', hidden: true }, 'Nothing matches. Clear the search or the filter.');
    const search = h('input', { type: 'search', class: 'search', placeholder: 'Search this document', 'aria-label': 'Search this document', value: searchText, oninput: e => { searchText = e.target.value; applyFilter(); } });
    const filters = h('div', { class: 'choice', role: 'radiogroup', 'aria-label': 'Show' });
    const paintFilters = () => fill(filters, [['all', 'All'], ['open', 'Not decided']].map(([k, label]) =>
      h('button', { type: 'button', role: 'radio', 'aria-checked': String(filter === k), onclick: () => { filter = k; paintFilters(); applyFilter(); } }, label)));
    paintFilters();

    // ---- rows ----
    const makeRow = (kind, id, name, sub, rule, extra = {}) => { const r = { key: kind + ':' + id, kind, id, name, sub, server: serverEff(rule), kids: [], parent: null, ...extra }; rows.set(r.key, r); return r; };
    const eff = r => draft.get(r.key) || r.server;
    const setEff = (r, e) => { if (sameEff(e, r.server)) draft.delete(r.key); else draft.set(r.key, { ...e, kind: r.kind, id: r.id }); refresh(r); };

    const groupsOut = [];
    for (const { mod, pages } of g.mods.values()) {
      const m = makeRow('module', mod.id, mod.name, mod.description, mod.rule);
      m.kids = pages.map(p => makeRow('page', p.id, p.name, p.purpose, p.rule, { review: p.review, docPages: p.here, alsoIn: p.alsoIn, sharedWith: p.sharedWith, parent: m }));
      groupsOut.push(m);
    }
    const looseRows = g.loose.map(p => makeRow('page', p.id, p.name, p.purpose, p.rule, { review: p.review, docPages: p.here, alsoIn: p.alsoIn, sharedWith: p.sharedWith }));
    const leftover = g.leftover ? makeRow('document', g.leftover.id, 'Other pages of this document', `p. ${g.leftover.otherPageList}: introductions and anything not filed under a module or page`, g.leftover.rule) : null;

    // ---- changes ----
    const applyAud = (x, aid, on) => { const e = eff(x); if (e.state === 'all') return; const ids = new Set(e.ids); on ? ids.add(aid) : ids.delete(aid); setEff(x, { state: 'aud', ids }); };
    const applyAll = (x, on) => setEff(x, on ? { state: 'all', ids: new Set() } : { state: 'aud', ids: new Set() });
    const toggleAud = (r, aid, on) => { applyAud(r, aid, on); if (r.kind === 'module') r.kids.forEach(k => applyAud(k, aid, on)); afterChange(); };
    const toggleEveryone = (r, on) => { applyAll(r, on); if (r.kind === 'module') r.kids.forEach(k => applyAll(k, on)); afterChange(); };
    const adminsOnly = r => { for (const x of [r, ...r.kids]) if (eff(x).state === 'und') setEff(x, { state: 'aud', ids: new Set() }); afterChange(); };
    const resetRow = r => { setEff(r, { state: 'und', ids: new Set() }); afterChange(); };

    function buildRow(r, indent) {
      r.every = h('input', { type: 'checkbox', 'aria-label': `${r.name}: everyone`, onchange: e => toggleEveryone(r, e.target.checked) });
      r.boxes = new Map(auds.map(a => [a.id, h('input', { type: 'checkbox', 'aria-label': `${r.name}: ${a.name}`, onchange: e => toggleAud(r, a.id, e.target.checked) })]));
      r.pill = h('span', { class: 'rl-pill-wrap' });
      r.fold = r.kind === 'module'
        ? h('button', { type: 'button', class: 'rl-fold', 'aria-expanded': 'true', 'aria-label': `Show or hide the pages of ${r.name}`, onclick: () => { collapsed.has(r.id) ? collapsed.delete(r.id) : collapsed.add(r.id); applyFilter(); } }, icon('chevron', 14))
        : h('span', { class: 'rl-fold-gap' });
      r.sugg = h('span', { class: 'rl-sugg', hidden: true }, icon('sparkle', 12));
      const meta = [];
      if (r.kind === 'page' && r.docPages) meta.push(h('span', { class: 'rl-meta' }, r.docPages));
      if (r.alsoIn && r.alsoIn.length) meta.push(h('span', { class: 'rl-meta rl-also', title: 'Also used in: ' + r.alsoIn.join(', ') + '. The rule is the same in each document.' }, `also in ${r.alsoIn.length === 1 ? 'another document' : r.alsoIn.length + ' other documents'}`));
      if (r.review === 'Pending') meta.push(h('span', { class: 'rl-meta rl-unrev', title: 'Not reviewed yet' }, 'unreviewed'));
      if (r.sharedWith && r.sharedWith.length) meta.push(h('span', { class: 'rl-shared', title: `Shares document pages with ${r.sharedWith.join(', ')}. When two items share a page, the stricter rule applies to that page.` }, icon('alert', 12), 'shares a page'));
      r.tr = h('tr', { class: 'rl-row rl-' + r.kind },
        h('th', { scope: 'row', class: 'rl-name', style: `--indent:${indent}` }, h('div', { class: 'rl-name-in' }, r.fold,
          h('div', { class: 'rl-name-t' }, h('strong', null, r.name, r.sugg), r.sub ? h('small', null, r.sub) : null, meta.length ? h('div', { class: 'rl-metas' }, meta) : null))),
        h('td', { class: 'rl-cell' }, h('label', { class: 'rl-check', title: 'Everyone' }, r.every)),
        ...auds.map(a => h('td', { class: 'rl-cell' }, h('label', { class: 'rl-check', title: a.name }, r.boxes.get(a.id)))),
        h('td', { class: 'rl-cell rl-cell-st' }, r.pill));
      refresh(r);
      return r.tr;
    }

    function refresh(r) {
      if (!r.tr) return;
      const e = eff(r);
      r.every.checked = e.state === 'all';
      for (const [aid, box] of r.boxes) {
        const on = e.state === 'all' || (e.state === 'aud' && e.ids.has(aid));
        box.checked = on; box.disabled = e.state === 'all';
        box.indeterminate = r.kind === 'module' && r.kids.length > 0 && r.kids.some(k => { const ke = eff(k); return (ke.state === 'all' || (ke.state === 'aud' && ke.ids.has(aid))) !== on; });
      }
      r.every.indeterminate = r.kind === 'module' && r.kids.length > 0 && r.kids.some(k => (eff(k).state === 'all') !== (e.state === 'all'));
      const names = e.state === 'aud' ? auds.filter(a => e.ids.has(a.id)).map(a => a.name) : [];
      fill(r.pill,
        e.state === 'und' ? h('button', { type: 'button', class: 'rl-pill und', title: 'Not decided: only full-access keys see this. Click to make it admins only.', onclick: () => adminsOnly(r) }, 'Not decided')
          : h('span', { class: 'rl-pill ' + (e.state === 'all' ? 'all' : names.length ? 'aud' : 'adm'), title: names.length ? names.join(', ') : null },
            e.state === 'all' ? 'Everyone' : names.length ? (names.length === 1 ? names[0] : `${names.length} groups`) : 'Admins only'),
        e.state !== 'und' ? h('button', { type: 'button', class: 'rl-x', 'aria-label': `Clear the decision for ${r.name}`, title: 'Back to "not decided"', onclick: () => resetRow(r) }, icon('close', 12)) : null);
      r.tr.classList.toggle('dirty', draft.has(r.key));
      const s = suggested.get(r.key);
      r.sugg.hidden = !(s && draft.has(r.key));
      if (s) r.sugg.title = 'Suggested from your document: ' + s.join(' | ');
      scheduleBulk();
    }

    // ---- the "whole document" row ----
    const bulk = {
      every: h('input', { type: 'checkbox', 'aria-label': 'Whole document: everyone', onchange: e => { rows.forEach(r => applyAll(r, e.target.checked)); afterChange(); } }),
      boxes: new Map(auds.map(a => [a.id, h('input', { type: 'checkbox', 'aria-label': `Whole document: ${a.name}`, onchange: e => { rows.forEach(r => applyAud(r, a.id, e.target.checked)); afterChange(); } })])),
      note: h('span', { class: 'rl-bulk-note' }),
    };
    let bulkQueued = false;
    function scheduleBulk() { if (bulkQueued) return; bulkQueued = true; queueMicrotask(() => { bulkQueued = false; refreshBulk(); }); }
    function refreshBulk() {
      const es = [...rows.values()].map(eff);
      const allAll = es.length > 0 && es.every(e => e.state === 'all');
      bulk.every.checked = allAll; bulk.every.indeterminate = !allAll && es.some(e => e.state === 'all');
      for (const [aid, box] of bulk.boxes) {
        const on = es.map(e => e.state === 'all' || (e.state === 'aud' && e.ids.has(aid)));
        box.checked = on.length > 0 && on.every(Boolean); box.indeterminate = !box.checked && on.some(Boolean);
      }
      const open = es.filter(e => e.state === 'und').length;
      bulk.note.textContent = open ? `${open} not decided` : 'All decided';
      bulk.note.className = 'rl-bulk-note' + (open ? ' open' : '');
    }
    tbody.append(h('tr', { class: 'rl-row rl-bulk' },
      h('th', { scope: 'row', class: 'rl-name' }, h('div', { class: 'rl-name-in' }, h('span', { class: 'rl-fold-gap' }),
        h('div', { class: 'rl-name-t' }, h('strong', null, 'Whole document'), h('small', null, 'Tick here to set every module and page below at once.')))),
      h('td', { class: 'rl-cell' }, h('label', { class: 'rl-check', title: 'Everyone' }, bulk.every)),
      ...auds.map(a => h('td', { class: 'rl-cell' }, h('label', { class: 'rl-check', title: a.name }, bulk.boxes.get(a.id)))),
      h('td', { class: 'rl-cell rl-cell-st' }, bulk.note)));

    for (const m of groupsOut) { tbody.append(buildRow(m, 0)); m.kids.forEach(k => tbody.append(buildRow(k, 1))); }
    if (looseRows.length) {
      tbody.append(h('tr', { class: 'rl-group' }, h('td', { colspan: colCount }, 'Pages without a module')));
      looseRows.forEach(k => tbody.append(buildRow(k, 0)));
    }
    if (leftover) { tbody.append(h('tr', { class: 'rl-group' }, h('td', { colspan: colCount }, 'Rest of the document'))); tbody.append(buildRow(leftover, 0)); }

    // ---- filtering ----
    const textMatch = r => { const s = searchText.trim().toLowerCase(); return !s || r.name.toLowerCase().includes(s) || (r.sub || '').toLowerCase().includes(s); };
    const stateMatch = r => filter === 'all' || eff(r).state === 'und';
    const matchRow = r => textMatch(r) && stateMatch(r);
    function applyFilter() {
      const s = searchText.trim();
      for (const r of rows.values()) {
        if (r.kind === 'module') {
          const hide = !(matchRow(r) || r.kids.some(matchRow));
          r.tr.hidden = hide;
          const closed = collapsed.has(r.id) && !s;
          r.fold.setAttribute('aria-expanded', String(!closed)); r.fold.classList.toggle('closed', closed);
          r.kids.forEach(k => { k.tr.hidden = hide || closed || !(matchRow(k) || (filter === 'all' && textMatch(r))); });
        } else if (!r.parent) r.tr.hidden = !matchRow(r);
      }
      tbody.querySelectorAll('tr.rl-group').forEach(gr => {
        let n = gr.nextElementSibling, any = false;
        while (n && !n.classList.contains('rl-group')) { if (!n.hidden) any = true; n = n.nextElementSibling; }
        gr.hidden = !any;
      });
      noMatch.hidden = [...rows.values()].some(r => !r.tr.hidden);
    }

    // ---- suggestions: read from the role facts already extracted, drafted for undecided items only ----
    async function suggest(btn) {
      btn.disabled = true;
      try {
        const r = await api('GET', '/access/suggestions?applicationId=' + encodeURIComponent(ctx.appId));
        let applied = 0;
        const pageSug = r.pages || {};
        for (const row of rows.values()) {
          if (row.kind !== 'page' || eff(row).state !== 'und') continue;
          const s = pageSug[row.id]; if (!s) continue;
          suggested.set(row.key, s.notes || []);
          setEff(row, s.everyone ? { state: 'all', ids: new Set() } : { state: 'aud', ids: new Set(s.audienceIds) });
          applied++;
        }
        for (const row of rows.values()) {
          if (row.kind !== 'module' || eff(row).state !== 'und') continue;
          const effs = row.kids.filter(k => suggested.has(k.key)).map(eff);
          if (!effs.length) continue;
          suggested.set(row.key, ['Opened for everyone suggested on any of its pages']);
          if (effs.some(e => e.state === 'all')) setEff(row, { state: 'all', ids: new Set() });
          else setEff(row, { state: 'aud', ids: new Set(effs.flatMap(e => [...e.ids])) });
          applied++;
        }
        afterChange();
        if (applied) toast(`Suggested ${applied} item${applied === 1 ? '' : 's'} from the document. Check them, then press Save changes.`, 'ok');
        else toast('The document does not say enough about roles to suggest anything. Tick the boxes yourself.', 'info');
        const unmatched = (r.unmatchedRoles || []).slice(0, 5);
        if (unmatched.length) toast(`Roles in the document with no matching audience: ${unmatched.join(', ')}.`, 'info');
      } catch (e) { toast(e.message, 'bad'); }
      btn.disabled = false;
    }

    const previewPick = h('select', { class: 'sel-sm', 'aria-label': 'What can an audience see?', onchange: e => { const a = auds.find(x => x.id === e.target.value); e.target.value = ''; if (a) previewDialog(a); } },
      h('option', { value: '' }, 'What can they see?'), auds.map(a => h('option', { value: a.id }, a.name)));

    // Rules only apply to a key or share link that has an audience. Say plainly which ones do not.
    const openLinks = ov?.openLinks || [], openKeys = ov?.openKeys || [];
    const names = l => l.slice(0, 3).map(n => `"${n}"`).join(', ') + (l.length > 3 ? ` and ${l.length - 3} more` : '');
    const openNote = (openLinks.length || openKeys.length) ? h('div', { class: 'notice warn rl-open', role: 'note' },
      icon('shield', 16),
      h('div', null,
        h('strong', null, 'These rules do not apply to everything yet.'),
        h('p', null, 'A share link or key follows the rules only when it has an audience. Still with full access (they answer from every module): ',
          openLinks.length ? `share ${openLinks.length === 1 ? 'link' : 'links'} ${names(openLinks)}` : '',
          openLinks.length && openKeys.length ? ' and ' : '',
          openKeys.length ? `${openKeys.length === 1 ? 'key' : 'keys'} ${names(openKeys)}` : '', '.'),
        h('div', { class: 'actions' },
          openLinks.length ? h('a', { class: 'btn btn-sm', href: '#/share' }, 'Choose an audience for the links') : null,
          openKeys.length ? h('a', { class: 'btn btn-sm', href: '#/access' }, 'Choose an audience for the keys') : null))) : null;

    queueMicrotask(() => { applyFilter(); refreshBulk(); });
    return h('section', { class: 'rl-sheet' },
      openNote,
      h('div', { class: 'rl-toolbar' },
        h('label', { class: 'searchbox' }, icon('search', 16), search), filters,
        h('div', { class: 'rl-toolbar-r' },
          h('button', { class: 'btn btn-sm', type: 'button', title: 'Reads the role facts already extracted from your documents. Nothing is saved until you press Save changes.', onclick: e => suggest(e.currentTarget) }, icon('sparkle', 14), 'Suggest from document'),
          previewPick)),
      h('div', { class: 'rl-scroll' }, h('table', { class: 'rl-grid' },
        h('thead', null, h('tr', null,
          h('th', { class: 'rl-name rl-h-name', scope: 'col' }, 'Part of the document'),
          h('th', { class: 'rl-cell rl-h', scope: 'col' }, 'Everyone'),
          ...auds.map(a => h('th', { class: 'rl-cell rl-h', scope: 'col', title: a.name }, h('span', { class: 'rl-h-t' }, a.name))),
          h('th', { class: 'rl-cell rl-h rl-h-st', scope: 'col' }, ''))),
        tbody), noMatch),
      h('p', { class: 'rl-foot' }, 'Not decided = only full-access keys see it. Admins only = no group ticked. Ticking a module ticks its pages too; you can untick single pages.'));
  }

  function afterChange() { updateBar(); }

  // ---- audiences: a dialog, not a page --------------------------------------------------------------------------------------
  function modal(title, content, actions) {
    const dlg = h('dialog', { class: 'dialog rl-dialog', 'aria-label': title },
      h('div', { class: 'rl-dlg-h' }, h('h3', null, title), h('button', { class: 'icon-btn', type: 'button', 'aria-label': 'Close', onclick: () => dlg.close() }, icon('close', 18))),
      content, actions ? h('div', { class: 'actions' }, actions(dlg)) : null);
    dlg.addEventListener('close', () => dlg.remove());
    closeOnOutside(dlg);
    document.body.append(dlg);
    dlg.showModal();
    return dlg;
  }

  function openAudiences() {
    const intro = h('p', { class: 'rl-intro' }, 'An audience is a group of people who should see the same parts of your documents, like Manager or Nurse. Give a key or a share link an audience to limit what it can see.');
    const listHost = h('div'), suggestHost = h('div');

    // ---- create your own: always visible, so it never has to be found ----
    const nameIn = h('input', { type: 'text', id: 'au-new', maxlength: 80, autocomplete: 'off', placeholder: 'Type a name, for example Lab technician', 'aria-label': 'Name of the new audience' });
    const aliasIn = h('input', { type: 'text', id: 'au-new-al', maxlength: 300, autocomplete: 'off', placeholder: 'sister, charge nurse', 'aria-label': 'Other words your documents use for them' });
    const err = h('p', { class: 'error-text', role: 'alert' });
    async function addNew(e) {
      if (e) e.preventDefault();
      err.textContent = '';
      const name = nameIn.value.trim();
      if (name.length < 2) { err.textContent = 'Type a name of at least 2 letters.'; nameIn.focus(); return; }
      try {
        await api('POST', '/access/audiences', { body: { name, aliases: aliasIn.value.trim(), description: '' } });
        toast(`${name} added.`, 'ok');
        nameIn.value = ''; aliasIn.value = '';
        await refreshAudiences(true); paintAud(); nameIn.focus();
      } catch (ex) { err.textContent = ex.message; }
    }
    const createForm = h('form', { class: 'rl-create', onsubmit: addNew },
      h('h4', null, 'Add a new audience'),
      h('div', { class: 'rl-create-row' }, nameIn, h('button', { class: 'btn btn-primary', type: 'submit' }, icon('plus', 15), 'Add')),
      h('details', { class: 'rl-more' }, h('summary', null, 'Other words your documents use for this group (optional)'), aliasIn,
        h('small', { class: 'muted' }, 'Comma separated. Only used to suggest rules from the roles written in your documents.')),
      err);

    const dlg = modal('Audiences', h('div', { class: 'rl-audlist' }, intro, createForm, listHost, suggestHost));
    const count = (n, word) => `${n} ${word}${n === 1 ? '' : 's'}`;
    const paintAud = () => {
      const have = new Set(auds.map(a => a.name.toLowerCase()));
      const fromDocs = found.filter(r => !have.has(r.role.toLowerCase()));
      const offered = new Set(fromDocs.map(r => r.role.toLowerCase()));
      const starters = STARTERS.filter(([n]) => !have.has(n.toLowerCase()) && !offered.has(n.toLowerCase()));
      fill(listHost,
        h('h4', null, `Your audiences (${auds.length})`),
        auds.length ? h('ul', { class: 'rl-alist' }, auds.map(a => h('li', null,
          h('div', { class: 'rl-a-t' }, h('strong', null, a.name),
            h('span', null, `${count(a.keys, 'key')}, ${count(a.links, 'share link')}`),
            a.aliases ? h('span', { class: 'rl-a-al' }, 'Also matches: ' + a.aliases) : null,
            h('span', { class: 'rl-a-al' }, a.canBrowseKnowledge ? 'Chat and Knowledge page' : 'Chat only')),
          h('div', { class: 'rl-a-b' },
            h('button', { class: 'btn btn-sm', type: 'button', onclick: () => previewDialog(a) }, 'What can they see?'),
            h('button', { class: 'btn btn-sm', type: 'button', onclick: () => audienceDialog(a, paintAud) }, 'Edit'),
            h('button', { class: 'btn btn-sm btn-danger', type: 'button', onclick: () => removeAudience(a, paintAud) }, 'Delete'))))) : h('p', { class: 'muted' }, 'None yet. Add one above, or pick a suggestion below.'));
      fill(suggestHost, (fromDocs.length || starters.length) ? h('div', { class: 'rl-qrow' },
        h('h4', null, 'Suggestions'),
        fromDocs.length ? h('div', { class: 'rl-sg' }, h('span', null, 'Mentioned in your documents'), h('div', { class: 'chips' }, fromDocs.map(r => sugChip(r.role, '')))) : null,
        starters.length ? h('div', { class: 'rl-sg' }, h('span', null, 'Common groups'), h('div', { class: 'chips' }, starters.map(([n, al]) => sugChip(n, al)))) : null) : null);
    };
    const sugChip = (n, al) => h('button', { type: 'button', class: 'chip chip-add', onclick: async () => { await createAudience({ name: n, aliases: al, description: '' }, true); paintAud(); } }, icon('plus', 13), n);
    paintAud();
    dlg.addEventListener('close', () => { if (alive) paint(); });
    nameIn.focus();
  }

  async function createAudience(a, quiet) {
    try { await api('POST', '/access/audiences', { body: a }); toast(`${a.name} added.`, 'ok'); await refreshAudiences(quiet); }
    catch (e) { toast(e.message, 'bad'); }
  }
  // While the dialog is open only the lists refresh; the page behind it is repainted when the dialog closes.
  async function refreshAudiences(quiet) {
    if (quiet) { const [a, f] = await Promise.all([api('GET', '/access/audiences'), api('GET', '/access/roles-found').catch(() => [])]); auds = a; found = f; return; }
    await reload();
  }

  async function removeAudience(a, after) {
    if (!await confirmDialog(`Delete "${a.name}"?${a.decisions ? ` Its ${a.decisions} tick${a.decisions === 1 ? '' : 's'} in the rules are removed.` : ''}`, 'Delete')) return;
    try {
      await api('DELETE', '/access/audiences/' + a.id); toast('Audience deleted.', 'ok');
      for (const e of draft.values()) e.ids.delete(a.id);   // an unsaved tick for it must not make the next save fail
      await refreshAudiences(true); after && after();
    }
    catch (e) { toast(e.message, 'bad'); }
  }

  function audienceDialog(existing, after) {
    const name = h('input', { type: 'text', id: 'au-name', maxlength: 80, required: true, value: existing?.name || '', placeholder: 'e.g. Nurse' });
    const aliases = h('input', { type: 'text', id: 'au-al', maxlength: 300, value: existing?.aliases || '', placeholder: 'e.g. sister, charge nurse, staff nurse' });
    const browse = h('input', { type: 'checkbox', id: 'au-browse', checked: !!existing?.canBrowseKnowledge });
    const err = h('p', { class: 'error-text', role: 'alert' });
    modal(existing ? 'Edit audience' : 'New audience', h('div', { class: 'rl-form' },
      h('div', { class: 'field' }, h('label', { for: 'au-name' }, 'Name'), name),
      h('div', { class: 'field' }, h('label', { for: 'au-al' }, 'Other words your documents use for them (optional)'), aliases,
        h('small', { class: 'muted' }, 'Comma separated. Only used to suggest rules from the roles in your documents.')),
      h('div', { class: 'field rl-check' }, h('label', { for: 'au-browse' }, browse, ' Also let them open the Knowledge page'),
        h('small', { class: 'muted' }, 'Off (recommended): people who sign in with this audience\'s key get the chat only. On: they can also browse the modules and pages you ticked for them. Share links are always chat only.')), err),
    dlg => [
      h('button', { class: 'btn', type: 'button', onclick: () => dlg.close() }, 'Cancel'),
      h('button', { class: 'btn btn-primary', type: 'button', onclick: async () => {
        err.textContent = '';
        if (name.value.trim().length < 2) { err.textContent = 'Give the audience a name.'; return; }
        try {
          const payload = { name: name.value.trim(), aliases: aliases.value.trim(), description: existing?.description || '', canBrowseKnowledge: browse.checked };
          if (existing) await api('PUT', '/access/audiences/' + existing.id, { body: payload }); else await api('POST', '/access/audiences', { body: payload });
          dlg.close(); toast(existing ? 'Saved.' : 'Audience added.', 'ok');
          if (after) { await refreshAudiences(true); after(); } else await reload();
        } catch (e) { err.textContent = e.message; }
      } }, 'Save')]);
    name.focus();
  }

  async function previewDialog(a) {
    if (!ctx.appId) { toast('Create an application first.', 'info'); return; }
    const host = h('div', { class: 'rl-preview' }, h('div', { class: 'skel-set' }, h('i'), h('i')));
    modal(`What "${a.name}" can see`, host);
    try {
      const p = await api('GET', `/access/preview?audienceId=${encodeURIComponent(a.id)}&applicationId=${encodeURIComponent(ctx.appId)}`);
      const stat = (n, of, label) => h('div', { class: 'rl-stat' }, h('b', null, `${n}`), h('span', null, `of ${of} ${label}`));
      fill(host,
        h('div', { class: 'rl-stats' }, stat(p.openPages, p.totalPages, 'pages'), stat(p.openDocumentPages, p.totalDocumentPages, 'document pages'), stat(p.openWorkflows, p.totalWorkflows, 'workflows')),
        p.modules.length ? h('div', { class: 'rl-pv-list' }, p.modules.map(m => h('div', { class: 'rl-pv-mod' }, h('strong', null, m.name),
          m.pages.length ? h('ul', null, m.pages.map(n => h('li', null, n))) : h('p', { class: 'muted' }, 'The module itself, none of its pages.'))))
          : h('div', { class: 'notice warn' }, `${a.name} cannot see anything yet. Tick the modules and pages for them.`),
        h('p', { class: 'muted rl-pv-note' }, 'Computed with exactly the logic chat, search and the knowledge pages use for keys and share links with this audience.'));
    } catch (e) { fill(host, h('div', { class: 'notice warn' }, e.message)); }
  }

  // ---- save bar -------------------------------------------------------------------------------------------------------------
  function updateBar() {
    const n = draft.size;
    saveBar.hidden = n === 0;
    window.onbeforeunload = n ? e => { e.preventDefault(); e.returnValue = ''; } : null;
    if (!n) return;
    fill(saveBar,
      h('span', { class: 'rl-sb-t' }, icon('alert', 16), `${n} unsaved change${n === 1 ? '' : 's'}`, h('small', null, 'Nothing changes for your users until you save.')),
      h('div', { class: 'rl-sb-a' },
        h('button', { class: 'btn', type: 'button', onclick: discard }, 'Discard'),
        h('button', { class: 'btn btn-primary', type: 'button', onclick: save }, icon('check', 15), 'Save changes')));
  }
  function discard() { draft.clear(); suggested.clear(); paint(); }
  async function save() {
    const changes = [...draft.values()].map(e => ({ kind: e.kind, targetId: e.id, mode: e.state === 'und' ? 'clear' : e.state === 'all' ? 'everyone' : 'audiences', audienceIds: e.state === 'aud' ? [...e.ids] : [] }));
    if (!changes.length) return;
    try {
      const res = await api('PUT', '/access/rules', { body: { changes } });
      toast(`Saved ${res.saved} change${res.saved === 1 ? '' : 's'}. Keys and links follow the new rules within about 15 seconds.`, 'ok');
      const y = window.scrollY, sc0 = document.querySelector('.rl-scroll')?.scrollTop || 0;
      await reload();
      window.scrollTo(0, y); const sc = document.querySelector('.rl-scroll'); if (sc) sc.scrollTop = sc0;
    } catch (e) { toast(e.message, 'bad'); }
  }

  main.append(body, saveBar);
  fill(body, h('div', { class: 'skel-set' }, h('i'), h('i'), h('i')));
  await reload();
}

