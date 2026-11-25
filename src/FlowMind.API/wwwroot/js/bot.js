// The public chat page. It knows nothing about the admin app: no sign-in, no navigation, no other API.
// The secret in the link's #fragment (never sent to the server or written to logs) selects one assistant.
// The admin controls the look (theme), the Tamil texts, and whether visitors keep a private history of their own chats.
// The page speaks English or Tamil (visitor's choice); the server answers in the language each question is written in.
import { h, clear, fill, renderAnswer, confirmDialog } from './dom.js';
import { icon, copyText, typewrite, reducedMotion } from './chatkit.js';
import { themeToggle, setDefaultMode } from './theme.js';
import { t, lang, setLang, pickLanguage, failure, timeGreeting } from './i18n.js';

const root = document.getElementById('bot');
const params = new URLSearchParams(location.hash.replace(/^#/, ''));
const token = (params.get('t') || '').trim();
// "#preview" = shown inside the admin's design editor: no network, the admin's unsaved choices arrive by postMessage (same origin only).
const PREVIEW = params.has('preview') && window.parent !== window;

function visitorId() {
  try {
    let v = localStorage.getItem('flowmind.visitor');
    if (!v) { v = (crypto.randomUUID ? crypto.randomUUID() : String(Date.now()) + Math.random().toString(16).slice(2)).replace(/[^a-zA-Z0-9-]/g, ''); localStorage.setItem('flowmind.visitor', v); }
    return v;
  } catch { return 'v' + Math.random().toString(16).slice(2) + Date.now(); }
}
const VISITOR = visitorId();

async function call(method, path, body) {
  const res = await fetch('/api/bot' + path, {
    method,
    headers: { 'X-Bot-Token': token, 'X-Bot-Visitor': VISITOR, ...(body ? { 'Content-Type': 'application/json' } : {}) },
    body: body ? JSON.stringify(body) : undefined,
  });
  let data = null;
  try { data = await res.json(); } catch { /* no body */ }
  if (!res.ok) { const e = new Error(failure(res.status, data && (data.detail || data.title))); e.status = res.status; e.serverMessage = data && (data.detail || data.title); throw e; }
  return data;
}

const HEX = /^#[0-9a-f]{6}$/i;
const rgb = hex => { const n = parseInt(hex.slice(1), 16); return [n >> 16, (n >> 8) & 255, n & 255]; };

/** Accent colour chosen for this link: used for buttons and your bubbles, with readable text on top and a lighter shade in dark mode. */
function applyAccent(hex) {
  const s = document.documentElement.style;
  if (!HEX.test(hex || '')) { ['--brand', '--brand-ink', '--brand-tint'].forEach(p => s.removeProperty(p)); return; }
  const [r, g, b] = rgb(hex);
  const lum = (0.2126 * r + 0.7152 * g + 0.0722 * b) / 255;
  s.setProperty('--brand', hex);
  s.setProperty('--brand-ink', lum > 0.6 ? '#0b1020' : '#ffffff');
  s.setProperty('--brand-tint', `rgba(${r}, ${g}, ${b}, .14)`);
}

/**
 * Everything the admin can style. Only allow-listed names and #rrggbb colours reach this far (the server checks them; we check again),
 * and they only ever become data-attributes and CSS custom properties, never markup or free CSS.
 */
function applyTheme(info) {
  const th = info.theme || {};
  const el = document.documentElement, s = el.style;
  const pick = (v, ok, d) => (ok.includes(v) ? v : d);
  el.dataset.font = pick(th.font, ['system', 'serif', 'rounded', 'mono'], 'system');
  el.dataset.size = pick(th.fontSize, ['sm', 'md', 'lg'], 'md');
  el.dataset.radius = pick(th.radius, ['sharp', 'soft', 'round'], 'round');
  el.dataset.bubble = pick(th.bubble, ['solid', 'soft', 'outline'], 'solid');
  el.dataset.width = pick(th.width, ['narrow', 'standard', 'wide'], 'standard');
  el.dataset.header = pick(th.header, ['plain', 'accent', 'glass'], 'plain');
  applyAccent(info.accentColor);

  const style = pick(th.background, ['solid', 'gradient', 'dots', 'grid', 'aurora'], 'solid');
  const b1 = HEX.test(th.bg1 || '') ? th.bg1 : null, b2 = HEX.test(th.bg2 || '') ? th.bg2 : null;
  const accent = HEX.test(info.accentColor || '') ? info.accentColor : '#3752d6';
  let page = null;
  if (style === 'solid') page = b1;
  else if (style === 'gradient') page = `linear-gradient(160deg, ${b1 || '#eef2ff'}, ${b2 || '#e0e7ff'})`;
  else if (style === 'dots') page = `radial-gradient(rgba(127,127,127,.38) 1.4px, transparent 1.6px) 0 0 / 22px 22px, ${b1 || '#f5f6f8'}`;
  else if (style === 'grid') page = `linear-gradient(rgba(127,127,127,.18) 1px, transparent 1px) 0 0 / 28px 28px, linear-gradient(90deg, rgba(127,127,127,.18) 1px, transparent 1px) 0 0 / 28px 28px, ${b1 || '#f5f6f8'}`;
  else if (style === 'aurora') page = `radial-gradient(60% 50% at 12% 8%, ${b2 || accent}66, transparent 62%), radial-gradient(55% 50% at 92% 92%, ${accent}55, transparent 62%), ${b1 || '#f5f6f8'}`;
  if (page) { s.setProperty('--page-bg', page); el.dataset.customBg = '1'; } else { s.removeProperty('--page-bg'); delete el.dataset.customBg; }

  // Light/dark: the admin's default applies until the visitor picks their own; with the toggle hidden it is the only choice.
  setDefaultMode(th.mode, PREVIEW || th.showThemeToggle === false);
  const [r, g, b] = rgb(accent);
  const meta = document.querySelector('meta[name="theme-color"]'); if (meta) meta.setAttribute('content', '#' + [r, g, b].map(v => v.toString(16).padStart(2, '0')).join(''));
}

function unavailable(text) {
  document.body.append(h('div', { class: 'floattheme' }, themeToggle('ghost icon-only', () => ({ toLight: t('toLight'), toDark: t('toDark') }))));
  fill(root, h('main', { class: 'gone' }, icon('alert', 32), h('h1', null, t('unavailableTitle')), h('p', null, text)));
}

async function start() {
  setLang(pickLanguage('auto', PREVIEW));
  if (PREVIEW) return startPreview();
  if (token.length < 16) return unavailable(t('incomplete'));
  let info;
  try { info = await call('GET', '/info'); }
  catch (e) { return unavailable(e.status === 404 ? t('inactive') : t('tryLater')); }
  applyTheme(info);
  setLang(pickLanguage(info.theme && info.theme.defaultLanguage));    // visitor's own choice, then the admin's default, then the browser
  build(info);
}

function startPreview() {
  window.addEventListener('message', e => {
    if (e.origin !== location.origin || !e.data || e.data.type !== 'flowmind-preview') return;
    applyTheme(e.data.info);
    const d = e.data.info.theme && e.data.info.theme.defaultLanguage;
    if (d === 'en' || d === 'ta') setLang(d);
    build(e.data.info, { preview: true, view: e.data.view });
  });
  window.parent.postMessage({ type: 'flowmind-preview-ready' }, location.origin);
}

const asDate = iso => new Date(/[zZ]|[+-]\d\d:?\d\d$/.test(iso) ? iso : iso + 'Z'); // the server stores UTC
const when = iso => { try { return asDate(iso).toLocaleString(lang() === 'ta' ? 'ta-IN' : [], { dateStyle: 'medium', timeStyle: 'short' }); } catch { return ''; } };

const AVATARS = ['sparkle', 'chat', 'book', 'bolt', 'user', 'info'];

function build(info, { preview = false, view = 'welcome', carry = null } = {}) {
  const th = info.theme || {};
  const ta = lang() === 'ta';
  const pickText = (en, tamil) => (ta && tamil ? tamil : en);
  const title = pickText(info.title, th.titleTa);
  const historyOn = th.historyEnabled !== false;
  document.title = title;

  let conversationId = carry ? carry.conversationId : null;
  const transcript = carry ? carry.transcript : [];     // what is on screen, kept so switching language does not lose the chat
  let busy = false;
  let typing = null;
  let stick = true;

  const thread = h('div', { class: 'thread', role: 'log', 'aria-live': 'polite', 'aria-label': t('yourChats') });
  const toBottom = h('button', { class: 'to-bottom', type: 'button', hidden: true, 'aria-label': t('toLatest'), onclick: () => { stick = true; scrollEnd(true); } }, icon('down', 18));
  const input = h('textarea', { id: 'msg', rows: 1, maxlength: 2000, placeholder: pickText(th.placeholder, th.placeholderTa) || t('placeholder'), 'aria-label': t('yourQuestion'), autocomplete: 'off', enterkeyhint: 'send' });
  const send = h('button', { class: 'send', type: 'submit', 'aria-label': t('send') }, icon('send', 20));
  const noteText = pickText(th.footer, th.footerTa) || t('footer');
  const note = th.showFooter === false ? null : h('p', { class: 'note' }, noteText);

  const scroller = h('div', { class: 'scroller' }, thread);
  function scrollEnd(smooth) { scroller.scrollTo({ top: scroller.scrollHeight, behavior: smooth && !reducedMotion() ? 'smooth' : 'auto' }); }
  function follow() { if (stick) scrollEnd(false); }
  scroller.addEventListener('scroll', () => {
    const away = scroller.scrollHeight - scroller.scrollTop - scroller.clientHeight;
    stick = away < 80; toBottom.hidden = stick;
  });

  const avatarIcon = AVATARS.includes(th.avatar) ? th.avatar : 'sparkle';
  const avatar = () => h('div', { class: 'avatar', 'aria-hidden': 'true' }, th.avatarText ? th.avatarText : icon(avatarIcon, 16));

  function userRow(text) {
    return h('div', { class: 'brow user' }, h('div', { class: 'bubble' }, text));
  }
  /** "Send this to our team": under an answer the assistant could not give. Only the newest such answer keeps it (see ask()). */
  function supportButton(question) {
    const b = h('button', { class: 'ghost support-btn', type: 'button' }, icon('send', 14), h('span', null, t('askTeam')));
    b.addEventListener('click', () => openSupport(question, b));
    return b;
  }
  /** The question to offer for the last answer in a list of {role, content, documented}, or null when that answer was fine. */
  function supportQuestion(list) {
    const i = list.length - 1;
    return i > 0 && list[i].role === 'assistant' && list[i].documented === false && list[i - 1].role === 'user' ? list[i - 1].content : null;
  }
  function botRow(content, { animate = false, copy = null, error = false, support = null } = {}) {
    const bubble = h('div', { class: 'bubble' + (error ? ' error' : '') }, content);
    const actions = h('div', { class: 'actions pending' });
    if (support && th.showSupport !== false) actions.append(supportButton(support));
    if (copy && th.showCopy !== false) {
      const label = h('span', null, t('copy'));
      actions.append(h('button', { class: 'ghost', type: 'button', onclick: async () => {
        const ok = await copyText(copy); label.textContent = ok ? t('copied') : t('pressCtrlC'); setTimeout(() => { label.textContent = t('copy'); }, 1800);
      } }, icon('copy', 14), label));
    }
    const row = h('div', { class: 'brow bot' }, avatar(), h('div', { class: 'col' }, bubble, actions));
    if (animate && th.typingEffect !== false) {
      let done = false;
      const tw = typewrite(content, { onTick: follow, onDone: () => { done = true; actions.classList.remove('pending'); follow(); if (typing && typing.owner === row) typing = null; } });
      if (!done) { tw.owner = row; typing = tw; }
      bubble.addEventListener('click', () => tw.finish());
    } else actions.classList.remove('pending');
    return row;
  }
  const typingRow = () => h('div', { class: 'brow bot', role: 'status' }, avatar(),
    h('div', { class: 'col' }, h('div', { class: 'bubble typing' }, h('span', { class: 'dot' }), h('span', { class: 'dot' }), h('span', { class: 'dot' }), h('span', { class: 'sr-only' }, t('typing')))));

  const answerNode = (text, documented) => documented ? renderAnswer(text) : h('p', null, text || t('notFound'));

  async function ask(text) {
    text = text.trim();
    if (!text || busy) return;
    busy = true; send.disabled = true;
    if (typing) typing.finish();
    welcome.hidden = true;
    thread.querySelectorAll('.support-btn').forEach(b => b.remove());   // an older unanswered question can no longer be sent: the server uses the newest one
    thread.append(userRow(text)); stick = true; scrollEnd(false);
    transcript.push({ role: 'user', content: text });
    input.value = ''; autosize();
    const wait = typingRow(); thread.append(wait); scrollEnd(false);
    try {
      const r = preview
        ? await new Promise(res => setTimeout(() => res({ answer: ta ? 'இப்படித்தான் பதில் தோன்றும். படிகள், **தடித்த எழுத்து** மற்றும் பட்டியல்கள் நீங்கள் தேர்ந்த எழுத்துரு, நிறங்களில் காட்டப்படும்.' : 'This is how an answer will look. Steps, **bold text** and lists use your chosen font, colours and corners.', documented: true, conversationId: null }), 500))
        : await call('POST', '/ask', { message: text, conversationId, language: lang() });   // the answer is written in the language selected on the page
      conversationId = r.conversationId;
      transcript.push({ role: 'assistant', content: r.answer, documented: r.documented });
      wait.replaceWith(botRow(answerNode(r.answer, r.documented), { animate: true, copy: r.documented ? r.answer : null, support: r.documented ? null : text }));
    } catch (e) {
      transcript.pop();
      if (e.status === 404 && !/link|conversation|உரையாடல்/i.test(e.serverMessage || e.message)) { unavailable(t('inactive')); return; }
      if (e.status === 404) conversationId = null;
      wait.replaceWith(botRow(h('p', null, e.message), { error: true }));
      thread.lastChild.querySelector('.actions').append(h('button', { class: 'ghost', type: 'button', onclick: () => { thread.lastChild.remove(); thread.lastChild.remove(); busy = false; ask(text); } }, icon('refresh', 14), h('span', null, t('tryAgain'))));
    } finally { busy = false; send.disabled = false; if (!preview && matchMedia('(hover: hover)').matches) input.focus(); follow(); }
  }

  function autosize() { input.style.height = 'auto'; input.style.height = Math.min(input.scrollHeight, 160) + 'px'; }
  input.addEventListener('input', autosize);
  input.addEventListener('keydown', e => { if (e.key === 'Enter' && !e.shiftKey && !e.isComposing) { e.preventDefault(); ask(input.value); } });   // isComposing: Tamil IME input must not send half-typed words

  function newChat() {
    if (typing) typing.finish();
    conversationId = null; transcript.length = 0; clear(thread); welcome.hidden = false; stick = true; scroller.scrollTop = 0; input.value = ''; autosize();
  }

  /** Show a stored chat (no typing effect) and continue it from there. */
  function showChat(id, messages) {
    if (typing) typing.finish();
    conversationId = id; clear(thread); welcome.hidden = true; transcript.length = 0;
    for (const m of messages) transcript.push({ role: m.role, content: m.content, documented: m.documented });
    paintTranscript();
    stick = true; scrollEnd(false);
  }

  /** Draw the stored transcript (no typing effect). Only the last answer can offer "send to our team", and only if it was an unanswered one. */
  function paintTranscript() {
    const offer = supportQuestion(transcript);
    transcript.forEach((m, i) => thread.append(m.role === 'user' ? userRow(m.content)
      : botRow(answerNode(m.content, m.documented), { copy: m.documented ? m.content : null, support: i === transcript.length - 1 ? offer : null })));
  }

  // ---- "send this to our team": the server reads the question from this visitor's own chat, so only a note and a contact are sent ----
  function openSupport(question, button) {
    const note = h('textarea', { id: 'sup-note', rows: 3, maxlength: 500 });
    const contact = h('input', { id: 'sup-contact', type: 'text', maxlength: 120, autocomplete: 'off' });
    const err = h('p', { class: 'sup-err', role: 'alert' });
    const submit = h('button', { class: 'ghost sup-send', type: 'submit' }, icon('send', 14), h('span', null, t('supportSend')));
    const dlg = h('dialog', { class: 'sheet', 'aria-label': t('supportTitle') },
      h('form', { class: 'sup-form', method: 'dialog', onsubmit: async e => {
        e.preventDefault();
        submit.disabled = true; err.textContent = '';
        try {
          if (!preview) await call('POST', '/support', { conversationId, note: note.value, contact: contact.value });
          dlg.close();
          button.replaceWith(h('span', { class: 'support-done', role: 'status' }, icon('check', 14), h('span', null, t('supportSent'))));
        } catch (ex) { err.textContent = ex.message; submit.disabled = false; }
      } },
        h('div', { class: 'sheet-head' }, h('strong', null, t('supportTitle')),
          h('button', { class: 'ghost icon-only', type: 'button', 'aria-label': t('close'), onclick: () => dlg.close() }, icon('close', 16))),
        h('div', { class: 'sup-body' },
          h('p', null, t('supportIntro')), h('blockquote', { class: 'sup-q' }, question),
          h('label', { for: 'sup-note' }, t('supportNote')), note,
          h('label', { for: 'sup-contact' }, t('supportContact')), contact, h('small', null, t('supportContactHint')), err),
        h('div', { class: 'sup-actions' }, h('button', { class: 'ghost', type: 'button', onclick: () => dlg.close() }, t('cancel')), submit)));
    dlg.addEventListener('close', () => dlg.remove());
    dlg.addEventListener('click', e => { if (e.target === dlg) dlg.close(); });
    document.body.append(dlg); dlg.showModal(); note.focus();
  }

  // ---- history: the visitor's own earlier chats on this device ----
  async function openHistory() {
    const list = h('ul', { class: 'hist-list' });
    const err = h('p', { class: 'sheet-note', role: 'alert' });   // a failed delete says so instead of doing nothing
    const clearBtn = h('button', { class: 'ghost', type: 'button' }, icon('trash', 14), h('span', null, t('clearAll')));
    const dlg = h('dialog', { class: 'sheet', 'aria-label': t('history') },
      h('div', { class: 'sheet-head' }, h('strong', null, t('yourChats')), h('span', { class: 'sheet-tools' }, clearBtn,
        h('button', { class: 'ghost icon-only', type: 'button', 'aria-label': t('close'), onclick: () => dlg.close() }, icon('close', 16)))),
      list, err,
      h('p', { class: 'sheet-note' }, t('sheetNote', th.historyDays || 30)));
    dlg.addEventListener('close', () => dlg.remove());
    dlg.addEventListener('click', e => { if (e.target === dlg) dlg.close(); });
    document.body.append(dlg); dlg.showModal();

    const paint = items => {
      clearBtn.hidden = items.length === 0;
      if (!items.length) { fill(list, h('li', { class: 'hist-empty' }, t('noChats'))); return; }
      fill(list, ...items.map(c => h('li', { class: c.id === conversationId ? 'on' : '' },
        h('button', { class: 'hist-open', type: 'button', onclick: async () => {
          try { showChat(c.id, preview ? [] : await call('GET', '/conversations/' + c.id)); dlg.close(); } catch (e) { fill(list, h('li', { class: 'hist-empty' }, e.message)); }
        } }, h('strong', null, c.title || '…'), h('small', null, when(c.updatedUtc))),
        h('button', { class: 'ghost icon-only', type: 'button', 'aria-label': t('deleteChat'), title: t('deleteChat'), onclick: async () => {
          if (!preview) { try { await call('POST', '/conversations/' + c.id + '/delete'); } catch (e) { err.textContent = e.message; return; } }
          err.textContent = '';
          if (c.id === conversationId) newChat();
          items = items.filter(x => x.id !== c.id); paint(items);
        } }, icon('trash', 15)))));
    };
    let items = [];
    try {
      items = preview ? [{ id: 'a', title: ta ? 'நோயாளியைப் பதிவு செய்வது எப்படி?' : 'How do I register a patient?', updatedUtc: new Date().toISOString() }, { id: 'b', title: ta ? 'எந்தப் பங்குகள் பதிவுகளை அங்கீகரிக்கலாம்?' : 'Which roles can approve records?', updatedUtc: new Date(Date.now() - 864e5).toISOString() }] : await call('GET', '/conversations');
    } catch (e) { fill(list, h('li', { class: 'hist-empty' }, e.message)); return; }
    paint(items);
    clearBtn.addEventListener('click', async () => {
      if (!await confirmDialog(t('confirmClear'), t('deleteAll'))) return;
      if (!preview) { try { await call('POST', '/conversations/clear'); } catch (e) { err.textContent = e.message; return; } }
      err.textContent = '';
      newChat(); items = []; paint(items);
    });
  }

  // ---- language switch: rebuild the page text, keep the conversation ----
  function switchLanguage() {
    if (typing) typing.finish();
    setLang(lang() === 'ta' ? 'en' : 'ta', !preview);
    build(info, { preview, view, carry: { conversationId, transcript: transcript.slice() } });
  }

  const starters = ((ta && th.startersTa && th.startersTa.length ? th.startersTa : info.starters) || []).slice(0, 4);
  const welcome = h('section', { class: 'welcome' },
    h('div', { class: 'logo', 'aria-hidden': 'true' }, th.avatarText ? th.avatarText : icon(avatarIcon, 28)),
    h('h1', null, timeGreeting() + '.'),
    h('p', null, pickText(info.welcome, th.welcomeTa) || t('welcome', title)),
    starters.length ? h('div', { class: 'starters' }, starters.map(s => h('button', { class: 'starter', type: 'button', onclick: () => ask(s) }, s))) : null);
  scroller.insertBefore(welcome, thread);

  const form = h('form', { class: 'composer', onsubmit: e => { e.preventDefault(); ask(input.value); } }, input, send);
  fill(root, h('div', { class: 'app' },
    h('header', { class: 'top' },
      h('div', { class: 'brand' }, avatar(), h('div', null, h('strong', null, title), th.showStatus === false ? null : h('span', { class: 'presence' }, h('i', { class: 'live' }), t('online')))),
      h('div', { class: 'tools' },
        th.showLanguageSwitch === false ? null : h('button', { class: 'ghost langbtn', type: 'button', onclick: switchLanguage, 'aria-label': t('langSwitchLabel'), title: t('langSwitchLabel') }, t('langSwitch')),
        th.showThemeToggle === false ? null : themeToggle('ghost icon-only', () => ({ toLight: t('toLight'), toDark: t('toDark') })),
        historyOn ? h('button', { class: 'ghost icon-only', type: 'button', onclick: openHistory, 'aria-label': t('history'), title: t('history') }, icon('history', 18)) : null,
        h('button', { class: 'ghost', type: 'button', onclick: newChat, 'aria-label': t('startNewChat') }, icon('plus', 16), h('span', null, t('newChat'))))),
    scroller, toBottom,
    h('footer', { class: 'bottom' }, form, note)));

  if (carry && carry.transcript.length) {
    welcome.hidden = true;
    paintTranscript();
    scrollEnd(false);
  } else if (preview && view === 'chat') {
    welcome.hidden = true;
    thread.append(userRow(ta ? 'புதிய நோயாளியை எப்படிப் பதிவு செய்வது?' : 'How do I register a new patient?'),
      botRow(answerNode(ta ? '**நோயாளிகள்** பக்கத்தைத் திறந்து, **புதிய நோயாளி** என்பதைத் தேர்ந்து, தேவையான புலங்களை நிரப்பி, **சேமி** அழுத்துங்கள்.\n\n- அடையாள எண் தானாக உருவாகும்\n- தொலைபேசி எண் சரிபார்க்கப்படும்\n- நகல் பதிவுகள் குறிக்கப்படும்' : 'Open **Patients**, choose **New patient**, fill in the required fields, then **Save**.\n\n- The ID is created for you\n- Phone number is checked\n- Duplicates are flagged', true), { copy: 'x' }));
  } else if (!preview && historyOn && !carry) resumeLast();

  // Reopening the link continues the most recent chat, so closing the page loses nothing.
  async function resumeLast() {
    try {
      const chats = await call('GET', '/conversations');
      if (!chats.length || conversationId || thread.childElementCount) return;
      showChat(chats[0].id, await call('GET', '/conversations/' + chats[0].id));
    } catch { /* the welcome screen is fine */ }
  }
}

start();
