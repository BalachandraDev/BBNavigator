import { h, clear, reviewBadge, citation, empty, badge } from '../dom.js';
import { api } from '../api.js';
import { icon } from '../chatkit.js';

export async function knowledgeView(main, ctx) {
  let tab = 'pages';
  let selected = null;       // page id or workflow id
  const tree = h('div');
  const detail = h('div');

  const tabBtn = (key, label) => h('button', { type: 'button', role: 'tab', 'aria-selected': String(tab === key), onclick: () => { tab = key; selected = null; paint(); } }, label);
  const tabs = h('div', { class: 'tabs', role: 'tablist' });

  async function paint() {
    clear(tabs).append(tabBtn('pages', 'Modules & pages'), tabBtn('workflows', 'Workflows'));
    finder.value = '';
    clear(detail).append(empty('Select an item', 'Choose a page or workflow on the left to see its details.', 'book'));
    clear(tree).append(h('p', { class: 'muted' }, 'Loading…'));
    try { tab === 'pages' ? await paintModules() : await paintWorkflows(); }
    catch (e) { clear(tree).append(h('div', { class: 'notice warn' }, e.message)); }
  }

  async function paintModules() {
    const { modules } = await api('GET', '/modules?applicationId=' + encodeURIComponent(ctx.appId));
    clear(tree);
    if (!modules.length) { tree.append(empty('No knowledge yet', 'It appears after a document has been analysed.', 'book')); return; }
    tree.append(h('ul', { class: 'tree' }, modules.map(m => h('li', null,
      h('span', { class: 'mod' }, m.name, ' ', m.review && m.review !== 'Approved' ? badge('draft', 'review') : null),
      m.pages.length ? h('ul', null, m.pages.map(p => h('li', null, h('button', {
        class: 'link', type: 'button', 'aria-current': String(selected === p.id),
        onclick: () => { selected = p.id; markSelected(); showPage(p.id); },
      }, p.name)))) : h('p', { class: 'muted small', style: 'margin-left:12px' }, 'No pages')))));
    // Auto-open the first page so the screen is never empty.
    const first = modules.flatMap(m => m.pages)[0];
    if (first) { selected = first.id; markSelected(); showPage(first.id); }
  }

  function markSelected() {
    tree.querySelectorAll('button.link').forEach(b => b.setAttribute('aria-current', 'false'));
  }

  async function showPage(id) {
    clear(detail).append(h('p', { class: 'muted' }, 'Loading…'));
    try {
      const p = await api('GET', '/pages/' + id);
      if (selected !== id) return;
      tree.querySelectorAll('button.link').forEach(b => b.setAttribute('aria-current', String(b.textContent === p.name)));
      const section = (title, rows) => rows.length ? h('div', { class: 'section' }, h('h3', null, title, h('span', { class: 'count' }, String(rows.length))), h('ul', { class: 'rowlist' }, rows)) : null;
      const goto = n => h('li', null, h('button', { class: 'btn-link', type: 'button', onclick: () => { selected = n.pageId; showPage(n.pageId); } }, n.pageName),
        n.condition ? h('span', { class: 'muted small' }, 'when ' + n.condition) : null, reviewBadge(n.review));
      clear(detail).append(h('section', { class: 'card' },
        h('div', { class: 'item-head' }, h('h2', null, p.name), reviewBadge(p.review), citation(p.source)),
        p.module ? h('p', { class: 'muted' }, 'Module: ' + p.module) : null,
        h('p', null, p.purpose || h('span', { class: 'muted' }, 'No description documented.')),
        section('Functions', p.functionalities.map(f => h('li', null, f.text, citation(f.source)))),
        section('Validations', p.validations.map(v => h('li', null, v.field ? h('strong', null, v.field + ': ') : null, v.rule, citation(v.source)))),
        section('Business rules', p.businessRules.map(r => h('li', null, r.text, citation(r.source)))),
        section('Roles & permissions', p.roles.map(r => h('li', null, h('strong', null, r.role + ': '), r.permission, citation(r.source)))),
        section('Comes from', p.previousPages.map(goto)),
        section('Leads to', p.nextPages.map(goto))));
    } catch (e) { clear(detail).append(h('div', { class: 'notice warn' }, e.message)); }
  }

  async function paintWorkflows() {
    const list = await api('GET', '/workflows?applicationId=' + encodeURIComponent(ctx.appId));
    clear(tree);
    if (!list.length) { tree.append(empty('No workflows yet', 'Workflows are found when a document describes a multi-step process.', 'book')); return; }
    // Grouped module by module (a workflow with no module goes last), the same way the Modules & pages tab is.
    const NONE = '(No module)';
    const groups = new Map();
    list.forEach(w => { const k = w.module || NONE; if (!groups.has(k)) groups.set(k, []); groups.get(k).push(w); });
    const keys = [...groups.keys()].sort((a, b) => (a === NONE) - (b === NONE) || a.localeCompare(b));
    const open = w => { selected = w.id; showWorkflow(w.id, w.name); tree.querySelectorAll('button.link').forEach(b => b.setAttribute('aria-current', String(b.dataset.id === w.id))); };
    tree.append(h('ul', { class: 'tree' }, keys.map(k => h('li', null,
      h('span', { class: 'mod' }, k, ' ', h('span', { class: 'muted small' }, `· ${groups.get(k).length} workflow${groups.get(k).length === 1 ? '' : 's'}`)),
      h('ul', null, groups.get(k).map(w => h('li', null, h('button', { class: 'link', type: 'button', 'data-id': w.id, onclick: () => open(w) },
        w.name, h('span', { class: 'muted small' }, ` · ${w.steps} steps`)))))))));
    open(groups.get(keys[0])[0]);
  }

  async function showWorkflow(id) {
    clear(detail).append(h('p', { class: 'muted' }, 'Loading…'));
    try {
      const w = await api('GET', '/workflows/' + id);
      clear(detail).append(h('section', { class: 'card' },
        h('div', { class: 'item-head' }, h('h2', null, w.name), reviewBadge(w.review), citation(w.source)),
        w.module ? h('p', { class: 'muted' }, 'Module: ' + w.module) : null,
        w.description ? h('p', null, w.description) : null,
        h('div', { class: 'section' }, h('h3', null, 'Steps'),
          w.steps.length ? h('ol', { class: 'flow' }, w.steps.map(s => h('li', null, h('strong', null, s.name),
            s.pageName ? h('div', { class: 'muted small' }, 'Page: ' + s.pageName) : null, s.description ? h('div', null, s.description) : null)))
            : h('p', { class: 'muted' }, 'No steps documented.')),
        w.transitions.length ? h('div', { class: 'section' }, h('h3', null, 'Transitions'),
          h('ul', { class: 'rowlist' }, w.transitions.map(t => h('li', null, h('strong', null, t.fromStep), ' → ', h('strong', null, t.toStep),
            t.condition ? h('span', { class: 'muted' }, ' when ' + t.condition) : null)))) : null));
    } catch (e) { clear(detail).append(h('div', { class: 'notice warn' }, e.message)); }
  }

  // Filter the left list as you type (module names, page names, workflow names).
  const finder = h('input', { type: 'search', class: 'search', placeholder: 'Filter this list', 'aria-label': 'Filter this list', oninput: e => applyFilter(e.target.value) });
  function applyFilter(text) {
    const q = text.trim().toLowerCase();
    tree.querySelectorAll('ul.tree > li').forEach(li => {
      const subs = [...li.querySelectorAll('ul li')];
      if (subs.length) {
        let any = false;
        subs.forEach(s => { const hit = !q || s.textContent.toLowerCase().includes(q); s.hidden = !hit; any = any || hit; });
        li.hidden = !!q && !any && !li.querySelector('.mod').textContent.toLowerCase().includes(q);
        if (q && li.querySelector('.mod').textContent.toLowerCase().includes(q)) subs.forEach(s => { s.hidden = false; });
      } else li.hidden = !!q && !li.textContent.toLowerCase().includes(q);
    });
  }

  main.append(
    h('div', { class: 'page-head' }, h('div', null, h('h2', null, 'Knowledge'),
      h('p', null, 'What BBNavigator found in your documents. Items marked "draft" have not been reviewed yet.'))),
    tabs,
    h('div', { class: 'grid-2' }, h('section', { class: 'card tree-card' }, h('label', { class: 'searchbox' }, icon('search', 16), finder), tree), detail));
  await paint();
}
