import { icon } from './chatkit.js';
// Tiny DOM helpers. Everything the server, a document or the AI produces is inserted as text (never as HTML),
// so untrusted content cannot inject markup or scripts.

export function h(tag, props, ...children) {
  const el = document.createElement(tag);
  for (const [k, v] of Object.entries(props || {})) {
    if (v == null || v === false) continue;
    if (k === 'class') el.className = v;
    else if (k === 'style') el.style.cssText = v; // CSSOM assignment is allowed by the CSP; a style attribute is not
    else if (k.startsWith('on') && typeof v === 'function') el.addEventListener(k.slice(2).toLowerCase(), v);
    else if (v === true) el.setAttribute(k, '');
    else el.setAttribute(k, String(v));
  }
  add(el, children);
  return el;
}

export function add(el, children) {
  for (const c of children.flat(Infinity)) {
    if (c == null || c === false) continue;
    el.append(c instanceof Node ? c : document.createTextNode(String(c)));
  }
  return el;
}

export function clear(el) { while (el.firstChild) el.removeChild(el.firstChild); return el; }

// Replace the contents of el. Unlike el.append, this skips null/false and flattens arrays instead of printing "null".
export function fill(el, ...children) { clear(el); return add(el, children); }

// ---- status presentation -------------------------------------------------------------------------------------------
export const STATUS = {
  Uploaded:         { label: 'Uploaded',            tone: 'info',  busy: true,  hint: 'Waiting to be picked up.' },
  Queued:           { label: 'Queued',              tone: 'info',  busy: true,  hint: 'Waiting for the background worker.' },
  Processing:       { label: 'Extracting text',     tone: 'info',  busy: true,  hint: 'Reading pages from the file.' },
  AwaitingOcr:      { label: 'Needs OCR',           tone: 'warn',  busy: false, hint: 'Some pages are scans. Enable OCR (or wait for its daily limit to reset), then press Retry.' },
  Extracted:        { label: 'Text extracted',      tone: 'info',  busy: true,  hint: 'Text is ready and AI analysis is about to start. If nothing happens after a minute, press Retry.' },
  Analyzing:        { label: 'AI analysing',        tone: 'info',  busy: true,  hint: 'Finding modules, pages, rules and workflows.' },
  AwaitingAnalysis: { label: 'Waiting for AI quota', tone: 'warn', busy: false, hint: 'The AI daily limit is reached or not configured. Press Retry after it resets. Finished sections are not re-sent.' },
  PendingReview:    { label: 'Ready for review',    tone: 'review', busy: false, hint: 'Check the extracted knowledge, then approve the document.' },
  Approved:         { label: 'Approved',            tone: 'ok',    busy: false, hint: 'Approved and available to the chatbot once indexed.' },
  Failed:           { label: 'Failed',              tone: 'bad',   busy: false, hint: 'Processing failed. See details, then Retry.' },
  Deleted:          { label: 'Deleted',             tone: 'bad',   busy: false, hint: '' },
};
export const statusInfo = s => STATUS[s] || { label: s, tone: 'info', busy: false, hint: '' };

export function badge(text, tone = 'info') { return h('span', { class: 'badge badge-' + tone }, text); }
export const reviewBadge = r => badge(r === 'Approved' ? 'Approved' : r === 'Rejected' ? 'Rejected' : 'Unreviewed',
  r === 'Approved' ? 'ok' : r === 'Rejected' ? 'bad' : 'review');

export function citation(src) {
  if (!src || !src.document) return null;
  return h('span', { class: 'cite', title: 'Source document and page' }, src.document + (src.page ? ' · p.' + src.page : ''));
}

// ---- toasts --------------------------------------------------------------------------------------------------------
export function toast(message, kind = 'info') {
  const host = document.getElementById('toasts');
  if (!host) return;
  const t = h('div', { class: 'toast toast-' + kind, role: kind === 'bad' ? 'alert' : 'status' }, message);
  host.append(t);
  setTimeout(() => t.remove(), kind === 'bad' ? 8000 : 4500);
}

export function formatDate(iso) {
  if (!iso) return '';
  const d = new Date(iso.endsWith('Z') ? iso : iso + 'Z');
  return isNaN(d) ? '' : d.toLocaleString([], { dateStyle: 'medium', timeStyle: 'short' });
}

export function empty(title, text, iconName = 'info') {
  return h('div', { class: 'empty' }, h('span', { class: 'empty-ic', 'aria-hidden': 'true' }, icon(iconName, 24)), h('strong', null, title), text ? h('p', { class: 'muted' }, text) : null);
}

/** Close a modal dialog when the dark area outside it is clicked. Keyboard "clicks" on its buttons have a button as target, so they never count. */
export function closeOnOutside(dlg) {
  dlg.addEventListener('click', e => {
    if (e.target !== dlg) return;
    const r = dlg.getBoundingClientRect();
    if (e.clientX < r.left || e.clientX > r.right || e.clientY < r.top || e.clientY > r.bottom) dlg.close();
  });
  return dlg;
}

export function confirmDialog(message, okLabel = 'Delete') {
  return new Promise(resolve => {
    const dlg = h('dialog', { class: 'dialog', 'aria-label': 'Confirm' },
      h('p', null, message),
      h('div', { class: 'actions' },
        h('button', { class: 'btn', type: 'button', onclick: () => { dlg.close(); resolve(false); } }, 'Cancel'),
        h('button', { class: 'btn btn-danger', type: 'button', onclick: () => { dlg.close(); resolve(true); } }, okLabel)));
    dlg.addEventListener('close', () => { dlg.remove(); resolve(false); });
    closeOnOutside(dlg);
    document.body.append(dlg);
    dlg.showModal();
  });
}

// ---- safe Markdown renderer for chat answers --------------------------------------------------------------------------
// Supports: headings, paragraphs, **bold**, *italic*, `code`, code blocks, bullet / numbered / lettered lists (nested, with the
// numbering kept across blank lines), block quotes, horizontal rules and tables. It only ever creates text nodes and a fixed set
// of elements: no HTML, no links, nothing is evaluated. Citation numbers and "Source:" lines are never shown.

const MARKER = /\s*[\[【]\s*\d{1,2}(?:\s*[,;]\s*\d{1,2})*\s*(?:†[^\]】]*)?[\]】]/g;
// What is left of an inline "*Source: [2]*" once the numbers are gone: a label with nothing after it, at the end of a line.
const EMPTY_LABEL = /[ \t]*[*_]{0,2}(?:sources?|references?)[ \t]*:[ \t]*(?:\d{1,2}(?:[ \t]*[,;&][ \t]*\d{1,2})*)?[ \t]*[*_]{0,2}[ \t]*(?=\n|$)/gim;
const SOURCE_LINE = /^[ \t>*_\-–—•]*\**_*(?:sources?|references?)\b(?:\s*[:：].*|[\s\d,;&\[\]()*_]*)$/i;
const LIST_ITEM = /^(\s*)([-*•+]|\d{1,3}[.)]|[a-h][.)])\s+(.*)$/;
const HR = /^\s*([-*_])(\s*\1){2,}\s*$/;
const INLINE_PATTERN = /(\*\*[^*\n]+\*\*|`[^`\n]+`|(?<![\w*])\*[^*\s][^*\n]*?\*(?![\w*])|(?<!\w)_[^_\s][^_\n]*?_(?!\w))/;

function inline(text) {
  const out = [];
  let last = 0, m;
  const re = new RegExp(INLINE_PATTERN.source, 'g');   // one regex per call: this function calls itself for text inside **bold**
  let guard = 0;
  while ((m = re.exec(text)) !== null && guard++ < 2000) {
    if (m.index > last) out.push(text.slice(last, m.index));
    const tok = m[0];
    if (tok.startsWith('**')) out.push(h('strong', null, inline(tok.slice(2, -2))));
    else if (tok.startsWith('`')) out.push(h('code', null, tok.slice(1, -1)));
    else out.push(h('em', null, tok.slice(1, -1)));
    last = m.index + tok.length;
  }
  if (last < text.length) out.push(text.slice(last));
  return out;
}

/** "a. Open the page. b. Click New. c. Save." written on one line becomes three lines (only when the letters really run a, b, c...). */
function splitLetters(line) {
  const m = /^(\s*)a[.)]\s/.exec(line);
  if (!m) return [line];
  const parts = line.trim().split(/\s+(?=[a-h][.)]\s)/);
  const letters = parts.map(p => p[0]);
  const ascending = parts.length > 1 && letters.every((c, k) => c === String.fromCharCode(97 + k));
  return ascending ? parts.map(p => m[1] + p) : [line];
}

export function renderAnswer(text) {
  const root = h('div', { class: 'answer' });
  const raw = String(text || '').replace(/\r/g, '').replace(MARKER, '').replace(EMPTY_LABEL, '').replace(/\t/g, '    ');
  const lines = raw.split('\n').filter(l => !SOURCE_LINE.test(l)).flatMap(splitLetters);

  let stack = [];                 // open lists: { indent, kind, el, lastLi }
  let para = [];
  const closeLists = () => { stack = []; };
  const flushPara = () => { if (para.length) { root.append(h('p', null, inline(para.join(' ')))); para = []; } };
  const top = () => stack[stack.length - 1];

  let guard = 0;
  for (let i = 0; i < lines.length;) {
    // Safety net: a rendering bug must never freeze the page. Past the limit, show the rest as plain text.
    if (++guard > lines.length * 3 + 100) { flushPara(); root.append(h('p', null, lines.slice(i).join(' '))); break; }
    const line = lines[i];

    if (/^\s*```/.test(line)) {                                         // code block
      flushPara(); closeLists();
      const code = []; i++;
      while (i < lines.length && !/^\s*```/.test(lines[i])) code.push(lines[i++]);
      i++;
      root.append(h('pre', null, h('code', null, code.join('\n'))));
      continue;
    }

    if (!line.trim()) {                                                 // blank line: a list carries on if the next line is another item
      let j = i + 1; while (j < lines.length && !lines[j].trim()) j++;
      if (!(stack.length && j < lines.length && LIST_ITEM.test(lines[j]) && !HR.test(lines[j]))) { flushPara(); closeLists(); }
      i++; continue;
    }

    if (HR.test(line)) { flushPara(); closeLists(); root.append(h('hr')); i++; continue; }

    const hm = /^\s*(#{1,4})\s+(.*)$/.exec(line);                       // heading
    if (hm) { flushPara(); closeLists(); root.append(h('h' + (hm[1].length + 2), { class: 'md-h' }, inline(hm[2]))); i++; continue; }

    if (/^\s*\|/.test(line)) {                                          // table
      flushPara(); closeLists();
      const rows = [];
      while (i < lines.length && /^\s*\|/.test(lines[i])) rows.push(lines[i++]);
      const cells = r => r.trim().replace(/^\||\|$/g, '').split('|').map(c => c.trim());
      const body = rows.filter(r => r.replace(/[|\-:\s]/g, '') !== '');
      if (body.length) {
        const [head, ...rest] = body;
        root.append(h('div', { class: 'table-wrap' }, h('table', { class: 'table' },
          h('thead', null, h('tr', null, cells(head).map(c => h('th', null, inline(c))))),
          h('tbody', null, rest.map(r => h('tr', null, cells(r).map(c => h('td', null, inline(c)))))))));
      }
      continue;
    }

    if (/^\s*>/.test(line)) {                                           // block quote
      flushPara(); closeLists();
      const q = [];
      while (i < lines.length && /^\s*>/.test(lines[i])) q.push(lines[i++].replace(/^\s*>\s?/, '').trim());
      root.append(h('blockquote', null, inline(q.join(' '))));
      continue;
    }

    const m = LIST_ITEM.exec(line);
    if (m) {
      const [, lead, marker, content] = m;
      const kind = /^[-*•+]$/.test(marker) ? 'ul' : /^\d/.test(marker) ? 'ol' : 'alpha';
      const nextIsB = i + 1 < lines.length && /^\s*b[.)]\s/.test(lines[i + 1]);
      // a lettered marker is only a list when we are already inside one, or it starts a, b, ... run
      if (kind === 'alpha' && !stack.length && !(marker[0] === 'a' && nextIsB)) { para.push(line.trim()); i++; continue; }
      flushPara();
      let indent = lead.length;
      // "a." under "1." at the same indentation means a sub-step of that numbered step (also for b., c., ...)
      // Bullets written directly under a numbered step (not indented) are that step's details, not a new list.
      if (kind === 'alpha' || (kind === 'ul' && stack.length && !(top().kind === 'ul' && indent > top().indent))) {
        const owner = [...stack].reverse().find(l => l.kind === 'ol' && l.indent <= indent);
        if (owner) indent = owner.indent + 2;
      }
      while (stack.length && indent < top().indent) stack.pop();
      if (stack.length && indent === top().indent && top().kind !== kind) stack.pop();
      if (!stack.length || indent > top().indent) {
        const el = h(kind === 'ul' ? 'ul' : 'ol');
        if (kind === 'ol' && parseInt(marker, 10) > 1) el.setAttribute('start', String(parseInt(marker, 10)));
        if (kind === 'alpha') { el.setAttribute('type', 'a'); el.setAttribute('start', String(marker.charCodeAt(0) - 96)); }
        (stack.length ? top().lastLi || root : root).append(el);
        stack.push({ indent, kind, el, lastLi: null });
      }
      const li = h('li', null, inline(content));
      top().el.append(li); top().lastLi = li;
      i++; continue;
    }

    if (stack.length) {                                                 // an indented line continues the last list item; anything else ends the list
      const lead = /^\s*/.exec(line)[0].length;
      if (lead > top().indent && top().lastLi) { top().lastLi.append(' ', ...inline(line.trim())); i++; continue; }
      closeLists();
    }
    para.push(line.trim()); i++;
  }
  flushPara();
  return root;
}