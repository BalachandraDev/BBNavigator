import { h, add, clear, empty, badge } from '../dom.js';
import { api } from '../api.js';
import { icon } from '../chatkit.js';

// AI usage: what the free AI allowances look like right now, when each one renews, and what this company has used.
// Everything is built from DOM nodes and SVG attributes (no HTML strings, no inline style attributes).

const PURPOSE = {
  gemini: { name: 'Gemini total', vendor: 'document analysis + Tamil chat', icon: 'doc', hue: 'a' },
  'gemini-chat': { name: 'Tamil chat answers', vendor: 'Gemini, part of its total', icon: 'chat', hue: 'e' },
  cloudflare: { name: 'Search embeddings', vendor: 'Cloudflare', icon: 'search', hue: 'b' },
  groq: { name: 'Chat answers', vendor: 'Groq', icon: 'chat', hue: 'c' },
  mistral: { name: 'Scanned-PDF OCR', vendor: 'Mistral', icon: 'upload', hue: 'd' },
};
const purpose = p => PURPOSE[p] || { name: p, vendor: '', icon: 'bolt', hue: 'a' };
// A table row knows what its calls were for: Gemini is used for reading documents and for writing Tamil answers.
const purposeOf = l => l.provider === 'gemini' && l.kind === 'Chat' ? { name: 'Tamil chat answers', vendor: 'Gemini', icon: 'chat', hue: 'c' } : l.provider === 'gemini' ? { name: 'Document analysis', vendor: 'Gemini', icon: 'doc', hue: 'a' } : purpose(l.provider);
const STATUS = {
  ok: { label: 'Healthy', tone: 'ok' }, warn: { label: 'Near limit', tone: 'warn' }, full: { label: 'Limit reached', tone: 'bad' }, off: { label: 'Off', tone: 'info' },
};
const n = v => Number(v || 0).toLocaleString(undefined, { maximumFractionDigits: 2 });
const compact = v => Number(v || 0).toLocaleString(undefined, { notation: 'compact', maximumFractionDigits: 1 });
const asDate = iso => new Date(String(iso).endsWith('Z') ? iso : iso + 'Z');
const NS = 'http://www.w3.org/2000/svg';

function duration(ms) {
  const mins = Math.max(0, Math.round(ms / 60000));
  if (mins < 1) return 'under a minute';
  const hrs = Math.floor(mins / 60), rest = mins % 60;
  return hrs > 0 ? `${hrs} h ${String(rest).padStart(2, '0')} min` : `${rest} min`;
}
const localTime = d => d.toLocaleString([], { weekday: 'short', hour: '2-digit', minute: '2-digit' });

/** A circular gauge. The arc animates from empty to the used share. */
function ring(percent, status) {
  const R = 42, C = 2 * Math.PI * R, pct = Math.max(0, Math.min(100, percent));
  const svg = document.createElementNS(NS, 'svg');
  svg.setAttribute('viewBox', '0 0 100 100'); svg.setAttribute('class', 'ring'); svg.setAttribute('aria-hidden', 'true');
  const mk = (cls, extra) => { const c = document.createElementNS(NS, 'circle'); c.setAttribute('cx', 50); c.setAttribute('cy', 50); c.setAttribute('r', R); c.setAttribute('fill', 'none'); c.setAttribute('class', cls); Object.entries(extra || {}).forEach(([k, v]) => c.setAttribute(k, v)); return c; };
  const arc = mk('ring-fill ring-' + status, { 'stroke-linecap': 'round', 'stroke-dasharray': `0 ${C}`, transform: 'rotate(-90 50 50)' });
  svg.append(mk('ring-track'), arc);
  requestAnimationFrame(() => requestAnimationFrame(() => arc.setAttribute('stroke-dasharray', `${(pct / 100) * C} ${C}`)));
  return svg;
}

/** One cap, as a card: gauge, figures, exact renewal time, and the month so far. */
function gauge(l) {
  const st = STATUS[l.status] || STATUS.ok, p = purpose(l.provider);
  const resets = asDate(l.resetsAtUtc);
  const countdown = h('strong', null, '');
  const tick = () => { countdown.textContent = duration(resets - Date.now()); };
  tick();
  const off = l.status === 'off';
  const monthShare = l.monthCapacity > 0 ? Math.min(100, (l.monthUsed / l.monthCapacity) * 100) : 0;
  const mfill = h('i', { class: 'mbar-fill fill-' + l.status }); mfill.style.width = monthShare + '%';

  const row = h('article', { class: 'gauge hue-' + p.hue },
    h('header', { class: 'gauge-head' },
      h('span', { class: 'gauge-ic' }, icon(p.icon, 18)),
      h('span', { class: 'gauge-title' }, h('strong', null, p.name), h('small', null, `${p.vendor} · ${l.metric} per day`)),
      badge(st.label, st.tone)),
    h('div', { class: 'gauge-body' },
      h('div', { class: 'ring-wrap', role: 'progressbar', 'aria-label': `${p.name}: ${l.metric} used today`, 'aria-valuemin': 0, 'aria-valuemax': 100, 'aria-valuenow': Math.round(l.percent) },
        ring(off ? 0 : l.percent, l.status), h('span', { class: 'ring-num' }, off ? 'Off' : Math.round(l.percent) + '%')),
      off
        ? h('p', { class: 'muted' }, 'No daily limit is set, so this feature is switched off. Set one in .env to turn it on.')
        : h('div', { class: 'gauge-figs' },
          h('div', null, h('b', { title: n(l.used) }, compact(l.used)), h('small', null, 'used today')),
          h('div', null, h('b', { title: n(Math.max(0, l.limit - l.used)) }, compact(Math.max(0, l.limit - l.used))), h('small', null, 'left')),
          h('div', null, h('b', { title: n(l.limit) }, compact(l.limit)), h('small', null, 'daily limit')))),
    h('footer', { class: 'gauge-foot' },
      h('div', { class: 'reset' }, icon('refresh', 14), h('span', null, 'Renews in ', countdown, h('small', null, ` · ${l.resetBasis} · ${localTime(resets)} your time`))),
      l.perMinute ? h('div', { class: 'pace' }, icon('bolt', 14), h('span', null, `Paced to ${l.perMinute} requests / min`)) : null,
      h('div', { class: 'month' }, h('div', { class: 'month-top' }, h('span', null, 'This month'), h('span', null, `${n(l.monthUsed)} / ${n(l.monthCapacity)} ${l.unit}`)),
        h('div', { class: 'mbar' }, mfill))));
  return { row, tick, resets };
}

const sum = (lines, k) => (lines || []).reduce((a, l) => a + Number(l[k] || 0), 0);

function breakdown(lines) {
  if (!lines || !lines.length) return empty('No AI calls yet.');
  const total = Math.max(1, sum(lines, 'requests'));
  return h('div', { class: 'table-wrap' }, h('table', { class: 'table stack usage-table' },
    h('thead', null, h('tr', null, ['Purpose', 'Share', 'Requests', 'Tokens in', 'Tokens out', 'OCR pages'].map((t, i) => h('th', { class: i > 1 ? 'num' : '' }, t)))),
    h('tbody', null, lines.map(l => {
      const share = h('i'); share.style.width = Math.round((l.requests / total) * 100) + '%';
      const p = purposeOf(l);
      return h('tr', null,
        h('td', { 'data-label': 'Purpose' }, h('span', { class: 'pname hue-' + p.hue }, h('i', { class: 'pdot' }), h('span', null, h('strong', null, p.name), h('small', null, l.model)))),
        h('td', { 'data-label': 'Share' }, h('span', { class: 'share' }, share)),
        h('td', { class: 'num', 'data-label': 'Requests' }, n(l.requests)),
        h('td', { class: 'num', 'data-label': 'Tokens in' }, n(l.inputTokens)),
        h('td', { class: 'num', 'data-label': 'Tokens out' }, n(l.outputTokens)),
        h('td', { class: 'num', 'data-label': 'OCR pages' }, n(l.ocrPages)));
    }))));
}


/** Tokens per share link: which chatbot is using the free allowance. */
function linkTable(r) {
  if (!r.links.length) return empty('No share links yet.', 'Create one under Share links. Its AI use will be counted here.', 'share');
  const top = Math.max(1, ...r.links.map(l => l.totalTokens));
  const rows = r.links.map(l => {
    const bar = h('i'); bar.style.width = Math.round((l.totalTokens / top) * 100) + '%';
    return h('tr', null,
      h('td', { 'data-label': 'Share link' }, h('span', { class: 'pname hue-c' }, h('i', { class: 'pdot' }),
        h('span', null, h('strong', null, l.name), l.exists ? (l.active ? null : h('small', null, 'Disabled')) : h('small', null, 'Deleted, history kept')))),
      h('td', { 'data-label': 'Share of tokens' }, h('span', { class: 'share' }, bar), h('small', { class: 'muted' }, ` ${l.sharePercent}%`)),
      h('td', { class: 'num', 'data-label': 'Questions', title: 'Visitor questions still stored in this period' }, n(l.questions)),
      h('td', { class: 'num', 'data-label': 'AI calls', title: 'Answers that really called the AI model' }, n(l.aiCalls)),
      h('td', { class: 'num', 'data-label': 'Tokens in' }, n(l.inputTokens)),
      h('td', { class: 'num', 'data-label': 'Tokens out' }, n(l.outputTokens)),
      h('td', { class: 'num', 'data-label': 'Total tokens' }, h('strong', null, n(l.totalTokens))),
      h('td', { class: 'num', 'data-label': 'Per AI call', title: 'Average tokens per AI call: a high number means long questions or long answers' }, n(l.tokensPerCall)),
      h('td', { class: 'num', 'data-label': 'Search units', title: 'Search embedding neurons used for this link' }, n(l.embeddingUnits)));
  });
  return h('div', { class: 'table-wrap' }, h('table', { class: 'table stack usage-table' },
    h('thead', null, h('tr', null, ['Share link', 'Share of tokens', 'Questions', 'AI calls', 'Tokens in', 'Tokens out', 'Total tokens', 'Per AI call', 'Search units'].map((t, i) => h('th', { class: i > 1 ? 'num' : '' }, t)))),
    h('tbody', null, rows)),
    h('p', { class: 'muted small' }, `Chat tokens only. Not from a share link (your Assistant page, access keys): ${n(r.otherTokens)} tokens. All chat together: ${n(r.totalTokens)} tokens. `,
      'Greetings, "what can I ask?", off-topic replies and repeated questions answered from memory make no AI call, so they add no tokens.'));
}
export async function usageView(main, ctx) {
  const usage = await api('GET', '/usage');
  let health = null;
  try { health = await (await fetch('/health/ready')).json(); } catch { /* shown as unreachable below */ }
  // Features that are switched off (no limit set, e.g. scanned-PDF OCR) are not shown as gauges; they appear by themselves once a limit is set.
  const all = usage.limits || [];
  const limits = all.filter(l => l.status !== 'off');
  const hidden = [...new Set(all.filter(l => l.status === 'off').map(l => purpose(l.provider).name))];
  const rows = limits.map(gauge);

  // ---- headline numbers ----
  const worst = limits.some(l => l.status === 'full') ? 'full' : limits.some(l => l.status === 'warn') ? 'warn' : 'ok';
  const verdict = { ok: ['All systems healthy', 'Every allowance has room today.', 'ok'], warn: ['Approaching a limit', 'A feature may pause soon; it resumes after the reset.', 'warn'], full: ['A limit is reached', 'That feature is paused until it renews. Nothing paid is ever used.', 'bad'] }[worst];
  const live = rows.map(r => r.resets).sort((a, b) => a - b)[0];
  const nextReset = h('b', null, live ? duration(live - Date.now()) : '–');
  const kpi = (iconName, tone, value, label, hint) => h('div', { class: 'kpi tone-' + tone },
    h('span', { class: 'kpi-ic' }, icon(iconName, 20)), h('div', null, value, h('span', null, label), hint ? h('small', null, hint) : null));
  const kpis = h('div', { class: 'kpis' },
    kpi('check', verdict[2], h('b', null, verdict[0]), 'Overall status', verdict[1]),
    kpi('chat', 'brand', h('b', null, n(sum(usage.today, 'requests'))), 'AI requests today', `${n(sum(usage.thisMonth, 'requests'))} this month`),
    kpi('chart', 'review', h('b', null, compact(sum(usage.today, 'inputTokens') + sum(usage.today, 'outputTokens'))), 'Tokens today', `${compact(sum(usage.thisMonth, 'inputTokens') + sum(usage.thisMonth, 'outputTokens'))} this month`),
    kpi('refresh', 'info', nextReset, 'Next allowance reset', 'Each limit renews on its own'));

  // ---- your company: today / this month ----
  let period = 'today';
  const tableHost = h('div');
  const paintTable = () => { clear(tableHost).append(breakdown(period === 'today' ? usage.today : usage.thisMonth)); btns.forEach(b => b.setAttribute('aria-pressed', String(b.dataset.v === period))); };
  const btns = [['Today', 'today'], ['This month', 'month']].map(([label, v]) => { const b = h('button', { type: 'button', onclick: () => { period = v; paintTable(); } }, label); b.dataset.v = v; return b; });
  paintTable();

  // ---- per share link ----
  let linkDays = 30;
  const linkHost = h('div', null, h('div', { class: 'skel-set' }, h('i'), h('i')));
  const paintLinks = async () => {
    dayBtns.forEach(b => b.setAttribute('aria-pressed', String(Number(b.dataset.v) === linkDays)));
    try { clear(linkHost).append(linkTable(await api('GET', '/bot-links/usage?days=' + linkDays))); }
    catch (e) { clear(linkHost).append(h('div', { class: 'notice warn' }, e.message)); }
  };
  const dayBtns = [['7 days', 7], ['30 days', 30], ['90 days', 90]].map(([label, v]) => { const b = h('button', { type: 'button', onclick: () => { linkDays = v; paintLinks(); } }, label); b.dataset.v = String(v); return b; });
  paintLinks();

  // ---- health ----
  const healthCards = health
    ? health.checks.map(c => h('div', { class: 'hcard' }, h('i', { class: 'hdot ' + (c.status === 'Healthy' ? 'ok' : 'bad') }),
      h('span', null, h('strong', null, c.name), c.detail ? h('small', null, c.detail) : null), badge(c.status, c.status === 'Healthy' ? 'ok' : 'bad')))
    : [h('div', { class: 'hcard' }, h('i', { class: 'hdot bad' }), h('span', null, h('strong', null, 'Server unreachable')))];

  const warn = limits.filter(l => l.status === 'warn' || l.status === 'full');
  const banner = warn.length
    ? h('div', { class: 'notice warn', role: 'status' }, 'Heads up: ' + warn.map(l => `${purpose(l.provider).name} (${l.metric}) is at ${l.percent}%`).join('; ') + '. The feature pauses at 100% and resumes after the reset.')
    : null;

  let timer = null, alive = true;
  const reload = () => { if (alive) { clear(main); usageView(main, ctx); } };
  if (rows.length) {
    timer = setInterval(() => {
      rows.forEach(r => r.tick()); if (live) nextReset.textContent = duration(live - Date.now());
      if (rows.some(r => r.resets - Date.now() <= 0)) { clearInterval(timer); reload(); }   // a cap has just renewed: reload the numbers
    }, 30000);
  }
  if (ctx && ctx.onCleanup) ctx.onCleanup(() => { alive = false; clearInterval(timer); });

  add(main, [
    h('div', { class: 'page-head split' }, h('div', null, h('h2', null, 'AI usage'),
      h('p', null, 'Every AI call is counted against a free daily allowance. When one runs out, that feature pauses instead of using a paid model.')),
      h('button', { class: 'btn', type: 'button', onclick: reload }, icon('refresh', 15), 'Refresh')),
    banner, kpis,
    h('section', { class: 'block' }, h('div', { class: 'block-head' }, h('h3', null, 'Daily allowances'), h('small', { class: 'muted' }, 'Shared by every company on this server · each renews by itself every day')),
      rows.length ? h('div', { class: 'gauges' }, rows.map(r => r.row)) : empty('No limits reported.'),
      hidden.length ? h('p', { class: 'muted small off-note' }, 'Not shown, switched off: ' + hidden.join(', ') + '. They appear here once a daily limit is set.') : null),
    h('section', { class: 'card' }, h('div', { class: 'block-head' }, h('h3', null, 'Each share link'), h('span', { class: 'choice mini' }, dayBtns)), linkHost),
    h('div', { class: 'two-col usage-cols' },
      h('section', { class: 'card' }, h('div', { class: 'block-head' }, h('h3', null, 'Your company'), h('span', { class: 'choice mini' }, btns)), tableHost),
      h('section', { class: 'card' }, h('div', { class: 'block-head' }, h('h3', null, 'System health')), h('div', { class: 'hgrid' }, healthCards)))]);
}
