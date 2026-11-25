// Small UI helpers for the assistant: icons, the "typing" reveal of an answer, and time formatting.
// Everything is built with DOM APIs (no HTML strings), so server/AI text can never become markup.

const NS = 'http://www.w3.org/2000/svg';
const ICONS = {
  sparkle: 'M12 3l1.9 5.1L19 10l-5.1 1.9L12 17l-1.9-5.1L5 10l5.1-1.9L12 3zM19 15l.8 2.2L22 18l-2.2.8L19 21l-.8-2.2L16 18l2.2-.8L19 15z',
  send: 'M22 2L11 13M22 2l-7 20-4-9-9-4 20-7z',
  copy: 'M9 9h13v13H9zM5 15H2V2h13v3',
  check: 'M20 6L9 17l-5-5',
  plus: 'M12 5v14M5 12h14',
  trash: 'M3 6h18M8 6V4h8v2M19 6l-1 14H6L5 6M10 11v6M14 11v6',
  user: 'M20 21v-2a4 4 0 0 0-4-4H8a4 4 0 0 0-4 4v2M12 11a4 4 0 1 0 0-8 4 4 0 0 0 0 8z',
  info: 'M12 22a10 10 0 1 0 0-20 10 10 0 0 0 0 20zM12 16v-4M12 8h.01',
  down: 'M12 5v14M19 12l-7 7-7-7',
  menu: 'M3 12h18M3 6h18M3 18h18',
  alert: 'M12 9v4M12 17h.01M10.3 3.9L1.8 18a2 2 0 0 0 1.7 3h17a2 2 0 0 0 1.7-3L13.7 3.9a2 2 0 0 0-3.4 0z',
  refresh: 'M23 4v6h-6M1 20v-6h6M3.5 9a9 9 0 0 1 14.9-3.4L23 10M1 14l4.6 4.4A9 9 0 0 0 20.5 15',
  sun: 'M12 17a5 5 0 1 0 0-10 5 5 0 0 0 0 10zM12 1v2M12 21v2M4.2 4.2l1.4 1.4M18.4 18.4l1.4 1.4M1 12h2M21 12h2M4.2 19.8l1.4-1.4M18.4 5.6l1.4-1.4',
  moon: 'M21 12.8A9 9 0 1 1 11.2 3a7 7 0 0 0 9.8 9.8z',
  home: 'M3 9l9-7 9 7v11a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2zM9 22V12h6v10',
  review: 'M9 11l3 3L22 4M21 12v7a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2h11',
  book: 'M4 19.5A2.5 2.5 0 0 1 6.5 17H20M4 19.5A2.5 2.5 0 0 0 6.5 22H20V2H6.5A2.5 2.5 0 0 0 4 4.5z',
  chat: 'M21 15a2 2 0 0 1-2 2H7l-4 4V5a2 2 0 0 1 2-2h14a2 2 0 0 1 2 2z',
  key: 'M21 2l-2 2m-7.6 7.6a5.5 5.5 0 1 1-7.8 7.8 5.5 5.5 0 0 1 7.8-7.8zm0 0L15.5 7.5m0 0l3 3L22 7l-3-3m-3.5 3.5L19 4',
  share: 'M18 8a3 3 0 1 0 0-6 3 3 0 0 0 0 6zM6 15a3 3 0 1 0 0-6 3 3 0 0 0 0 6zM18 22a3 3 0 1 0 0-6 3 3 0 0 0 0 6zM8.6 13.5l6.8 4M15.4 6.5l-6.8 4',
  chart: 'M18 20V10M12 20V4M6 20v-6',
  logout: 'M9 21H5a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2h4M16 17l5-5-5-5M21 12H9',
  building: 'M3 21h18M5 21V7l7-4 7 4v14M9 9h1M9 13h1M14 9h1M14 13h1',
  upload: 'M21 15v4a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2v-4M17 8l-5-5-5 5M12 3v12',
  close: 'M18 6L6 18M6 6l12 12',
  history: 'M3 12a9 9 0 1 0 3-6.7L3 8M3 3v5h5M12 7v5l3 2',
  search: 'M11 19a8 8 0 1 0 0-16 8 8 0 0 0 0 16zM21 21l-4.3-4.3',
  chevron: 'M9 18l6-6-6-6',
  bolt: 'M13 2L3 14h9l-1 8 10-12h-9z',
  doc: 'M14 2H6a2 2 0 0 0-2 2v16a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V8zM14 2v6h6M8 13h8M8 17h8',
  sliders: 'M4 21v-7M4 10V3M12 21v-9M12 8V3M20 21v-5M20 12V3M1 14h6M9 8h6M17 16h6',
  cloud: 'M18 10h-1.26A8 8 0 1 0 9 20h9a5 5 0 0 0 0-10z',
  server: 'M2 3h20v7H2zM2 14h20v7H2zM6 6.5h.01M6 17.5h.01',
  drive: 'M22 12H2M5.45 5.11L2 12v6a2 2 0 0 0 2 2h16a2 2 0 0 0 2-2v-6l-3.45-6.89A2 2 0 0 0 16.76 4H7.24a2 2 0 0 0-1.79 1.11zM6 16h.01M10 16h.01',
  database: 'M12 8c4.97 0 9-1.34 9-3s-4.03-3-9-3-9 1.34-9 3 4.03 3 9 3zM21 12c0 1.66-4.03 3-9 3s-9-1.34-9-3M3 5v14c0 1.66 4.03 3 9 3s9-1.34 9-3V5',
  shield: 'M12 22s8-4 8-10V5l-8-3-8 3v7c0 6 8 10 8 10z',
  activity: 'M22 12h-4l-3 9L9 3l-3 9H2',
  lock: 'M5 11h14v10H5zM8 11V7a4 4 0 0 1 8 0v4',
  eye: 'M1 12s4-8 11-8 11 8 11 8-4 8-11 8-11-8-11-8zM12 15a3 3 0 1 0 0-6 3 3 0 0 0 0 6z',
  eyeoff: 'M17.94 17.94A10.07 10.07 0 0 1 12 20c-7 0-11-8-11-8a18.45 18.45 0 0 1 5.06-5.94M9.9 4.24A9.12 9.12 0 0 1 12 4c7 0 11 8 11 8a18.5 18.5 0 0 1-2.16 3.19m-6.72-1.07a3 3 0 1 1-4.24-4.24M1 1l22 22',
  mail: 'M4 4h16a2 2 0 0 1 2 2v12a2 2 0 0 1-2 2H4a2 2 0 0 1-2-2V6a2 2 0 0 1 2-2zM22 6l-10 7L2 6',
  panel: 'M5 3h14a2 2 0 0 1 2 2v14a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2zM9 3v18',
  selector: 'M7 15l5 5 5-5M7 9l5-5 5 5',
  dots: 'M12 12h.01M19 12h.01M5 12h.01',
  external: 'M18 13v6a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2V8a2 2 0 0 1 2-2h6M15 3h6v6M10 14L21 3',
  filter: 'M22 3H2l8 9.46V19l4 2v-8.54z',
  checkcircle: 'M22 11.08V12a10 10 0 1 1-5.93-9.14M22 4L12 14.01l-3-3',
};

export function icon(name, size = 18) {
  const svg = document.createElementNS(NS, 'svg');
  svg.setAttribute('viewBox', '0 0 24 24'); svg.setAttribute('width', size); svg.setAttribute('height', size);
  svg.setAttribute('fill', 'none'); svg.setAttribute('stroke', 'currentColor'); svg.setAttribute('stroke-width', '1.75');
  svg.setAttribute('stroke-linecap', 'round'); svg.setAttribute('stroke-linejoin', 'round'); svg.setAttribute('aria-hidden', 'true');
  const p = document.createElementNS(NS, 'path'); p.setAttribute('d', ICONS[name] || ''); svg.append(p);
  return svg;
}

/** Copy text to the clipboard. Falls back to a temporary textarea where the Clipboard API is unavailable (plain-HTTP intranets). */
export async function copyText(text) {
  try { if (navigator.clipboard && window.isSecureContext) { await navigator.clipboard.writeText(text); return true; } } catch { /* try the fallback */ }
  const ta = document.createElement('textarea');
  ta.value = text; ta.setAttribute('readonly', ''); ta.style.cssText = 'position:fixed;top:0;left:0;opacity:0;';
  document.body.append(ta); ta.select();
  let ok = false;
  try { ok = document.execCommand('copy'); } catch { ok = false; }
  ta.remove();
  return ok;
}

export const reducedMotion =() => { try { return window.matchMedia('(prefers-reduced-motion: reduce)').matches; } catch { return false; } };

export function formatTime(iso) {
  const d = iso ? new Date(String(iso).endsWith('Z') || /[+-]\d\d:?\d\d$/.test(String(iso)) ? iso : iso + 'Z') : new Date();
  return isNaN(d) ? '' : d.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' });
}

export function relativeTime(iso) {
  const d = new Date(String(iso).endsWith('Z') ? iso : iso + 'Z');
  if (isNaN(d)) return '';
  const mins = Math.round((Date.now() - d.getTime()) / 60000);
  if (mins < 1) return 'Just now';
  if (mins < 60) return mins + ' min ago';
  const hrs = Math.round(mins / 60);
  if (hrs < 24) return hrs + ' h ago';
  const days = Math.round(hrs / 24);
  return days === 1 ? 'Yesterday' : days < 7 ? days + ' days ago' : d.toLocaleDateString([], { day: 'numeric', month: 'short' });
}

export function greeting() {
  const h = new Date().getHours();
  return h < 5 ? 'Hello' : h < 12 ? 'Good morning' : h < 18 ? 'Good afternoon' : 'Good evening';
}

const BLOCKS = 'p,li,ul,ol,table,thead,tbody,tr,.table-wrap,h3,h4,h5,h6,blockquote,pre,hr';

/**
 * Reveals the text inside an already-rendered answer gradually, like a person typing, keeping its structure
 * (lists, tables, bold). Blocks stay hidden until the typing reaches them. Returns { finish } to complete instantly.
 * With "reduce motion" on, it completes immediately.
 */
export function typewrite(root, { onTick = () => {}, onDone = () => {}, msPerChar = 14, minMs = 500, maxMs = 3500 } = {}) {
  const items = [];
  // Anything marked data-notype (such as the section pills) is not typed; the caller reveals it when typing is done.
  const walker = document.createTreeWalker(root, NodeFilter.SHOW_TEXT, { acceptNode: n => (n.parentElement && n.parentElement.closest('[data-notype]')) ? NodeFilter.FILTER_REJECT : NodeFilter.FILTER_ACCEPT });
  for (let n = walker.nextNode(); n; n = walker.nextNode()) if (n.nodeValue.length) items.push({ node: n, text: n.nodeValue });
  const total = items.reduce((s, i) => s + i.text.length, 0);
  if (reducedMotion() || total === 0) { onDone(); return { finish() {} }; }

  const ancestors = node => { const out = []; for (let e = node.parentElement; e && e !== root; e = e.parentElement) if (e.matches(BLOCKS)) out.push(e); return out; };
  items.forEach(i => { i.blocks = ancestors(i.node); i.node.nodeValue = ''; i.blocks.forEach(b => { b.hidden = true; }); });

  const caret = document.createElement('span'); caret.className = 'caret'; caret.setAttribute('aria-hidden', 'true');
  const duration = Math.min(maxMs, Math.max(minMs, total * msPerChar));
  const start = performance.now();
  let done = false, raf = 0;

  function render(count) {
    let left = count, last = null;
    for (const i of items) {
      const k = Math.max(0, Math.min(i.text.length, left));
      if (k > 0) { i.node.nodeValue = i.text.slice(0, k); i.blocks.forEach(b => { b.hidden = false; }); last = i; }
      left -= i.text.length;
      if (left <= 0) break;
    }
    if (last) last.node.parentNode.insertBefore(caret, last.node.nextSibling);
  }
  function finish() {
    if (done) return; done = true; cancelAnimationFrame(raf);
    items.forEach(i => { i.node.nodeValue = i.text; i.blocks.forEach(b => { b.hidden = false; }); });
    caret.remove(); onTick(); onDone();
  }
  function frame(now) {
    if (done) return;
    const progress = Math.min(1, (now - start) / duration);
    render(Math.floor(progress * total)); onTick();
    if (progress >= 1) finish(); else raf = requestAnimationFrame(frame);
  }
  raf = requestAnimationFrame(frame);
  return { finish };
}
