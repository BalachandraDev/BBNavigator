import { h, fill, badge, toast, formatDate, empty, confirmDialog, closeOnOutside } from '../dom.js';
import { api } from '../api.js';
import { icon } from '../chatkit.js';

// Insights (company admins): what people asked that the documents could not answer, who asked for a person's help,
// and a one-page report of what the assistant did over a period. Everything is read from the chats already stored.
// Questions arrive with e-mail addresses and long numbers already hidden by the server, and never say who asked.

const TABS = [['gaps', 'Unanswered'], ['support', 'Support'], ['report', 'Report']];
const PERIODS = [['7 days', 7], ['30 days', 30], ['90 days', 90]];
const MINUTES_KEY = 'flowmind.minutesPerAnswer';
const n = v => Number(v || 0).toLocaleString();
const day = iso => new Date(iso + 'T12:00:00Z').toLocaleDateString([], { day: 'numeric', month: 'short', timeZone: 'UTC' });
const stamp = iso => new Date(/[zZ]$/.test(iso) ? iso : iso + 'Z').toLocaleDateString([], { day: 'numeric', month: 'short', year: 'numeric' });

export async function insightsView(main, ctx) {
  let alive = true;
  ctx.onCleanup(() => { alive = false; });
  const q = ctx.routeQuery();
  let tab = TABS.some(([k]) => k === q.get('tab')) ? q.get('tab') : 'gaps';
  let days = 30;
  let gapState = 'open', supportState = 'open';
  const counts = { gaps: null, support: null };
  let docsCache = null;

  const body = h('div', { class: 'insights-body' });
  const tabBar = h('div', { class: 'tabs no-print', role: 'tablist', 'aria-label': 'Insights' });

  function paintTabs() {
    fill(tabBar, TABS.map(([k, label]) => {
      const c = counts[k];
      return h('button', { type: 'button', role: 'tab', 'aria-selected': String(k === tab), onclick: () => show(k) },
        label, c ? h('b', { class: 'tab-count' }, String(c)) : null);
    }));
  }
  function show(t) {
    tab = t; paintTabs();
    try { history.replaceState(null, '', '#/insights?tab=' + t); } catch { /* the address is only a convenience */ }
    load();
  }
  async function load() {
    const mine = tab;
    fill(body, h('p', { class: 'muted', role: 'status' }, 'Loading…'));
    try {
      const view = mine === 'gaps' ? await gapsView() : mine === 'support' ? await supportView() : await reportView();
      if (alive && tab === mine) fill(body, view);
    } catch (e) { if (alive && tab === mine) fill(body, h('div', { class: 'notice warn', role: 'alert' }, e.message || 'Could not load this page.')); }
  }

  const period = onPick => h('div', { class: 'choice mini no-print', role: 'group', 'aria-label': 'Period' }, PERIODS.map(([label, v]) =>
    h('button', { type: 'button', 'aria-pressed': String(days === v), onclick: () => { days = v; onPick(); } }, label)));

  // ---- documents (to add the missing information to) ----
  async function docsList() { if (!docsCache) docsCache = await api('GET', '/documents?applicationId=' + encodeURIComponent(ctx.appId)); return docsCache; }

  /** Open the "Add update" dialog of a document, with the question as the update's heading. The closest document is preselected. */
  async function fixIt({ question, key, closest }) {
    let docs;
    try { docs = (await docsList()).filter(d => d.status === 'Approved' || d.status === 'PendingReview'); } catch (e) { toast(e.message, 'bad'); return; }
    if (!docs.length) { toast('Upload a document and approve it first. Then you can add updates to it.', 'info'); return; }
    const go = d => ctx.navigate('documents', { addUpdate: d.id, title: question.slice(0, 120), question, ...(key ? { gap: key } : {}) });
    if (docs.length === 1) { go(docs[0]); return; }
    const near = closest && closest.length ? docs.find(d => d.fileName === closest[0].document) : null;
    const select = h('select', { id: 'fix-doc' }, docs.map(d => h('option', { value: d.id, selected: near && d.id === near.id }, d.fileName)));
    const dlg = h('dialog', { class: 'dialog', 'aria-label': 'Choose a document' },
      h('div', { class: 'dlg-head' }, h('h2', null, 'Add to which document?'),
        h('button', { class: 'btn btn-sm icon-btn', type: 'button', 'aria-label': 'Close', title: 'Close', onclick: () => dlg.close() }, icon('close', 16))),
      h('p', { class: 'muted' }, near ? 'The closest match is selected.' : 'No document looked close to this question. Choose where the answer belongs.'),
      h('div', { class: 'field' }, h('label', { for: 'fix-doc' }, 'Document'), select),
      h('div', { class: 'actions' }, h('button', { class: 'btn', type: 'button', onclick: () => dlg.close() }, 'Cancel'),
        h('button', { class: 'btn btn-primary', type: 'button', onclick: () => { const d = docs.find(x => x.id === select.value); dlg.close(); if (d) go(d); } }, 'Continue')));
    dlg.addEventListener('close', () => dlg.remove());
    closeOnOutside(dlg);
    document.body.append(dlg); dlg.showModal();
  }

  // ---- 1. unanswered questions ----
  async function gapsView() {
    const r = await api('GET', `/insights/gaps?applicationId=${encodeURIComponent(ctx.appId)}&days=${days}`);
    counts.gaps = r.open; paintTabs();
    const shown = r.items.filter(i => i.state === gapState);

    async function setState(item, status) {
      try { await api('POST', '/insights/gaps/state', { body: { applicationId: ctx.appId, key: item.key, status } }); await load(); }
      catch (e) { toast(e.message, 'bad'); }
    }
    const card = i => h('article', { class: 'gap-card' },
      h('div', { class: 'gap-main' },
        h('strong', { class: 'gap-q' }, i.question),
        h('div', { class: 'gap-meta' },
          badge(`Asked ${i.count}×`, i.count > 1 ? 'warn' : 'info'),
          i.reopened ? badge('Asked again', 'bad') : null,
          h('span', { class: 'muted small' }, `Last: ${formatDate(i.lastAskedUtc)} · ${i.channels.join(', ')}`)),
        i.closest.length
          ? h('div', { class: 'gap-close' }, h('span', { class: 'muted small' }, 'Closest in your documents:'),
            i.closest.map(c => h('span', { class: 'chip' }, `${c.document} · p.${c.page}`)))
          : h('p', { class: 'muted small' }, 'Nothing close in your documents. This may be a new topic, or not something the documents should cover.')),
      h('div', { class: 'gap-actions' }, i.state === 'open'
        ? [h('button', { class: 'btn btn-sm btn-primary', type: 'button', onclick: () => fixIt(i) }, icon('plus', 14), 'Add update'),
          h('button', { class: 'btn btn-sm', type: 'button', onclick: () => setState(i, 'handled') }, icon('check', 14), 'Mark handled'),
          h('button', { class: 'btn btn-sm', type: 'button', title: 'Not something the documents should answer', onclick: () => setState(i, 'ignored') }, 'Ignore')]
        : h('button', { class: 'btn btn-sm', type: 'button', onclick: () => setState(i, 'open') }, icon('refresh', 14), 'Reopen')));

    const stateBtn = (k, label, c) => h('button', { type: 'button', 'aria-pressed': String(gapState === k), onclick: () => { gapState = k; load(); } }, `${label} (${c})`);
    return h('div', null,
      h('div', { class: 'notice' }, 'Questions people asked that your approved documents could not answer, with the most asked first. Add the missing information as an update to a document, and the assistant can answer it next time. Only the question is shown, never who asked; phone numbers and e-mail addresses are hidden.'),
      h('div', { class: 'insights-bar' },
        h('div', { class: 'choice mini', role: 'group', 'aria-label': 'Show' }, stateBtn('open', 'To fix', r.open), stateBtn('handled', 'Handled', r.handled), stateBtn('ignored', 'Ignored', r.ignored)),
        period(load)),
      shown.length
        ? h('div', { class: 'gap-list' }, shown.map(card))
        : empty(gapState === 'open' ? 'Nothing to fix' : 'Nothing here',
          gapState === 'open' ? `In the last ${days} days, every question was answered from your documents. When someone asks something they do not cover, it appears here.` : 'Questions you mark as ' + gapState + ' are kept here.', gapState === 'open' ? 'check' : 'info'));
  }

  // ---- 2. support requests ----
  async function supportView() {
    const r = await api('GET', `/support-requests?applicationId=${encodeURIComponent(ctx.appId)}&status=${supportState}`);
    counts.support = r.openTotal; paintTabs();

    async function patch(item, body) {
      try { await api('PATCH', '/support-requests/' + item.id, { body }); await load(); } catch (e) { toast(e.message, 'bad'); }
    }
    function closeDialog(item) {
      const note = h('textarea', { id: 'sup-res', rows: 3, maxlength: 500, placeholder: 'e.g. Called the visitor and explained the steps' });
      const dlg = h('dialog', { class: 'dialog', 'aria-label': 'Mark as done' },
        h('div', { class: 'dlg-head' }, h('h2', null, 'Mark as done'),
          h('button', { class: 'btn btn-sm icon-btn', type: 'button', 'aria-label': 'Close', title: 'Close', onclick: () => dlg.close() }, icon('close', 16))),
        h('p', { class: 'muted' }, item.question),
        h('div', { class: 'field' }, h('label', { for: 'sup-res' }, 'How was it handled? (optional, only you see this)'), note),
        h('div', { class: 'actions' }, h('button', { class: 'btn', type: 'button', onclick: () => dlg.close() }, 'Cancel'),
          h('button', { class: 'btn btn-primary', type: 'button', onclick: () => { dlg.close(); patch(item, { status: 'done', resolution: note.value }); } }, 'Mark done')));
      dlg.addEventListener('close', () => dlg.remove());
      closeOnOutside(dlg);
      document.body.append(dlg); dlg.showModal(); note.focus();
    }
    async function remove(item) {
      if (!await confirmDialog('Delete this request? This cannot be undone.')) return;
      try { await api('DELETE', '/support-requests/' + item.id); await load(); } catch (e) { toast(e.message, 'bad'); }
    }

    const card = i => h('article', { class: 'gap-card' },
      h('div', { class: 'gap-main' },
        h('strong', { class: 'gap-q' }, i.question),
        h('div', { class: 'gap-meta' },
          badge(i.status === 'open' ? 'Waiting' : 'Done', i.status === 'open' ? 'warn' : 'ok'),
          badge(i.wasAnswered ? 'Had an answer, wanted a person' : 'Had no answer', i.wasAnswered ? 'info' : 'review'),
          h('span', { class: 'muted small' }, `${formatDate(i.createdUtc)} · ${i.channel}`)),
        i.note ? h('p', { class: 'sup-quote' }, h('span', { class: 'muted small' }, 'What they were trying to do: '), i.note) : null,
        i.contact ? h('p', { class: 'sup-quote' }, h('span', { class: 'muted small' }, 'Contact: '), h('strong', null, i.contact)) : h('p', { class: 'muted small' }, 'No contact left. The sender did not say how to reach them.'),
        i.resolution ? h('p', { class: 'sup-quote' }, h('span', { class: 'muted small' }, 'Your note: '), i.resolution) : null),
      h('div', { class: 'gap-actions' },
        i.status === 'open' ? h('button', { class: 'btn btn-sm btn-primary', type: 'button', onclick: () => closeDialog(i) }, icon('check', 14), 'Mark done')
          : h('button', { class: 'btn btn-sm', type: 'button', onclick: () => patch(i, { status: 'open' }) }, icon('refresh', 14), 'Reopen'),
        h('button', { class: 'btn btn-sm', type: 'button', onclick: () => fixIt({ question: i.question, key: null, closest: [] }) }, icon('plus', 14), 'Add update'),
        h('button', { class: 'btn btn-sm btn-danger', type: 'button', onclick: () => remove(i) }, icon('trash', 14), 'Delete')));

    const stateBtn = (k, label) => h('button', { type: 'button', 'aria-pressed': String(supportState === k), onclick: () => { supportState = k; load(); } }, label);
    return h('div', null,
      h('div', { class: 'notice' }, 'When the assistant cannot answer, visitors can send their question to your team. Requests are not e-mailed anywhere: they wait here, so check this page regularly. Contact details are whatever the sender chose to type.'),
      h('div', { class: 'insights-bar' }, h('div', { class: 'choice mini', role: 'group', 'aria-label': 'Show' }, stateBtn('open', 'Waiting'), stateBtn('done', 'Done'), stateBtn('all', 'All'))),
      r.items.length ? h('div', { class: 'gap-list' }, r.items.map(card))
        : empty(supportState === 'open' ? 'No requests waiting' : 'No requests here', 'Visitors see "Send this to our team" under an answer the assistant could not give. You can switch that off for a share link under its design, Behaviour.', supportState === 'open' ? 'check' : 'send'));
  }

  // ---- 3. usage report ----
  async function reportView() {
    const r = await api('GET', `/insights/usage?applicationId=${encodeURIComponent(ctx.appId)}&days=${days}`);
    let minutes = 5;
    try { const v = Number(localStorage.getItem(MINUTES_KEY)); if (v >= 1 && v <= 120) minutes = v; } catch { /* default */ }

    const saved = h('b', null, '');
    const savedNote = h('small', null, '');
    const paintSaved = () => {
      const hours = (r.answered * minutes) / 60;
      saved.textContent = hours >= 10 ? Math.round(hours) + ' h' : hours.toFixed(1) + ' h';
      savedNote.textContent = `Estimate: ${n(r.answered)} answer${r.answered === 1 ? '' : 's'} × ${minutes} min each`;
    };
    paintSaved();
    const mins = h('input', {
      type: 'number', min: 1, max: 120, value: minutes, id: 'mins', 'aria-label': 'Minutes saved per answer',
      oninput: e => { minutes = Math.min(120, Math.max(1, Number(e.target.value) || 5)); try { localStorage.setItem(MINUTES_KEY, String(minutes)); } catch { /* not remembered */ } paintSaved(); },
    });

    const change = r.previousQuestions > 0
      ? (() => { const p = Math.round(((r.questions - r.previousQuestions) / r.previousQuestions) * 100); return `${p >= 0 ? '+' : ''}${p}% vs the ${days} days before`; })()
      : 'No earlier period to compare';
    const kpi = (ic, tone, value, label, hint) => h('div', { class: 'kpi tone-' + tone }, h('span', { class: 'kpi-ic' }, icon(ic, 20)),
      h('div', null, value, h('span', null, label), hint ? (typeof hint === 'string' ? h('small', null, hint) : hint) : null));

    // questions per day: answered (blue) + not answered (amber), stacked
    const peak = Math.max(1, ...r.daily.map(d => d.questions));
    const bars = h('div', { class: 'daybars', role: 'img', 'aria-label': `Questions per day over the last ${days} days. Most in one day: ${peak}.` },
      r.daily.map(d => {
        const col = h('div', { title: `${day(d.date)}: ${d.questions} question${d.questions === 1 ? '' : 's'}${d.notDocumented ? ` (${d.notDocumented} not answered)` : ''}` });
        const miss = h('i', { class: 'bar-miss' }), ok = h('i', { class: 'bar-ok' });
        miss.style.height = (d.notDocumented / peak) * 100 + '%';
        ok.style.height = ((d.questions - d.notDocumented) / peak) * 100 + '%';
        col.append(miss, ok);
        return col;
      }));
    const topMax = Math.max(1, ...r.topQuestions.map(t => t.count));
    const chMax = Math.max(1, ...r.channels.map(c => c.questions));
    const meter = (v, max) => { const f = h('i'); f.style.width = (v / max) * 100 + '%'; return h('span', { class: 'meter' }, f); };

    return h('div', { class: 'report' },
      h('div', { class: 'insights-bar no-print' }, period(load),
        h('label', { class: 'mins-label', for: 'mins' }, 'Minutes saved per answer', mins),
        h('button', { class: 'btn btn-sm', type: 'button', onclick: () => window.print() }, 'Print or save as PDF')),
      h('header', { class: 'report-head' }, h('h3', null, `${r.application}: assistant report`),
        h('p', { class: 'muted' }, `${stamp(r.fromUtc)} to ${stamp(r.toUtc)} (last ${r.days} days)`)),
      h('div', { class: 'kpis' },
        kpi('chat', 'brand', h('b', null, n(r.questions)), 'Questions asked', change),
        kpi('check', r.answerRatePercent >= 80 ? 'ok' : r.answerRatePercent >= 60 ? 'warn' : 'bad', h('b', null, r.questions ? r.answerRatePercent + '%' : '–'), 'Answered from your documents', `${n(r.notDocumented)} could not be answered`),
        kpi('user', 'info', h('b', null, n(r.people)), 'Different visitors', `${n(r.activeDays)} of ${r.days} days had questions · ${n(r.conversations)} chats`),
        kpi('bolt', 'review', saved, 'Time saved (estimate)', savedNote)),
      h('section', { class: 'card' }, h('h3', null, 'Questions per day'), bars,
        h('div', { class: 'daybars-axis' }, h('span', null, r.daily.length ? day(r.daily[0].date) : ''), h('span', { class: 'legend' }, h('i', { class: 'bar-ok' }), 'answered', h('i', { class: 'bar-miss' }), 'not answered'), h('span', null, r.daily.length ? day(r.daily[r.daily.length - 1].date) : ''))),
      h('div', { class: 'two-col' },
        h('section', { class: 'card' }, h('h3', null, 'Most asked questions'),
          r.topQuestions.length ? h('ol', { class: 'rank' }, r.topQuestions.map(t => h('li', null,
            h('span', { class: 'rank-q' }, t.question, t.allAnswered ? null : badge('not fully answered', 'warn')), meter(t.count, topMax), h('b', null, `${t.count}×`))))
            : empty('No questions yet', 'They appear here as people use the assistant.', 'chat')),
        h('section', { class: 'card' }, h('h3', null, 'Where people ask from'),
          r.channels.length ? h('ul', { class: 'rank' }, r.channels.map(c => h('li', null, h('span', { class: 'rank-q' }, c.name), meter(c.questions, chMax), h('b', null, n(c.questions)))))
            : empty('Nothing yet', null, 'chat'),
          r.tamilQuestions ? h('p', { class: 'muted small' }, `${n(r.tamilQuestions)} question${r.tamilQuestions === 1 ? ' was' : 's were'} asked in Tamil.`) : null)),
      h('section', { class: 'card' }, h('h3', null, 'Keeping the documentation current'),
        h('ul', { class: 'facts' },
          h('li', null, h('b', null, n(r.documentsChanged)), ` document upload${r.documentsChanged === 1 ? '' : 's'} or update${r.documentsChanged === 1 ? '' : 's'} in this period`),
          h('li', null, h('b', null, n(r.openGaps)), ` unanswered question${r.openGaps === 1 ? '' : 's'} still to fix`),
          h('li', null, h('b', null, n(r.supportReceived)), ` request${r.supportReceived === 1 ? '' : 's'} sent to your team, `, h('b', null, n(r.supportClosed)), ' closed, ', h('b', null, n(r.supportOpenNow)), ' waiting now'))),
      h('p', { class: 'muted small report-foot' }, 'Counted from the chats still stored: a chat that someone deletes is no longer counted. Greetings and thanks are not counted as questions, but off-topic questions are, and count as not answered. A visitor is one browser or signed-in user. Days are in UTC. Time saved is an estimate from the figure you enter above.',
        r.truncated ? ' This period had more questions than can be analysed at once, so the oldest are left out.' : ''));
  }

  main.append(
    h('div', { class: 'page-head no-print' }, h('div', null, h('h2', null, 'Insights'),
      h('p', null, 'What people ask ', h('strong', null, ctx.app?.name || 'this application'), ', where the documents fall short, and what the assistant delivers.'))),
    tabBar, body);
  paintTabs();
  // The tab counts are shown before a tab is opened, so a waiting request is visible at a glance.
  api('GET', '/support-requests/count').then(c => { counts.support = c.open; if (alive) paintTabs(); }).catch(() => { /* counts are a convenience */ });
  await load();
}
