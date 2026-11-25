import { h, clear, fill, toast, formatDate, empty, badge, statusInfo, confirmDialog } from '../dom.js';
import { api } from '../api.js';
import { copyText, icon } from '../chatkit.js';

// Company admins: create chat-only share links. Each link is its own chatbot: its own name, documents, look and behaviour.
// The editor shows a live preview of the real visitor page, so every change is visible before saving.
const linkUrl = t => `${location.origin}/bot.html#t=${t}`;
const DEFAULT_ACCENT = '#3752d6';
const DEFAULT_THEME = {
  mode: 'auto', background: 'solid', bg1: null, bg2: null, font: 'system', fontSize: 'md', radius: 'round', bubble: 'solid', width: 'standard',
  header: 'plain', avatar: 'sparkle', avatarText: '', placeholder: '', footer: '', showFooter: true, showThemeToggle: true, showStatus: true,
  typingEffect: true, showCopy: true, showSupport: true, historyEnabled: true, historyDays: 30,
  defaultLanguage: 'auto', showLanguageSwitch: true, titleTa: '', welcomeTa: '', startersTa: [], placeholderTa: '', footerTa: '',
};
const CHOICES = {
  mode: [['Auto', 'auto'], ['Light', 'light'], ['Dark', 'dark']],
  background: [['Plain', 'solid'], ['Gradient', 'gradient'], ['Dots', 'dots'], ['Grid', 'grid'], ['Glow', 'aurora']],
  font: [['Modern', 'system'], ['Serif', 'serif'], ['Rounded', 'rounded'], ['Mono', 'mono']],
  fontSize: [['Small', 'sm'], ['Medium', 'md'], ['Large', 'lg']],
  radius: [['Sharp', 'sharp'], ['Soft', 'soft'], ['Round', 'round']],
  bubble: [['Solid', 'solid'], ['Tinted', 'soft'], ['Outline', 'outline']],
  width: [['Narrow', 'narrow'], ['Standard', 'standard'], ['Wide', 'wide']],
  header: [['Plain', 'plain'], ['Accent', 'accent'], ['Glass', 'glass']],
  defaultLanguage: [['Automatic', 'auto'], ['English', 'en'], ['தமிழ்', 'ta']],
  avatar: [['Sparkle', 'sparkle'], ['Chat', 'chat'], ['Book', 'book'], ['Bolt', 'bolt'], ['Person', 'user'], ['Info', 'info']],
};
const PRESETS = [
  ['Clean', DEFAULT_ACCENT, {}],
  ['Ocean', '#0ea5e9', { background: 'gradient', bg1: '#e0f2fe', bg2: '#bae6fd', header: 'glass' }],
  ['Sunset', '#f97316', { background: 'gradient', bg1: '#fff1e6', bg2: '#ffd6e0', header: 'accent', radius: 'soft' }],
  ['Forest', '#16a34a', { background: 'dots', bg1: '#f0fdf4', font: 'rounded', bubble: 'soft' }],
  ['Midnight', '#8b5cf6', { mode: 'dark', background: 'aurora', bg1: '#0b1020', bg2: '#4c1d95', header: 'glass' }],
  ['Paper', '#92400e', { background: 'solid', bg1: '#faf6ef', font: 'serif', radius: 'sharp', bubble: 'outline' }],
];

export async function shareView(main, ctx) {
  const listHost = h('div');
  const formHost = h('section', { class: 'card editor-card' });
  let apps = [];
  try { apps = await api('GET', '/applications'); } catch { /* the form explains */ }
  let audiences = [];
  let usageById = new Map();
  try { audiences = await api('GET', '/access/audiences'); } catch { /* links still work without audiences */ }
  let editing = null;                    // the link being edited, or null when creating
  let design = { ...DEFAULT_THEME };     // the look being edited
  let accent = DEFAULT_ACCENT;
  let tab = 'basics';
  let pvView = 'welcome', pvDevice = 'phone', pvReady = false;

  const f = {
    name: h('input', { type: 'text', id: 'bname', maxlength: 200, required: true, placeholder: 'e.g. Billing team, Website widget, Priya' }),
    title: h('input', { type: 'text', id: 'btitle', maxlength: 120, required: true, placeholder: 'Shown to visitors, e.g. Billing Help', oninput: () => pushPreview() }),
    welcome: h('input', { type: 'text', id: 'bwelcome', maxlength: 500, placeholder: 'Optional greeting under the title', oninput: () => pushPreview() }),
    starters: h('textarea', { id: 'bstart', rows: 3, placeholder: 'Optional starter questions, one per line (up to 4)', oninput: () => pushPreview() }),
    limit: h('input', { type: 'number', id: 'blimit', min: 1, max: 100000, value: 300 }),
    app: h('select', { id: 'bapp', onchange: () => loadDocs() }, apps.map(a => h('option', { value: a.id, selected: a.id === ctx.appId }, a.name))),
    audience: h('select', { id: 'baud' }, h('option', { value: '' }, 'No limit: everything this link is mapped to'), audiences.map(a => h('option', { value: a.id }, a.name))),
  };
  const err = h('p', { class: 'error-text', role: 'alert' });
  const docsBox = h('div', { class: 'checklist' });
  const allBox = h('input', { type: 'checkbox', id: 'ball', checked: true, onchange: () => syncAll() });
  let chosen = new Set();

  // ---- live preview: the real visitor page in a frame, fed the unsaved choices ----
  const frame = h('iframe', { class: 'pv-frame', src: '/bot.html#preview', title: 'Live preview of the chat page', loading: 'eager' });
  const onMsg = e => { if (e.origin === location.origin && e.data && e.data.type === 'flowmind-preview-ready' && e.source === frame.contentWindow) markReady(); };
  window.addEventListener('message', onMsg);
  let queued = false;
  function pushPreview() {
    if (!pvReady || queued) return;
    queued = true;
    requestAnimationFrame(() => {
      queued = false;
      frame.contentWindow?.postMessage({
        type: 'flowmind-preview', view: pvView,
        info: {
          title: f.title.value.trim() || 'Assistant', welcome: f.welcome.value.trim() || null, accentColor: accent,
          starters: f.starters.value.split('\n').map(s => s.trim()).filter(Boolean).slice(0, 4), theme: { ...design },
        },
      }, location.origin);
    });
  }
  const setDesign = (patch) => { Object.assign(design, patch); pushPreview(); };

  // ---- small form controls ----
  function seg(key) {
    const wrap = h('div', { class: 'choice', role: 'radiogroup' });
    const paint = () => wrap.querySelectorAll('button').forEach(b => b.setAttribute('aria-checked', String(b.dataset.v === design[key])));
    CHOICES[key].forEach(([label, v]) => wrap.append(h('button', { type: 'button', role: 'radio', 'data-v': v, onclick: () => { setDesign({ [key]: v }); paint(); } }, label)));
    paint(); wrap.repaint = paint; return wrap;
  }
  function toggle(key, label, hint) {
    const box = h('input', { type: 'checkbox', checked: !!design[key], onchange: e => setDesign({ [key]: e.target.checked }) });
    box.repaint = () => { box.checked = !!design[key]; };
    return h('label', { class: 'switch' }, box, h('span', null, label, hint ? h('small', null, hint) : null));
  }
  function colorPick(key, label) {
    const input = h('input', { type: 'color', value: design[key] || '#ffffff', 'aria-label': label });
    const on = h('input', { type: 'checkbox', checked: !!design[key], 'aria-label': `Use a custom ${label.toLowerCase()}` });
    const sync = () => { input.disabled = !on.checked; };
    on.addEventListener('change', () => { sync(); setDesign({ [key]: on.checked ? input.value : null }); });
    input.addEventListener('input', () => setDesign({ [key]: input.value }));
    sync();
    const el = h('div', { class: 'colorpick' }, h('span', null, label), on, input);
    el.repaint = () => { on.checked = !!design[key]; input.value = design[key] || '#ffffff'; sync(); };
    return el;
  }
  const accentInput = h('input', { type: 'color', id: 'baccent', value: DEFAULT_ACCENT, oninput: e => { accent = e.target.value; pushPreview(); } });
  const text = (key, label, max, ph) => h('div', { class: 'field' }, h('label', null, label),
    h('input', { type: 'text', maxlength: max, placeholder: ph || '', value: design[key] || '', oninput: e => setDesign({ [key]: e.target.value }) }));
  const field = (label, control, hint) => h('div', { class: 'field' }, h('label', null, label), control, hint ? h('small', { class: 'muted' }, hint) : null);

  let widgets = [];
  const keep = w => { widgets.push(w); return w; };

  // ---- which documents may this link answer from ----
  let allDocs = [];
  const docSearch = h('input', { type: 'search', class: 'search', placeholder: 'Search documents', 'aria-label': 'Search documents', oninput: () => paintDocs() });
  const docCount = h('span', { class: 'pick-count' });
  const modeHint = h('p', { class: 'muted pick-hint' });
  const selectAll = () => { allDocs.filter(d => d.status === 'Approved' && matches(d)).forEach(d => chosen.add(d.id)); paintDocs(); };
  const pickPanel = h('div', { class: 'pickpanel' },
    h('div', { class: 'pick-bar' }, h('label', { class: 'searchbox' }, icon('search', 16), docSearch),
      h('button', { class: 'btn btn-sm', type: 'button', onclick: selectAll }, 'Select all'),
      h('button', { class: 'btn btn-sm', type: 'button', onclick: () => { chosen.clear(); paintDocs(); } }, 'Clear')),
    docCount, docsBox);
  const modeBtns = [['All approved documents', true], ['Only the documents I choose', false]].map(([label, all]) => {
    const b = h('button', { type: 'button', role: 'radio', onclick: () => { allBox.checked = all; syncAll(); } }, label); b.dataset.all = String(all); return b;
  });
  const matches = d => { const q = docSearch.value.trim().toLowerCase(); return !q || String(d.fileName).toLowerCase().includes(q); };
  function syncAll() {
    modeBtns.forEach(b => b.setAttribute('aria-checked', String((b.dataset.all === 'true') === allBox.checked)));
    pickPanel.hidden = allBox.checked;
    modeHint.textContent = allBox.checked
      ? 'This link answers from every approved document, including ones you approve later.'
      : 'This link answers only from the documents you tick. Documents approved later are not added automatically.';
  }
  function countDocs() {
    const approved = allDocs.filter(d => d.status === 'Approved');
    docCount.textContent = `${approved.filter(d => chosen.has(d.id)).length} of ${approved.length} approved documents selected`;
  }
  function paintDocs() {
    const approved = allDocs.filter(d => d.status === 'Approved'), waiting = allDocs.filter(d => d.status !== 'Approved' && d.status !== 'Deleted');
    clear(docsBox);
    if (!approved.length) docsBox.append(h('span', { class: 'muted' }, 'No approved documents yet. Approve a document first, then it appears here.'));
    approved.filter(matches).forEach(d => docsBox.append(h('label', { class: 'docpick' },
      h('input', { type: 'checkbox', value: d.id, checked: chosen.has(d.id), onchange: e => { e.target.checked ? chosen.add(d.id) : chosen.delete(d.id); countDocs(); } }),
      h('span', null, h('strong', null, d.fileName), h('small', null, 'Added ' + formatDate(d.createdUtc))))));
    if (waiting.length) {
      docsBox.append(h('div', { class: 'pick-sep' }, 'Not approved yet: these cannot answer questions until approved'));
      waiting.filter(matches).forEach(d => docsBox.append(h('label', { class: 'docpick off' }, h('input', { type: 'checkbox', disabled: true }),
        h('span', null, h('strong', null, d.fileName), badge(statusInfo(d.status).label, statusInfo(d.status).tone)))));
    }
    countDocs();
  }
  async function loadDocs() {
    const appId = editing ? editing.applicationId : (apps.length ? f.app.value : ctx.appId);
    try { allDocs = await api('GET', '/documents?applicationId=' + encodeURIComponent(appId)); } catch { allDocs = []; }
    paintDocs();
    syncAll();
  }

  function resetForm() {
    editing = null; chosen = new Set(); allBox.checked = true; design = { ...DEFAULT_THEME }; accent = DEFAULT_ACCENT; tab = 'basics';
    f.name.value = ''; f.title.value = ''; f.welcome.value = ''; f.starters.value = ''; f.limit.value = 300; accentInput.value = DEFAULT_ACCENT; f.audience.value = '';
    paintForm();
  }
  function startEdit(l) {
    editing = l; chosen = new Set(l.documentIds); allBox.checked = l.documentIds.length === 0; tab = 'basics';
    design = { ...DEFAULT_THEME, ...(l.theme || {}) }; accent = l.accentColor || DEFAULT_ACCENT;
    f.name.value = l.name; f.title.value = l.title; f.welcome.value = l.welcome || ''; f.starters.value = (l.starters || []).join('\n');
    f.limit.value = l.dailyMessageLimit; accentInput.value = accent; f.audience.value = l.audienceId || '';
    paintForm(); formHost.scrollIntoView({ behavior: 'smooth', block: 'start' });
  }
  function applyPreset([, color, patch]) {
    design = { ...DEFAULT_THEME, ...patch, historyEnabled: design.historyEnabled, historyDays: design.historyDays, placeholder: design.placeholder, footer: design.footer, avatarText: design.avatarText };
    accent = color; accentInput.value = color;
    widgets.forEach(w => w.repaint && w.repaint()); pushPreview();
  }

  function panels() {
    widgets = [];
    const pm = { // one panel per tab
      basics: [
        h('div', { class: 'formgrid' },
          h('div', { class: 'field' }, h('label', { for: 'bname' }, 'Who or what is it for? (internal)'), f.name),
          h('div', { class: 'field' }, h('label', { for: 'btitle' }, 'Assistant name (visitors see this)'), f.title),
          !editing && apps.length > 1 ? h('div', { class: 'field' }, h('label', { for: 'bapp' }, 'Application'), f.app) : null,
          h('div', { class: 'field' }, h('label', { for: 'blimit' }, 'Most questions per day'), f.limit),
          h('div', { class: 'field' }, h('label', { for: 'baud' }, 'Who is this link for?'), f.audience,
            h('small', { class: 'muted' }, audiences.length
              ? 'Pick an audience to answer only from the pages that audience may see (set on the Access rules page).'
              : 'No audiences yet. Create them on the Access rules page to limit what a link can answer.')),
          h('div', { class: 'field wide' }, h('label', { for: 'bwelcome' }, 'Welcome text'), f.welcome),
          h('div', { class: 'field wide' }, h('label', { for: 'bstart' }, 'Starter questions'), f.starters))],
      design: [
        h('div', { class: 'field' }, h('label', null, 'Start from a style'),
          h('div', { class: 'presets' }, PRESETS.map(p => h('button', { type: 'button', class: 'preset', onclick: () => applyPreset(p) },
            swatchDot(p[1]), p[0])))),
        h('div', { class: 'formgrid' },
          field('Accent colour', accentInput),
          field('Light or dark', keep(seg('mode')), 'The starting look. Visitors can still switch if you leave their toggle on.'),
          field('Page background', keep(seg('background'))),
          h('div', { class: 'field' }, h('label', null, 'Background colours'), h('div', { class: 'colors' }, keep(colorPick('bg1', 'Colour 1')), keep(colorPick('bg2', 'Colour 2')))),
          field('Font', keep(seg('font'))), field('Text size', keep(seg('fontSize'))),
          field('Corners', keep(seg('radius'))), field('Visitor messages', keep(seg('bubble'))),
          field('Header', keep(seg('header'))), field('Chat width', keep(seg('width'))),
          field('Avatar icon', keep(seg('avatar'))), text('avatarText', 'Or an emoji / initials instead', 4, 'e.g. 🩺 or AB'))],
      behaviour: [
        h('div', { class: 'formgrid' },
          text('placeholder', 'Message box hint', 80, 'Ask a question…'), text('footer', 'Small print under the box', 200, 'Answers come from approved documents only…')),
        h('div', { class: 'switches' },
          keep(toggle('showFooter', 'Show the small print')), keep(toggle('showThemeToggle', 'Let visitors switch light / dark')),
          keep(toggle('showStatus', 'Show "Online" under the name')), keep(toggle('typingEffect', 'Type answers out gradually')),
          keep(toggle('showCopy', 'Show a Copy button on answers')),
          keep(toggle('showSupport', 'Offer "Send this to our team" when it cannot answer', 'The question lands in Insights, under Support. Visitors can add a note and, if they want, how to reach them.')),
          keep(toggle('historyEnabled', 'Visitors keep their own chat history', 'Each visitor sees only their own chats on their own device and can delete them any time.'))),
        field('Delete chats automatically after (days)', h('input', { type: 'number', min: 1, max: 365, value: design.historyDays, oninput: e => { design.historyDays = Math.min(365, Math.max(1, Number(e.target.value) || 30)); } }),
          'Chats with no new message for this long are removed for good.')],
      lang: [
        h('p', { class: 'muted pick-hint' }, 'Visitors can ask in Tamil or English and the assistant answers in the language of each question, from documents written in either language. Here you choose how the page itself looks in each language.'),
        h('div', { class: 'formgrid' },
          field('Page opens in', keep(seg('defaultLanguage')), 'Automatic = Tamil for visitors whose browser is Tamil, English for everyone else. Visitors can switch any time.'),
          h('div', { class: 'field' }, keep(toggle('showLanguageSwitch', 'Show the English / தமிழ் switch'))),
          text('titleTa', 'Assistant name in Tamil', 120, 'Optional'), text('placeholderTa', 'Message box hint in Tamil', 80, 'உங்கள் கேள்வியை உள்ளிடுங்கள்…'),
          h('div', { class: 'field wide' }, h('label', null, 'Welcome text in Tamil'), h('input', { type: 'text', maxlength: 500, placeholder: 'Optional. Leave empty to show the English welcome text.', value: design.welcomeTa || '', oninput: e => setDesign({ welcomeTa: e.target.value }) })),
          h('div', { class: 'field wide' }, h('label', null, 'Starter questions in Tamil (one per line, up to 4)'), h('textarea', { rows: 3, placeholder: 'Optional', oninput: e => setDesign({ startersTa: e.target.value.split('\n').map(s => s.trim()).filter(Boolean).slice(0, 4) }) }, (design.startersTa || []).join('\n'))),
          h('div', { class: 'field wide' }, h('label', null, 'Small print in Tamil'), h('input', { type: 'text', maxlength: 200, placeholder: 'Optional', value: design.footerTa || '', oninput: e => setDesign({ footerTa: e.target.value }) })))],
      docs: [
        h('div', { class: 'field' }, h('label', null, 'Which documents can this link answer from?'),
          h('div', { class: 'choice', role: 'radiogroup' }, modeBtns), modeHint, pickPanel)],
    };
    return pm[tab];
  }
  function swatchDot(color) { const d = h('i', { class: 'preset-dot' }); d.style.background = color; return d; }

  // The layout (and the preview frame) is built once; refreshing only swaps the tab contents, so the preview never reloads.
  const headEl = h('h3'), subEl = h('p', { class: 'muted' });
  const tabBar = h('div', { class: 'tabs', role: 'tablist' });
  const panelEl = h('div', { class: 'tabpanel', role: 'tabpanel' });
  const cancelBtn = h('button', { class: 'btn', type: 'button', onclick: () => resetForm() }, 'Cancel');
  const saveBtn = h('button', { class: 'btn btn-primary', type: 'submit' });
  const pvMsg = h('div', { class: 'pv-msg', role: 'status' }, h('div', { class: 'spinner', 'aria-hidden': 'true' }), h('p', null, 'Loading preview…'),
    h('button', { class: 'btn btn-sm', type: 'button', onclick: () => reloadFrame() }, icon('refresh', 14), 'Reload preview'));
  const frameWrap = h('div', { class: 'pv-wrap pv-phone' }, frame, pvMsg);
  // The preview announces itself when ready (and the frame's load event is a second signal). If neither arrives, say so and offer a retry.
  let readyTimer = null;
  function markReady() { pvReady = true; pvMsg.hidden = true; clearTimeout(readyTimer); pushPreview(); }
  function reloadFrame() {
    pvReady = false; pvMsg.hidden = false; pvMsg.querySelector('p').textContent = 'Loading preview…';
    frame.src = '/bot.html?v=' + Date.now() + '#preview';
    clearTimeout(readyTimer);
    readyTimer = setTimeout(() => { if (!pvReady) pvMsg.querySelector('p').textContent = 'The preview is taking too long. Press Reload, or Ctrl+F5 to refresh the page.'; }, 4000);
  }
  frame.addEventListener('load', () => setTimeout(() => { if (!pvReady) markReady(); }, 150));
  readyTimer = setTimeout(() => { if (!pvReady) pvMsg.querySelector('p').textContent = 'The preview is taking too long. Press Reload, or Ctrl+F5 to refresh the page.'; }, 4000);
  ctx.onCleanup(() => { window.removeEventListener('message', onMsg); clearTimeout(readyTimer); });
  const pvButtons = [];
  const pvBtn = (label, key, value) => { const b = h('button', { type: 'button', onclick: () => { if (key === 'view') pvView = value; else pvDevice = value; paintPv(); pushPreview(); } }, label); b.dataset.key = key; b.dataset.value = value; pvButtons.push(b); return b; };
  function paintPv() {
    pvButtons.forEach(b => b.setAttribute('aria-pressed', String((b.dataset.key === 'view' ? pvView : pvDevice) === b.dataset.value)));
    frameWrap.className = 'pv-wrap ' + (pvDevice === 'phone' ? 'pv-phone' : 'pv-desktop');
  }
  const preview = h('aside', { class: 'preview' },
    h('div', { class: 'pv-bar' }, h('strong', null, 'Live preview'),
      h('span', { class: 'pv-tools' },
        h('span', { class: 'choice mini' }, pvBtn('Welcome', 'view', 'welcome'), pvBtn('Chat', 'view', 'chat')),
        h('span', { class: 'choice mini' }, pvBtn('Phone', 'device', 'phone'), pvBtn('Desktop', 'device', 'desktop')))),
    frameWrap, h('p', { class: 'muted pv-note' }, 'This is the real chat page. Type in it to try it. Nothing is saved until you press Save.'));
  fill(formHost, headEl, subEl, h('div', { class: 'editor' }, h('form', { onsubmit: submit }, tabBar, panelEl, err, h('div', { class: 'actions' }, cancelBtn, saveBtn)), preview));

  function paintForm() {
    f.name.disabled = !!editing;
    headEl.textContent = editing ? `Edit "${editing.name}"` : 'New share link';
    subEl.textContent = editing ? 'Changes apply to the same link straight away. The address does not change.' : 'Each link is its own chatbot: choose its name, look and which documents it may answer from.';
    cancelBtn.hidden = !editing; saveBtn.textContent = editing ? 'Save changes' : 'Create share link';
    paintTabs(); paintPv(); pushPreview();
  }
  function paintTabs() {
    const tabs = [['basics', 'Basics'], ['design', 'Design'], ['behaviour', 'Chat options'], ['lang', 'Languages'], ['docs', 'Documents']];
    fill(tabBar, tabs.map(([k, label]) => h('button', { type: 'button', role: 'tab', 'aria-selected': String(tab === k), onclick: () => { tab = k; paintTabs(); } }, label)));
    fill(panelEl, panels());
    syncAll(); if (tab === 'docs' || !allDocs.length) loadDocs();
  }
  async function submit(e) {
    e.preventDefault(); err.textContent = '';
    if (!f.title.value.trim() || (!editing && !f.name.value.trim())) { tab = 'basics'; paintTabs(); err.textContent = 'Give the link a name and an assistant name.'; return; }
    const body = {
      title: f.title.value.trim(), welcome: f.welcome.value.trim(),
      starters: f.starters.value.split('\n').map(s => s.trim()).filter(Boolean).slice(0, 4),
      dailyMessageLimit: Number(f.limit.value) || 300, accentColor: accent,
      documentIds: allBox.checked ? [] : [...chosen], theme: { ...design },
    };
    if (f.audience.value) body.audienceId = f.audience.value; else if (editing) body.noAudience = true;
    if (!allBox.checked && chosen.size === 0) { tab = 'docs'; paintTabs(); err.textContent = 'Choose at least one document, or switch to "All approved documents".'; return; }
    try {
      if (editing) { await api('PATCH', '/bot-links/' + editing.id, { body }); toast('Saved.', 'ok'); }
      else { await api('POST', '/bot-links', { body: { ...body, name: f.name.value.trim(), welcome: body.welcome || null, applicationId: apps.length ? f.app.value : null } }); toast('Link created.', 'ok'); }
      resetForm(); await load();
    } catch (ex) { err.textContent = ex.message; }
  }

  async function load() {
    let links;
    try { links = await api('GET', '/bot-links'); } catch (e) { fill(listHost, h('div', { class: 'notice warn' }, e.message)); return; }
    // What each link has used in the last 30 days (a failure here only hides the line, never the links).
    usageById = new Map();
    try { (await api('GET', '/bot-links/usage?days=30')).links.forEach(u => usageById.set(u.linkId, u)); } catch { /* optional */ }
    clear(listHost);
    if (!links.length) { listHost.append(empty('No share links yet', 'Create one above, then send the link to anyone.', 'share')); return; }
    links.forEach(l => listHost.append(card(l)));
  }

  function card(l) {
    const url = l.token ? linkUrl(l.token) : null;
    const dot = h('span', { class: 'swatch' }); dot.style.background = l.accentColor || DEFAULT_ACCENT;
    return h('div', { class: 'card linkcard' },
      h('h3', null, dot, l.name, l.active ? badge('Active', 'ok') : badge('Disabled', 'bad')),
      h('p', { class: 'muted' }, `${l.title} · ${l.application} · today ${l.todayMessages}/${l.dailyMessageLimit} questions · created ${formatDate(l.createdUtc)} · ${l.lastUsedUtc ? 'last used ' + formatDate(l.lastUsedUtc) : 'never used'}`),
      (() => { const u = usageById.get(l.id); return u ? h('p', { class: 'muted link-usage', title: 'Chat model tokens caused by this link\'s visitors. See AI usage for the full table.' }, icon('chart', 13), ' Last 30 days: ', h('strong', null, u.totalTokens.toLocaleString()), ` tokens (${u.aiCalls.toLocaleString()} AI answers, ${u.tokensPerCall.toLocaleString()} per answer) `, h('a', { href: '#/usage', class: 'link-usage-more' }, 'Details')) : null; })(),
      h('div', { class: 'chips' }, l.documentNames.length ? l.documentNames.map(n => h('span', { class: 'chip' }, n)) : h('span', { class: 'chip' }, 'All approved documents'),
        l.audienceName ? h('span', { class: 'chip chip-aud', title: 'This link answers only from what this audience may see' }, icon('shield', 12), l.audienceName)
          : audiences.length ? h('span', { class: 'chip chip-open', title: 'This link has no audience, so it answers from every module and your Access rules do not apply. Press Customize and choose an audience.' }, icon('shield', 12), 'Full access: no audience') : null),
      url
        ? h('div', { class: 'copyrow linkrow' }, h('div', { class: 'keybox' }, url),
          h('button', { class: 'btn btn-primary', type: 'button', onclick: async () => toast(await copyText(url) ? 'Link copied.' : 'Select the link and copy it.', 'ok') }, icon('copy', 15), 'Copy link'))
        : h('p', { class: 'muted' }, 'This link cannot be shown again (the server secret changed). Delete it and create a new one.'),
      h('div', { class: 'actions' },
        url ? h('a', { class: 'btn btn-sm', href: url, target: '_blank', rel: 'noopener noreferrer' }, 'Open') : null,
        h('button', { class: 'btn btn-sm', type: 'button', onclick: () => startEdit(l) }, 'Customize'),
        h('button', { class: 'btn btn-sm', type: 'button', onclick: () => toggleLink(l) }, l.active ? 'Disable' : 'Enable'),
        h('button', { class: 'btn btn-sm', type: 'button', disabled: !l.storedChats, title: 'Erase every visitor chat stored for this link', onclick: () => clearHistory(l) },
          l.storedChats ? `Clear chat history (${l.storedChats})` : 'No chat history'),
        h('button', { class: 'btn btn-sm btn-danger', type: 'button', onclick: () => remove(l) }, 'Delete')));
  }

  async function toggleLink(l) {
    try { await api('PATCH', '/bot-links/' + l.id, { body: { active: !l.active } }); toast(l.active ? 'Link disabled. It stops working now.' : 'Link enabled.', 'ok'); await load(); } catch (e) { toast(e.message, 'bad'); }
  }
  async function clearHistory(l) {
    if (!await confirmDialog(`Erase all ${l.storedChats} visitor chat(s) stored for "${l.name}"? The link keeps working. This cannot be undone.`, 'Erase chats')) return;
    try { const r = await api('DELETE', `/bot-links/${l.id}/conversations`); toast(`${r.erased} chat(s) erased.`, 'ok'); await load(); } catch (e) { toast(e.message, 'bad'); }
  }
  async function remove(l) {
    if (!await confirmDialog(`Delete "${l.name}"? The link stops working immediately and cannot be restored.`, 'Delete')) return;
    try { await api('DELETE', '/bot-links/' + l.id); toast('Link deleted.', 'ok'); await load(); } catch (e) { toast(e.message, 'bad'); }
  }

  main.append(
    h('div', { class: 'page-head' }, h('div', null, h('h2', null, 'Share links'),
      h('p', null, 'Send a link to anyone. They get only the chat: no sign-in, no admin pages, no document names. Make one per team or module, each with its own documents, look and behaviour.'))),
    formHost,
    h('div', { class: 'notice' }, 'Anyone who has a link can ask questions, and each question uses your free AI allowance. Share it only with people you trust, keep the daily limit modest, and use only non-confidential documents.'),
    listHost);
  paintForm();
  await load();
}
