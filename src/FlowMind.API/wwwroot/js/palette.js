import { h } from './dom.js';
import { icon } from './chatkit.js';

let open = null;

/** Ctrl/Cmd+K quick jump. items: [{ label, hint, icon, run }]. */
export function openPalette(items) {
  if (open) return;
  const input = h('input', { type: 'text', class: 'pal-input', placeholder: 'Jump to a page or run an action…', 'aria-label': 'Search commands', autocomplete: 'off' });
  const list = h('ul', { class: 'pal-list', role: 'listbox' });
  const dlg = h('dialog', { class: 'dialog palette', 'aria-label': 'Quick search' },
    h('div', { class: 'pal-top' }, icon('search', 18), input, h('kbd', null, 'Esc')), list);
  let shown = [], sel = 0;

  const paint = () => {
    list.replaceChildren(...shown.map((it, i) => h('li', { role: 'option', 'aria-selected': String(i === sel), class: i === sel ? 'on' : '' },
      h('button', { type: 'button', onmouseenter: () => { sel = i; mark(); }, onclick: () => go(it) },
        h('span', { class: 'pal-ic' }, icon(it.icon || 'chevron', 16)), h('span', { class: 'pal-label' }, it.label), it.hint ? h('small', null, it.hint) : null))));
    if (!shown.length) list.append(h('li', { class: 'pal-empty' }, 'Nothing matches.'));
  };
  const mark = () => [...list.children].forEach((li, i) => { li.classList.toggle('on', i === sel); li.setAttribute('aria-selected', String(i === sel)); });
  const filter = () => {
    const q = input.value.trim().toLowerCase();
    shown = items.filter(it => !q || (it.label + ' ' + (it.hint || '')).toLowerCase().includes(q)); sel = 0; paint();
  };
  const go = it => { dlg.close(); it.run(); };

  input.addEventListener('input', filter);
  input.addEventListener('keydown', e => {
    if (e.key === 'ArrowDown') { e.preventDefault(); sel = Math.min(sel + 1, shown.length - 1); mark(); list.children[sel]?.scrollIntoView({ block: 'nearest' }); }
    else if (e.key === 'ArrowUp') { e.preventDefault(); sel = Math.max(sel - 1, 0); mark(); list.children[sel]?.scrollIntoView({ block: 'nearest' }); }
    else if (e.key === 'Enter' && shown[sel]) { e.preventDefault(); go(shown[sel]); }
  });
  dlg.addEventListener('click', e => { if (e.target === dlg) dlg.close(); });
  dlg.addEventListener('close', () => { dlg.remove(); open = null; });
  open = dlg; document.body.append(dlg); filter(); dlg.showModal(); input.focus();
}
