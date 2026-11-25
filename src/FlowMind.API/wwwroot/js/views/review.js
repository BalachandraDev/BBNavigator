import { h, clear, fill, badge, reviewBadge, citation, toast, empty } from '../dom.js';
import { api } from '../api.js';

const TYPE_LABEL = {
  module: 'Modules', page: 'Pages', functionality: 'Functions', validation: 'Validations',
  businessrule: 'Business rules', role: 'Roles & permissions', workflow: 'Workflows',
};
const NAME_EDITABLE = ['module', 'page', 'workflow', 'role'];
const ORDER = Object.keys(TYPE_LABEL);

export async function reviewView(main, ctx) {
  const q = ctx.routeQuery();
  let status = 'Pending';
  let documentId = q.get('documentId') || '';
  const docs = await api('GET', '/documents?applicationId=' + encodeURIComponent(ctx.appId));
  const reviewable = docs.filter(d => ['PendingReview', 'Approved'].includes(d.status));
  if (documentId && !docs.some(d => d.id === documentId)) documentId = '';

  const listHost = h('div');
  const actionsHost = h('div', { class: 'actions' });

  const statusSel = h('select', { id: 'rstatus', onchange: e => { status = e.target.value; load(); } },
    ['Pending', 'Approved', 'Rejected'].map(s => h('option', { value: s, selected: s === status }, s === 'Pending' ? 'Unreviewed' : s)));
  const docSel = h('select', { id: 'rdoc', onchange: e => { documentId = e.target.value; load(); } },
    h('option', { value: '' }, 'All documents'),
    docs.map(d => h('option', { value: d.id, selected: d.id === documentId }, d.fileName)));

  async function load() {
    clear(listHost).append(h('p', { class: 'muted' }, 'Loading…'));
    drawActions();
    let items;
    try {
      items = await api('GET', `/review?applicationId=${encodeURIComponent(ctx.appId)}&status=${status}` + (documentId ? '&documentId=' + documentId : ''));
    } catch (e) { clear(listHost).append(h('div', { class: 'notice warn' }, e.message)); return; }
    draw(items);
  }

  function drawActions() {
    clear(actionsHost);
    const doc = docs.find(d => d.id === documentId);
    if (doc && doc.status === 'PendingReview') {
      actionsHost.append(h('button', { class: 'btn btn-primary', type: 'button', onclick: () => approveDocument(doc) }, 'Approve everything in this document'));
    }
  }

  async function approveDocument(doc) {
    try {
      const r = await api('POST', `/documents/${doc.id}/approve`);
      doc.status = 'Approved';
      toast(`Approved ${r.approved} items. "${doc.fileName}" is now being indexed for chat.`, 'ok');
      load();
    } catch (e) { toast(e.message, 'bad'); }
  }

  function draw(items) {
    clear(listHost);
    if (!items.length) {
      listHost.append(empty(status === 'Pending' ? 'Nothing waiting for review' : 'Nothing here',
        status === 'Pending' ? 'New items appear after a document has been analysed.' : '', status === 'Pending' ? 'check' : 'info'));
      return;
    }
    const groups = new Map();
    items.forEach(i => { if (!groups.has(i.type)) groups.set(i.type, []); groups.get(i.type).push(i); });
    [...groups.keys()].sort((a, b) => ORDER.indexOf(a) - ORDER.indexOf(b)).forEach(type => {
      listHost.append(h('h3', { class: 'group-title' }, TYPE_LABEL[type] || type, badge(String(groups.get(type).length), 'info')));
      groups.get(type).forEach(item => listHost.append(itemCard(item)));
    });
  }

  function itemCard(item) {
    const card = h('div', { class: 'item' });
    const body = h('div');
    const textView = () => h('p', { class: 'item-text' }, item.text || '—');

    function view() {
      fill(body, textView());
      fill(head, h('strong', null, item.title), reviewBadge(item.review), citation(item.source));
      fill(foot,
        item.review !== 'Approved' ? btn('Approve', () => decide('Approved'), 'btn-primary') : null,
        item.review !== 'Rejected' ? btn('Reject', () => decide('Rejected')) : null,
        item.review !== 'Pending' ? btn('Mark unreviewed', () => decide('Pending')) : null,
        btn('Edit', edit));
    }

    function edit() {
      const name = NAME_EDITABLE.includes(item.type) ? h('input', { type: 'text', maxlength: 200, value: item.title }) : null;
      const text = h('textarea', { maxlength: 2000, 'aria-label': 'Text' }, item.text || '');
      fill(body, name ? h('div', { class: 'field' }, h('label', null, 'Name'), name) : null, h('div', { class: 'field' }, h('label', null, 'Text'), text));
      fill(foot,
        btn('Save and approve', async () => {
          await decide('Approved', { name: name ? name.value.trim() : undefined, text: text.value.trim() || undefined });
        }, 'btn-primary'),
        btn('Cancel', view));
    }

    async function decide(newStatus, edits = {}) {
      try {
        await api('PUT', `/review/${item.type}/${item.id}`, { body: { status: newStatus, ...edits } });
        if (newStatus !== status) { card.remove(); toast(newStatus === 'Approved' ? 'Approved.' : newStatus === 'Rejected' ? 'Rejected.' : 'Marked unreviewed.', 'ok'); return; }
        item.review = newStatus; if (edits.name) item.title = edits.name; if (edits.text) item.text = edits.text; view();
      } catch (e) { toast(e.message, 'bad'); }
    }
    const btn = (label, fn, cls = '') => h('button', { class: 'btn btn-sm ' + cls, type: 'button', onclick: fn }, label);

    const head = h('div', { class: 'item-head' });
    const foot = h('div', { class: 'actions' });
    card.append(head, body, foot);
    view();
    return card;
  }

  main.append(
    h('div', { class: 'page-head' }, h('div', null, h('h2', null, 'Review'),
      h('p', null, 'AI-extracted knowledge is a draft. Correct anything that is wrong, then approve. Wrong or invented items should be rejected.'))),
    h('div', { class: 'notice' }, 'Chat answers come from documents you have approved, and always show the source document and page. Approving a document queues it for search.'),
    h('section', { class: 'card' },
      h('div', { class: 'toolbar' },
        h('div', { class: 'field' }, h('label', { for: 'rdoc' }, 'Document'), docSel),
        h('div', { class: 'field' }, h('label', { for: 'rstatus' }, 'Show'), statusSel), actionsHost),
      listHost));
  void reviewable;
  await load();
}
