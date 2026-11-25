import { h, add, fill, clear, badge, toast, formatDate, empty, confirmDialog } from '../dom.js';
import { api } from '../api.js';
import { copyText, icon, relativeTime } from '../chatkit.js';
import { inviteLinkDialog } from '../authkit.js';

// Platform administrator: see every company, view or reset its sign-in, disable its access, or delete it with all its data.
// Everything shown comes from the server as text; nothing is inserted as HTML.

function loginDialog(login, { title, note }) {
  const row = (label, value, what) => h('div', { class: 'field' },
    h('label', null, label),
    h('div', { class: 'copyrow' }, h('div', { class: 'keybox' }, value),
      h('button', { class: 'btn btn-sm', type: 'button', onclick: async e => {
        const ok = await copyText(value);
        toast(ok ? what + ' copied.' : 'Select it and copy it manually.', ok ? 'ok' : 'info');
        if (ok) { e.currentTarget.textContent = 'Copied'; setTimeout(() => { e.currentTarget.textContent = 'Copy'; }, 1500); }
      } }, 'Copy')));
  const dlg = h('dialog', { class: 'dialog', 'aria-labelledby': 'login-title' },
    h('h2', { id: 'login-title' }, title),
    note ? h('p', { class: 'muted' }, note) : null,
    row('Company ID (client ID)', login.companyId, 'Company ID'),
    row('API key (password)', login.key, 'API key'),
    h('p', { class: 'hint' }, 'Treat the key like a password. Viewing it is recorded in the audit log. Users sign in on the Company tab with these two values.'),
    h('div', { class: 'actions' }, h('button', { class: 'btn btn-primary', type: 'button', onclick: () => dlg.close() }, 'Done')));
  dlg.addEventListener('close', () => dlg.remove());
  document.body.append(dlg); dlg.showModal();
}

function noKeyDialog(company, onReset) {
  const dlg = h('dialog', { class: 'dialog', 'aria-labelledby': 'nokey-title' },
    h('h2', { id: 'nokey-title' }, company.name),
    h('div', { class: 'field' }, h('label', null, 'Company ID (client ID)'), h('div', { class: 'keybox' }, company.slug)),
    h('div', { class: 'notice warn' }, "This company's key is not stored (it was created before key viewing existed, or the server secret changed), so it cannot be shown. Reset the key to issue a new one."),
    h('div', { class: 'actions' },
      h('button', { class: 'btn', type: 'button', onclick: () => dlg.close() }, 'Close'),
      h('button', { class: 'btn btn-primary', type: 'button', onclick: () => { dlg.close(); onReset(); } }, 'Reset key')));
  dlg.addEventListener('close', () => dlg.remove());
  document.body.append(dlg); dlg.showModal();
}

/** Deleting is permanent, so the admin must type the Company ID. */
function deleteDialog(company) {
  return new Promise(resolve => {
    const input = h('input', { type: 'text', id: 'confirm-id', autocomplete: 'off', autocapitalize: 'none', spellcheck: 'false', placeholder: company.slug });
    const go = h('button', { class: 'btn btn-danger', type: 'button', disabled: true }, 'Delete permanently');
    input.addEventListener('input', () => { go.disabled = input.value !== company.slug; });
    const dlg = h('dialog', { class: 'dialog', 'aria-labelledby': 'del-title' },
      h('h2', { id: 'del-title' }, 'Delete ' + company.name + '?'),
      h('p', null, 'This permanently removes the company and everything it owns: ',
        h('strong', null, `${company.applications} application(s), ${company.documents} document(s)`),
        ', their extracted text, the search index, conversations, user keys and the stored files. It cannot be undone. Usage counters and the audit log are kept.'),
      h('div', { class: 'field' }, h('label', { for: 'confirm-id' }, 'Type the Company ID to confirm: ', h('code', null, company.slug)), input),
      h('div', { class: 'actions' },
        h('button', { class: 'btn', type: 'button', onclick: () => { dlg.close(); resolve(false); } }, 'Cancel'), go));
    go.addEventListener('click', () => { dlg.close(); resolve(true); });
    dlg.addEventListener('close', () => { dlg.remove(); resolve(false); });
    document.body.append(dlg); dlg.showModal(); input.focus();
  });
}

export async function adminView(main) {
  const listHost = h('div');
  const createHost = h('section', { class: 'card', style: 'max-width:560px', hidden: true });

  // ---- create ----
  const name = h('input', { type: 'text', id: 'tname', maxlength: 200, required: true, placeholder: 'Acme Hospital Group' });
  const slug = h('input', { type: 'text', id: 'tslug', maxlength: 99, required: true, placeholder: 'acme-hospital', autocapitalize: 'none', spellcheck: 'false' });
  const adminName = h('input', { type: 'text', id: 'tadmin-name', maxlength: 100, autocomplete: 'off', placeholder: 'Asha Kumar' });
  const adminEmail = h('input', { type: 'email', id: 'tadmin-email', maxlength: 254, autocomplete: 'off', autocapitalize: 'none', placeholder: 'asha@acme-hospital.org' });
  const err = h('p', { class: 'error-text', role: 'alert' });
  let slugTouched = false;
  slug.addEventListener('input', () => { slugTouched = true; });
  name.addEventListener('input', () => {
    if (!slugTouched) slug.value = name.value.toLowerCase().trim().replace(/[^a-z0-9]+/g, '-').replace(/^-+|-+$/g, '').slice(0, 99);
  });
  const form = h('form', {
    onsubmit: async e => {
      e.preventDefault(); err.textContent = '';
      try {
        const t = await api('POST', '/tenants', { body: { name: name.value.trim(), slug: slug.value.trim(), adminEmail: adminEmail.value.trim() || null, adminName: adminName.value.trim() || null } });
        name.value = ''; slug.value = ''; adminEmail.value = ''; adminName.value = ''; slugTouched = false; createHost.hidden = true;
        if (t.adminEmail) inviteLinkDialog({ title: 'Company created: ' + t.name, name: t.adminEmail, email: t.adminEmail, emailSent: t.inviteEmailed, link: t.inviteLink });   // the admin chooses their own password
        else loginDialog({ companyId: t.slug, key: t.apiKey }, { title: 'Company created: ' + t.name, note: 'Give these two values to the company administrator.' });
        await load();
      } catch (ex) { err.textContent = ex.message; }
    },
  },
    h('h3', null, 'New company'),
    h('div', { class: 'field' }, h('label', { for: 'tname' }, 'Company name'), name),
    h('div', { class: 'field' }, h('label', { for: 'tslug' }, 'Company ID'), slug,
      h('p', { class: 'hint' }, 'Lowercase letters, digits and hyphens. Used in links and by integrations; people sign in with their email.')),
    h('div', { class: 'formgrid' },
      h('div', { class: 'field' }, h('label', { for: 'tadmin-name' }, 'First admin: name (optional)'), adminName),
      h('div', { class: 'field' }, h('label', { for: 'tadmin-email' }, 'First admin: email (optional)'), adminEmail,
        h('p', { class: 'hint' }, 'They get a link to choose their own password. Leave empty to use the company key instead.'))),
    err, h('div', { class: 'actions' }, h('button', { class: 'btn btn-primary', type: 'submit' }, 'Create company'),
      h('button', { class: 'btn', type: 'button', onclick: () => { createHost.hidden = true; } }, 'Cancel')));
  add(createHost, [form]);

  // ---- actions ----
  async function show(c) {
    try { loginDialog(await api('GET', `/tenants/${c.id}/key`), { title: c.name + ' · sign-in details' }); }
    catch (e) { if (e.status === 409) noKeyDialog(c, () => reset(c)); else toast(e.message, 'bad'); }
  }
  async function reset(c) {
    if (!await confirmDialog(`Issue a new key for "${c.name}"? The old key stops working immediately, and anyone signed in as this company's administrator is signed out.`, 'Reset key')) return;
    try { loginDialog(await api('POST', `/tenants/${c.id}/reset-key`), { title: 'New key for ' + c.name, note: 'The previous key no longer works.' }); await load(); }
    catch (e) { toast(e.message, 'bad'); }
  }
  async function toggle(c) {
    const enabling = !c.isActive;
    if (!enabling && !await confirmDialog(`Disable access for "${c.name}"? Nobody in this company can sign in or use the assistant or API until you enable it again. Open sessions end within seconds. Their data is kept.`, 'Disable access')) return;
    try { await api('POST', `/tenants/${c.id}/${enabling ? 'enable' : 'disable'}`); toast(enabling ? 'Access enabled.' : 'Access disabled.', 'ok'); await load(); }
    catch (e) { toast(e.message, 'bad'); }
  }
  async function remove(c) {
    if (!await deleteDialog(c)) return;
    try { await api('DELETE', `/tenants/${c.id}?confirm=${encodeURIComponent(c.slug)}`); toast(`"${c.name}" and all its data were deleted.`, 'ok'); await load(); }
    catch (e) { toast(e.message, 'bad'); await load(); }
  }

  // ---- list ----
  async function load() {
    let list;
    try { list = await api('GET', '/tenants'); } catch (e) { fill(listHost, h('div', { class: 'notice warn' }, e.message)); return; }
    if (!list.length) { fill(listHost, empty('No companies yet', 'Create one with "New company".', 'building')); return; }
    const b = (label, fn, cls = '') => h('button', { class: 'btn btn-sm ' + cls, type: 'button', onclick: fn }, label);
    fill(listHost, h('div', { class: 'table-wrap' }, h('table', { class: 'table stack' },
      h('thead', null, h('tr', null, ['Company', 'Access', 'Created', 'Apps', 'Docs', 'User keys', 'Actions'].map((t, i) => h('th', { class: i >= 3 && i <= 5 ? 'num' : '' }, t)))),
      h('tbody', null, list.map(c => h('tr', { class: c.isActive ? '' : 'row-disabled' },
        h('td', { 'data-label': 'Company' }, h('strong', null, c.name), h('div', { class: 'muted small' }, h('code', null, c.slug))),
        h('td', { 'data-label': 'Access' }, c.isActive ? badge('Active', 'ok') : badge('Disabled', 'bad')),
        h('td', { 'data-label': 'Created' }, formatDate(c.createdUtc)),
        h('td', { class: 'num', 'data-label': 'Apps' }, c.applications),
        h('td', { class: 'num', 'data-label': 'Docs' }, c.documents),
        h('td', { class: 'num', 'data-label': 'User keys' }, c.userKeys),
        h('td', { 'data-label': 'Actions' }, h('div', { class: 'actions' },
          b('Show login', () => show(c)), b('Reset key', () => reset(c)),
          b(c.isActive ? 'Disable' : 'Enable', () => toggle(c)),
          b('Delete', () => remove(c), 'btn-danger')))))))));
  }

  // ---- server folders: where a company may choose to keep its files ----
  const foldersHost = h('div');
  const folderInput = h('input', { type: 'text', id: 'sfpath', maxlength: 300, placeholder: '/shared-docs   (Docker)   or   D:\\FlowMindDocs   (IIS / Windows)', autocomplete: 'off', spellcheck: 'false' });
  const folderErr = h('p', { class: 'error-text', role: 'alert' });
  async function addFolder(e) {
    e.preventDefault(); folderErr.textContent = '';
    try { await api('POST', '/server-folders', { body: { path: folderInput.value.trim() } }); folderInput.value = ''; toast('Location approved. Companies can now choose a folder inside it.', 'ok'); await loadFolders(); }
    catch (ex) { folderErr.textContent = ex.message; }
  }
  async function removeFolder(f) {
    if (!await confirmDialog(`Stop offering ${f.path}? Companies can no longer choose a folder inside it.`, 'Remove location')) return;
    try { await api('DELETE', `/server-folders/${f.id}`); toast('Location removed.', 'ok'); await loadFolders(); }
    catch (ex) { toast(ex.message, 'bad'); }
  }
  async function loadFolders() {
    let rows;
    try { rows = await api('GET', '/server-folders'); } catch (ex) { fill(foldersHost, h('div', { class: 'notice warn' }, ex.message)); return; }
    fill(foldersHost, rows.length ? h('ul', { class: 'rowlist' }, rows.map(f => h('li', null,
      h('code', null, f.path), ' ',
      f.exists ? null : badge('Not found from this server', 'bad'), ' ',
      f.fromConfig ? badge('Set in server settings', 'info') : null, ' ',
      h('span', { class: 'muted small' }, f.companies ? `${f.companies} compan${f.companies === 1 ? 'y uses' : 'ies use'} it` : 'not used yet'), ' ',
      f.fromConfig ? null : h('button', { class: 'btn btn-sm btn-danger', type: 'button', onclick: () => removeFolder(f) }, 'Remove'))))
      : h('p', { class: 'muted' }, 'No location approved yet, so "Folder on a server" is switched off for every company.'));
  }
  // ---- platform admins with an email account ----
  const paHost = h('div');
  const paName = h('input', { type: 'text', id: 'pa-name', maxlength: 100, autocomplete: 'off', placeholder: 'Name' });
  const paEmail = h('input', { type: 'email', id: 'pa-email', maxlength: 254, autocomplete: 'off', autocapitalize: 'none', placeholder: 'email@yourcompany.com' });
  const paErr = h('p', { class: 'error-text', role: 'alert' });
  async function loadAdmins() {
    let rows;
    try { rows = await api('GET', '/platform-admins'); } catch (ex) { fill(paHost, h('div', { class: 'notice warn' }, ex.message)); return; }
    fill(paHost, rows.length ? h('ul', { class: 'rowlist' }, rows.map(a => h('li', null,
      h('strong', null, a.displayName), ' ', h('span', { class: 'muted' }, a.email), ' ', a.pendingInvite ? badge('Invited', 'review') : a.active ? badge('Active', 'ok') : badge('Off', 'bad'), ' ',
      h('span', { class: 'muted small' }, a.lastLoginUtc ? 'last signed in ' + relativeTime(a.lastLoginUtc) : 'never signed in'), ' ',
      h('button', { class: 'btn btn-sm', type: 'button', onclick: async () => { try { const r = await api('POST', '/platform-admins/' + a.id + '/reset'); inviteLinkDialog({ title: 'Link for ' + a.displayName, name: a.displayName, email: a.email, emailSent: r.emailSent, link: r.link }); } catch (ex) { toast(ex.message, 'bad'); } } }, a.pendingInvite ? 'New invitation' : 'Reset password'), ' ',
      h('button', { class: 'btn btn-sm btn-danger', type: 'button', onclick: async () => { if (!await confirmDialog('Delete ' + a.email + '?', 'Delete')) return; try { await api('DELETE', '/platform-admins/' + a.id); await loadAdmins(); } catch (ex) { toast(ex.message, 'bad'); } } }, 'Delete'))))
      : h('p', { class: 'muted' }, 'No platform admin has an email account yet. You are signed in with the platform key.'));
  }
  const adminsCard = h('section', { class: 'card' },
    h('h3', null, 'Platform admins'),
    h('p', { class: 'muted' }, 'Give each administrator their own email and password instead of sharing the platform key. The key keeps working as the emergency way in, so keep it somewhere safe.'),
    h('form', { onsubmit: async e => {
      e.preventDefault(); paErr.textContent = '';
      try { const r = await api('POST', '/platform-admins', { body: { name: paName.value.trim(), email: paEmail.value.trim(), role: 'PlatformAdmin' } }); paName.value = ''; paEmail.value = ''; await loadAdmins(); inviteLinkDialog({ title: 'Invitation ready', name: r.member.displayName, email: r.member.email, emailSent: r.emailSent, link: r.link }); }
      catch (ex) { paErr.textContent = ex.message; }
    } }, h('div', { class: 'formgrid' }, h('div', { class: 'field' }, h('label', { for: 'pa-name' }, 'Name'), paName), h('div', { class: 'field' }, h('label', { for: 'pa-email' }, 'Email'), paEmail)), paErr,
      h('div', { class: 'actions' }, h('button', { class: 'btn btn-primary', type: 'submit' }, 'Invite platform admin'))),
    paHost);

  const foldersCard = h('section', { class: 'card' },
    h('h3', null, 'Server folders for company files'),
    h('p', { class: 'muted' }, 'Approve the folders on this server (a drive folder on the IIS machine, or a file share) where companies may keep their uploaded files. A company admin then types a folder inside one of them in Settings > Storage, and can copy or move the files already uploaded. The account that runs BBNavigator needs Modify permission on the folder.'),
    h('form', { onsubmit: addFolder },
      h('div', { class: 'field' }, h('label', { for: 'sfpath' }, 'Folder path on this server'), folderInput,
        h('p', { class: 'hint' }, 'Running in Docker? Use /shared-docs (the folder the containers share). Windows paths such as D:\\FlowMindDocs only work when BBNavigator itself runs on Windows/IIS. The folder must already exist. A whole drive or a system folder is refused. A test file is written and removed to prove it works.')),
      folderErr, h('div', { class: 'actions' }, h('button', { class: 'btn btn-primary', type: 'submit' }, 'Approve this location'))),
    foldersHost);

  add(main, [
    h('div', { class: 'page-head' }, h('div', null, h('h2', null, 'Companies'),
      h('p', null, 'Every company on this server. You can view or reset its sign-in, disable its access, or delete it with all its data.')),
      h('button', { class: 'btn btn-primary', type: 'button', onclick: () => { createHost.hidden = false; name.focus(); } }, icon('plus', 16), 'New company')),
    createHost,
    h('section', { class: 'card' }, listHost),
    adminsCard, foldersCard]);
  await load();
  await loadAdmins();
  await loadFolders();
}
