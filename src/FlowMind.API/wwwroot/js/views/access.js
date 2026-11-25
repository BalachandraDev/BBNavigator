import { h, clear, fill, toast, formatDate, empty, badge, confirmDialog } from '../dom.js';
import { api } from '../api.js';
import { icon, copyText } from '../chatkit.js';

// Company admins: issue keys for ordinary users (chat + approved knowledge only) and revoke them.
// A key can belong to an audience (Manager, Nurse, Ward ...): it then sees only what that audience's rules open (set on the Access rules page).
export async function accessView(main) {
  const listHost = h('div');
  const result = h('div');
  const name = h('input', { type: 'text', id: 'kname', maxlength: 200, required: true, placeholder: 'e.g. Support team, or Priya (QA)' });
  const err = h('p', { class: 'error-text', role: 'alert' });
  let audiences = [];
  try { audiences = await api('GET', '/access/audiences'); } catch { /* keys still work without audiences */ }
  const audSelect = h('select', { id: 'kaud' }, h('option', { value: '' }, 'Full access (everything approved)'), audiences.map(a => h('option', { value: a.id }, a.name)));

  const audienceCell = k => {
    if (!k.active) return h('span', { class: 'muted' }, k.audienceName || 'Full access');
    const sel = h('select', { class: 'sel-sm', 'aria-label': `Audience for ${k.name}`, onchange: async () => {
      const v = sel.value;
      try {
        await api('PATCH', '/access-keys/' + k.id, { body: v ? { audienceId: v } : { fullAccess: true } });
        toast(v ? 'Key moved. It takes effect within about 15 seconds.' : 'Key now has full access. It takes effect within about 15 seconds.', 'ok');
        await load();
      } catch (e) { toast(e.message, 'bad'); sel.value = k.audienceId || ''; }
    } }, h('option', { value: '' }, 'Full access'), audiences.map(a => h('option', { value: a.id }, a.name)));
    sel.value = k.audienceId || '';
    return sel;
  };

  async function load() {
    let keys;
    try { keys = await api('GET', '/access-keys'); } catch (e) { fill(listHost, h('div', { class: 'notice warn' }, e.message)); return; }
    clear(listHost);
    if (!keys.length) { listHost.append(empty('No user keys yet', 'Create one above and give it to the person or team.', 'key')); return; }
    listHost.append(h('div', { class: 'table-wrap' }, h('table', { class: 'table stack' },
      h('thead', null, h('tr', null, ['Name', 'Audience', 'Key', 'Status', 'Created', 'Last used', ''].map(t => h('th', null, t)))),
      h('tbody', null, keys.map(k => h('tr', null,
        h('td', { 'data-label': 'Name' }, h('strong', null, k.name)),
        h('td', { 'data-label': 'Audience' }, audienceCell(k)),
        h('td', { 'data-label': 'Key' }, h('div', { class: 'key-cell' }, h('code', null, k.prefix + '…'), copyButton(k))),
        h('td', { 'data-label': 'Status' }, k.active ? badge('Active', 'ok') : badge('Revoked', 'bad')),
        h('td', { 'data-label': 'Created' }, formatDate(k.createdUtc)),
        h('td', { 'data-label': 'Last used' }, k.lastUsedUtc ? formatDate(k.lastUsedUtc) : 'Never'),
        h('td', { 'data-label': 'Actions', class: 'key-actions' }, k.active ? h('div', { class: 'key-btns' },
          h('button', { class: 'btn btn-sm', type: 'button', title: 'For a lost or leaked key: makes a new key with the same name and audience and stops the old one', onclick: () => replace(k) }, icon('refresh', 14), 'Replace'),
          h('button', { class: 'btn btn-sm btn-danger', type: 'button', onclick: () => revoke(k) }, 'Revoke')) : null)))))));
  }

  async function replace(k) {
    if (!await confirmDialog(`Replace the key for "${k.name}"? A new key with the same name and audience is created, and the old key stops working within seconds. Send the new key to the person.`, 'Replace key')) return;
    try {
      const n = await api('POST', `/access-keys/${k.id}/replace`);
      fill(result, keyNotice('New key for ' + n.name + (n.audienceName ? ' - ' + n.audienceName : ''), n.key), h('p', { class: 'muted key-hint' }, 'The old key no longer works. You can copy this one again later from the Keys list.'));
      result.scrollIntoView({ behavior: 'smooth', block: 'center' });
      await load();
    } catch (e) { toast(e.message, 'bad'); }
  }

  // Copy a key again later. Keys made before this existed were never stored, so they cannot be shown: replace them with a new key.
  function copyButton(k) {
    if (!k.active) return null;
    if (!k.canCopy) return h('span', { class: 'muted key-old', title: 'This key was made before copying was possible and cannot be shown again. Create a new key and revoke this one.' }, 'cannot be shown again');
    return h('button', { class: 'btn btn-sm', type: 'button', onclick: async () => {
      try {
        const r = await api('GET', `/access-keys/${k.id}/key`);
        if (await copyText(r.key)) toast(`Key for "${k.name}" copied.`, 'ok');
        else { showKey(k.name, r.key); toast('Could not copy automatically. The key is shown above: select it and copy.', 'info'); }
      } catch (e) { toast(e.message, 'bad'); }
    } }, icon('copy', 14), 'Copy key');
  }

  // The key in a box that selects in one click, with a Copy button that also works on plain http addresses.
  function keyNotice(title, key) {
    return h('div', { class: 'notice warn', role: 'status' },
      h('strong', null, title),
      h('div', { class: 'keybox' }, key),
      h('div', { class: 'actions', style: 'margin-top:10px' }, h('button', { class: 'btn btn-sm', type: 'button', onclick: async () => {
        toast(await copyText(key) ? 'Key copied.' : 'Select the key above and copy it (Ctrl+C).', 'info');
      } }, icon('copy', 14), 'Copy key')));
  }
  function showKey(name, key) { fill(result, keyNotice('Key for ' + name, key)); result.scrollIntoView({ behavior: 'smooth', block: 'center' }); }

  async function revoke(k) {
    if (!await confirmDialog(`Revoke "${k.name}"? Anyone using this key is signed out within seconds.`, 'Revoke')) return;
    try { await api('DELETE', '/access-keys/' + k.id); toast('Key revoked.', 'ok'); await load(); } catch (e) { toast(e.message, 'bad'); }
  }

  const form = h('form', {
    onsubmit: async e => {
      e.preventDefault(); err.textContent = ''; clear(result);
      try {
        const body = { name: name.value.trim() };
        if (audSelect.value) body.audienceId = audSelect.value;
        const k = await api('POST', '/access-keys', { body });
        name.value = '';
        result.append(keyNotice('Key created: ' + k.name + (k.audienceName ? ' - ' + k.audienceName : ''), k.key),
          h('p', { class: 'muted key-hint' }, 'The user signs in with your Company ID and this key. You can copy it again later from the Keys list below.'));
        await load();
      } catch (ex) { err.textContent = ex.message; }
    },
  }, h('div', { class: 'field' }, h('label', { for: 'kname' }, 'Who is this key for?'), name),
    h('div', { class: 'field' }, h('label', { for: 'kaud' }, 'Audience'), audSelect,
      h('small', { class: 'muted' }, audiences.length
        ? 'The audience decides which modules and pages this key can see. "Full access" sees everything approved.'
        : 'No audiences yet. Until you create some on the Access rules page, every key has full access.')),
    err,
    h('button', { class: 'btn btn-primary', type: 'submit' }, 'Create user key'));

  main.append(
    h('div', { class: 'page-head' }, h('div', null, h('h2', null, 'User access'),
      h('p', null, 'Give people a key so they can ask questions and read approved knowledge. They cannot upload, review, delete or see AI usage.')),
      h('a', { class: 'btn', href: '#/rules' }, icon('shield', 15), 'Access rules')),
    h('div', { class: 'two-col access-cols' },
    h('section', { class: 'card' }, h('div', { class: 'card-title' }, h('span', { class: 'ct-icon' }, icon('key', 18)), h('h3', null, 'Create a user key')),
      h('p', { class: 'muted' }, 'One key per person or team, so you can revoke any of them on its own.'), form, result),
    h('section', { class: 'card api-card' }, h('div', { class: 'card-title' }, h('span', { class: 'ct-icon' }, icon('bolt', 18)), h('h3', null, 'Use it from your own app')),
      h('p', { class: 'muted' }, 'One call: send a message, get an answer from your approved documents, with the document and page it came from.'),
      h('pre', { class: 'page-text' },
        `POST ${location.origin}/api/ask\nX-Api-Key: <a user key from above>\nContent-Type: application/json\n\n{ "message": "What validations apply on the registration page?" }`),
      h('pre', { class: 'page-text' },
        '{\n  "answer": "Full name is mandatory. Date of birth cannot be in the future. [1]",\n  "documented": true,\n  "sources": [ { "document": "guide.pdf", "page": 1 } ],\n  "conversationId": "…"\n}'),
      h('p', { class: 'hint' }, 'Add "conversationId" from a previous reply to ask a follow-up. Add "application" only if your company has more than one. Add "language": "ta" or "en" to choose the language of the answer (without it the answer follows the language of the question).'))),
    h('section', { class: 'card' }, h('div', { class: 'card-title' }, h('span', { class: 'ct-icon' }, icon('user', 18)), h('h3', null, 'Keys')), listHost));
  await load();
}
