import { h, fill, badge, statusInfo, formatDate } from '../dom.js';
import { api } from '../api.js';
import { icon } from '../chatkit.js';

// Home for company admins: a command deck. What needs you (with the numbers big), how the assistant did over the last 7 days,
// quick actions, the document pipeline and how much of the free AI allowance is left today. Every row opens the place where you deal with it.

const reduced = () => { try { return matchMedia('(prefers-reduced-motion: reduce)').matches; } catch { return false; } };

/** Numbers count up once when the page opens (skipped when the person prefers reduced motion). */
function countUp(el, to, suffix = '') {
  if (reduced() || !Number.isFinite(to) || to < 2) { el.textContent = to + suffix; return; }
  const start = performance.now(), ms = 650;
  const tick = now => {
    const t = Math.min(1, (now - start) / ms), eased = 1 - Math.pow(1 - t, 3);
    el.textContent = Math.round(to * eased) + suffix;
    if (t < 1 && el.isConnected) requestAnimationFrame(tick); else el.textContent = to + suffix;
  };
  requestAnimationFrame(tick);
}
const num = (value, suffix = '') => { const s = h('span', null, value + suffix); if (typeof value === 'number') countUp(s, value, suffix); return s; };

const asDate = iso => new Date(String(iso).endsWith('Z') ? iso : iso + 'Z');
const duration = ms => { const m = Math.max(0, Math.round(ms / 60000)), hr = Math.floor(m / 60); return hr > 0 ? `${hr} h ${String(m % 60).padStart(2, '0')} min` : `${m} min`; };
const AI_NAMES = { gemini: 'Document analysis and Tamil', groq: 'Chat answers', cloudflare: 'Search', mistral: 'Scanned-PDF OCR' };

function skeleton() {
  const b = cls => h('div', { class: 'bx skel ' + cls }, h('i'), h('i'), h('i'));
  return h('div', { class: 'bento', 'aria-busy': 'true', 'aria-label': 'Loading' }, b('bx-need'), b('bx-pulse'), b('bx-quick'), b('bx-docs'), b('bx-ai'));
}

export async function overviewView(main, ctx) {
  const host = h('div', { class: 'skeleton-host' }, skeleton());
  main.append(host);

  const app = encodeURIComponent(ctx.appId);
  const [docs, links, gaps, support, week, usage] = await Promise.all([
    api('GET', '/documents?applicationId=' + app).catch(() => []),
    api('GET', '/bot-links').catch(() => []),
    api('GET', `/insights/gaps?applicationId=${app}&days=30`).catch(() => null),
    api('GET', '/support-requests/count').catch(() => null),
    api('GET', `/insights/usage?applicationId=${app}&days=7`).catch(() => null),
    api('GET', '/usage').catch(() => null),
  ]);
  const by = s => docs.filter(d => d.status === s).length;
  const approved = by('Approved'), review = by('PendingReview');
  const working = docs.filter(d => statusInfo(d.status).busy).length;
  const attention = docs.filter(d => ['Failed', 'AwaitingOcr', 'AwaitingAnalysis'].includes(d.status)).length;
  const activeLinks = links.filter(l => l.active);
  const today = links.reduce((n, l) => n + (l.todayMessages || 0), 0);
  const openGaps = gaps ? gaps.open : null, openSupport = support ? support.open : null;
  const waiting = (review || 0) + (openGaps || 0) + (openSupport || 0) + attention;

  const now = new Date(), hour = now.getHours();
  const hello = hour < 12 ? 'Good morning' : hour < 18 ? 'Good afternoon' : 'Good evening';
  const dateText = now.toLocaleDateString([], { weekday: 'long', day: 'numeric', month: 'long' });

  // ---- needs you ----
  const row = (value, label, sub, href) => h('a', { class: 'need-row' + (value > 0 ? ' hot' : ''), href },
    h('span', { class: 'need-n' }, value == null ? '–' : num(value)),
    h('span', { class: 'need-t' }, h('strong', null, label), h('small', null, sub)),
    icon('down', 16));
  const rows = [
    row(review, 'Documents to review', review ? 'Check what the AI found, then approve' : 'Nothing waiting', '#/review'),
    row(openGaps, 'Unanswered questions', openGaps ? 'People asked; the documents could not answer' : openGaps === 0 ? 'Everything asked was answered' : 'Open Insights', '#/insights?tab=gaps'),
    row(openSupport, 'Support requests', openSupport ? 'Visitors asked for a person' : openSupport === 0 ? 'No one is waiting' : 'Open Insights', '#/insights?tab=support'),
  ];
  if (attention) rows.push(row(attention, 'Documents need attention', 'Processing stopped or waits for a limit', '#/documents'));
  const needs = h('section', { class: 'bx bx-need' + (waiting ? ' hot' : '') },
    h('div', { class: 'bx-head' }, h('h3', { class: 'bx-title' }, 'Needs you'), waiting ? badge(`${waiting} open`, 'warn') : badge('All clear', 'ok')),
    h('div', { class: 'need-list' }, rows));

  // ---- the last 7 days ----
  let pulse;
  if (week) {
    const change = week.previousQuestions > 0 ? (() => { const p = Math.round(((week.questions - week.previousQuestions) / week.previousQuestions) * 100); return `${p >= 0 ? '+' : ''}${p}% vs the week before`; })() : 'Last 7 days';
    const peak = Math.max(1, ...week.daily.map(d => d.questions));
    const bars = h('div', { class: 'spark', role: 'img', 'aria-label': `Questions per day, last 7 days. Most in one day: ${peak}.` }, week.daily.map(d => {
      const col = h('div', { title: `${d.date}: ${d.questions} question${d.questions === 1 ? '' : 's'}${d.notDocumented ? ` (${d.notDocumented} not answered)` : ''}` });
      const miss = h('i', { class: 'bar-miss' }), ok = h('i', { class: 'bar-ok' });
      miss.style.height = (d.notDocumented / peak) * 100 + '%'; ok.style.height = ((d.questions - d.notDocumented) / peak) * 100 + '%';
      col.append(miss, ok); return col;
    }));
    const dayName = iso => new Date(iso + 'T12:00:00Z').toLocaleDateString([], { weekday: 'short', timeZone: 'UTC' }).slice(0, 2);
    const hours = (week.answered * 5) / 60;
    const ring = h('div', { class: 'ring-rate', role: 'img', 'aria-label': `${week.answerRatePercent}% answered from your documents` }, h('span', null, week.questions ? week.answerRatePercent + '%' : '–'));
    ring.style.setProperty('--p', String(week.questions ? week.answerRatePercent : 0));
    pulse = h('section', { class: 'bx bx-pulse' },
      h('div', { class: 'bx-head' }, h('h3', { class: 'bx-title' }, 'Last 7 days'), h('a', { class: 'btn btn-sm', href: '#/insights?tab=report' }, 'Full report')),
      h('div', { class: 'pulse-top' },
        h('div', null, h('div', { class: 'pulse-n' }, num(week.questions)), h('div', { class: 'pulse-sub' }, 'questions asked · ' + change)),
        h('div', { class: 'pulse-stats' },
          h('div', { class: 'pill-stat ring-stat' }, ring, h('span', null, 'answered from your documents')),
          h('div', { class: 'pill-stat' }, h('b', null, num(week.people)), h('span', null, 'visitors')),
          h('div', { class: 'pill-stat' }, h('b', null, hours >= 10 ? Math.round(hours) + ' h' : hours.toFixed(1) + ' h'), h('span', null, 'time saved (est.)')))),
      bars, h('div', { class: 'spark-days' }, week.daily.map(d => h('span', null, dayName(d.date)))));
  } else {
    pulse = h('section', { class: 'bx bx-pulse' }, h('div', { class: 'bx-head' }, h('h3', { class: 'bx-title' }, 'Last 7 days')), h('p', { class: 'muted' }, 'The report is not available right now.'));
  }

  // ---- quick actions ----
  const qa = (ic, label, href, main = false) => h('a', { class: 'qa' + (main ? ' main' : ''), href }, icon(ic, 20), label);
  const quick = h('section', { class: 'bx bx-quick' }, h('div', { class: 'bx-head' }, h('h3', { class: 'bx-title' }, 'Do next')),
    h('div', { class: 'qa-grid' }, qa('upload', 'Upload document', '#/documents', true), qa('plus', 'Add update', '#/documents'), qa('share', 'Share assistant', '#/share'), qa('chat', 'Ask the assistant', '#/chat')));

  // ---- documents ----
  const seg = (n, tone, label) => { const s = h('span', { class: 'seg seg-' + tone, title: label + ': ' + n }); s.style.flexGrow = String(n); return s; };
  const parts = [[approved, 'ok', 'Approved'], [review, 'review', 'In review'], [working, 'info', 'Processing'], [attention, 'bad', 'Needs attention']].filter(p => p[0] > 0);
  const recent = docs.slice(0, 3);
  const docsTile = h('section', { class: 'bx bx-docs' },
    h('div', { class: 'bx-head' }, h('h3', { class: 'bx-title' }, 'Documents'), h('a', { class: 'btn btn-sm', href: '#/documents' }, `${docs.length} total`)),
    parts.length ? h('div', { class: 'pipe-line', role: 'img', 'aria-label': 'Document status breakdown' }, parts.map(p => seg(...p))) : null,
    parts.length ? h('div', { class: 'pipe-legend' }, parts.map(([n, , label]) => h('span', null, h('b', null, String(n)), ' ' + label))) : null,
    recent.length ? h('div', { class: 'docs-rows' }, recent.map(d => { const st = statusInfo(d.status);
      return h('div', { class: 'drow' }, h('span', { class: 'rname', title: d.fileName || d.name }, d.fileName || d.name), badge(st.label, st.tone)); }))
      : h('p', { class: 'muted' }, 'Nothing uploaded yet. Upload a document to get started.'));

  // ---- the free AI allowance, today ----
  let aiTile;
  const limits = usage ? (usage.limits || []).filter(l => l.status !== 'off' && l.provider !== 'gemini-chat') : [];   // Tamil chat is a part of the Gemini total, not a separate allowance
  if (limits.length) {
    const best = {};
    for (const l of limits) if (!best[l.provider] || l.percent > best[l.provider].percent) best[l.provider] = l;
    const items = Object.values(best).sort((a, b) => b.percent - a.percent).slice(0, 4);
    const soonest = Math.min(...limits.map(l => asDate(l.resetsAtUtc).getTime()));
    const worst = items[0];
    aiTile = h('section', { class: 'bx bx-ai' },
      h('div', { class: 'bx-head' }, h('h3', { class: 'bx-title' }, 'AI allowance today'), h('a', { class: 'btn btn-sm', href: '#/usage' }, 'Details')),
      h('div', { class: 'ai-list' }, items.map(l => {
        const fill = h('i', { class: 'ai-' + l.status }); fill.style.width = Math.max(2, Math.min(100, l.percent)) + '%';
        return h('div', { class: 'ai-row' }, h('div', { class: 'ai-top' }, h('span', null, AI_NAMES[l.provider] || l.provider), h('b', null, Math.round(l.percent) + '%')), h('div', { class: 'ai-bar' }, fill));
      })),
      h('p', { class: 'ai-foot' }, worst.status === 'full' ? 'A limit is reached; that feature pauses until it renews. Nothing paid is used.' : `Free tier only. The next limit renews in ${duration(soonest - Date.now())}.`));
  } else {
    aiTile = h('section', { class: 'bx bx-ai' }, h('div', { class: 'bx-head' }, h('h3', { class: 'bx-title' }, 'AI allowance today')), h('p', { class: 'muted' }, 'Usage is not available right now.'));
  }

  fill(host,
    h('div', { class: 'deck-hello' },
      h('div', null, h('small', { class: 'deck-date' }, dateText),
        h('h2', null, hello + '. ', h('span', null, waiting ? `${waiting} thing${waiting === 1 ? '' : 's'} need${waiting === 1 ? 's' : ''} you.` : docs.length ? 'You are all caught up.' : 'Let us get started.'))),
      h('span', { class: 'chip' }, (ctx.app ? ctx.app.name : 'Workspace') + (activeLinks.length ? ` · ${today} question${today === 1 ? '' : 's'} today` : ''))),
    working ? h('div', { class: 'notice' }, `${working} document${working > 1 ? 's are' : ' is'} being processed right now.`) : null,
    h('div', { class: 'bento' }, needs, pulse, quick, docsTile, aiTile));
}
