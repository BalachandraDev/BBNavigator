import { h, add, clear, fill, badge, toast, formatDate, empty, confirmDialog } from '../dom.js';
import { api, session, describeSession } from '../api.js';
import { icon, relativeTime } from '../chatkit.js';
import { inviteLinkDialog } from '../authkit.js';

// Team (company admins): the people of this company. Each person signs in with their own email and password, can reset it themselves,
// and sees only what their group (audience) allows. Invitations are emailed when the server has email set up; otherwise the page
// shows the link to pass on.

export async function teamView(main, ctx) {
  let options = { emailEnabled: false };
  try { options = await api('GET', '/auth/options'); } catch { /* show the manual wording */ }
  let audiences = [];
  try { audiences = await api('GET', '/access/audiences'); } catch { /* people can still be invited */ }
  const listHost = h('div');
  let members = [];

  const linkDialog = (title, r) => inviteLinkDialog({ title, name: r.member.displayName, email: r.member.email, emailSent: r.emailSent, link: r.link });

  // ---- invite ----
  function inviteDialog() {
    const name = h('input', { type: 'text', id: 'tm-name', maxlength: 100, required: true, autocomplete: 'off', placeholder: 'Priya Nair' });
    const email = h('input', { type: 'email', id: 'tm-email', maxlength: 254, required: true, autocomplete: 'off', autocapitalize: 'none', placeholder: 'priya@hospital.org' });
    const role = h('select', { id: 'tm-role' }, h('option', { value: 'TenantUser' }, 'User: chat, and the Knowledge page if their group allows it'), h('option', { value: 'TenantAdmin' }, 'Admin: manages documents, people and settings'));
    const aud = h('select', { id: 'tm-aud' }, h('option', { value: '' }, 'Everything approved'), audiences.map(a => h('option', { value: a.id }, a.name)));
    const audField = h('div', { class: 'field' }, h('label', { for: 'tm-aud' }, 'Group (what they may see)'), aud,
      h('p', { class: 'hint' }, audiences.length ? 'Groups are set on the Access rules page.' : 'No groups yet. Create them on the Access rules page to limit what people see.'));
    role.addEventListener('change', () => { audField.hidden = role.value === 'TenantAdmin'; });
    const err = h('p', { class: 'error-text', role: 'alert' });
    const go = h('button', { class: 'btn btn-primary', type: 'submit' }, options.emailEnabled ? 'Send invitation' : 'Create account');
    const dlg = h('dialog', { class: 'dialog', 'aria-label': 'Invite someone' },
      h('div', { class: 'dlg-head' }, h('h2', null, 'Invite someone'), h('button', { class: 'icon-btn', type: 'button', 'aria-label': 'Close', onclick: () => dlg.close() }, icon('close', 16))),
      h('p', { class: 'muted' }, options.emailEnabled ? 'They get an email with a link to choose their own password.' : 'Email is not set up on this server, so you will get a link to pass on yourself.'),
      h('form', { onsubmit: async e => {
        e.preventDefault(); err.textContent = ''; go.disabled = true;
        try {
          const r = await api('POST', '/users', { body: { name: name.value.trim(), email: email.value.trim(), role: role.value, audienceId: role.value === 'TenantUser' && aud.value ? aud.value : null } });
          dlg.close(); await load(); linkDialog('Invitation ready', r);
        } catch (ex) { err.textContent = ex.message; go.disabled = false; }
      } }, h('div', { class: 'field' }, h('label', { for: 'tm-name' }, 'Name'), name), h('div', { class: 'field' }, h('label', { for: 'tm-email' }, 'Email'), email),
        h('div', { class: 'field' }, h('label', { for: 'tm-role' }, 'Role'), role), audField, err,
        h('div', { class: 'actions' }, h('button', { class: 'btn', type: 'button', onclick: () => dlg.close() }, 'Cancel'), go)));
    dlg.addEventListener('close', () => dlg.remove());
    document.body.append(dlg); dlg.showModal(); name.focus();
  }

  // ---- changes ----
  async function patch(m, body, ok) {
    try { await api('PATCH', '/users/' + m.id, { body }); if (ok) toast(ok, 'ok'); await load(); } catch (e) { toast(e.message, 'bad'); await load(); }
  }
  async function resetLink(m) {
    const invite = m.pendingInvite;
    if (!invite && !await confirmDialog(`Create a password-reset link for ${m.displayName}? Their current password keeps working until they use it.`, 'Create link')) return;
    try { linkDialog(invite ? 'New invitation' : 'Password reset link', await api('POST', `/users/${m.id}/reset`)); } catch (e) { toast(e.message, 'bad'); }
  }
  async function remove(m) {
    if (!await confirmDialog(`Delete ${m.displayName} (${m.email})? They can no longer sign in and their chat history stays with the company. This cannot be undone.`, 'Delete person')) return;
    try { await api('DELETE', '/users/' + m.id); toast('Person removed.', 'ok'); await load(); } catch (e) { toast(e.message, 'bad'); }
  }

  const status = m => !m.active ? badge('Switched off', 'bad') : m.locked ? badge('Locked', 'warn') : m.pendingInvite ? badge('Invited', 'review') : badge('Active', 'ok');

  function row(m) {
    const roleSel = h('select', { class: 'sel-sm', 'aria-label': `Role of ${m.displayName}`, onchange: () => patch(m, { role: roleSel.value }, 'Role changed.') },
      h('option', { value: 'TenantUser' }, 'User'), h('option', { value: 'TenantAdmin' }, 'Admin'));
    roleSel.value = m.role;
    const audSel = h('select', { class: 'sel-sm', 'aria-label': `Group of ${m.displayName}`, onchange: () => patch(m, audSel.value ? { audienceId: audSel.value } : { fullAccess: true }, 'Group changed.') },
      h('option', { value: '' }, 'Everything approved'), audiences.map(a => h('option', { value: a.id }, a.name)));
    audSel.value = m.audienceId || '';
    return h('tr', { class: m.active ? '' : 'row-disabled' },
      h('td', { 'data-label': 'Person' }, h('div', { class: 'person' }, h('span', { class: 'me-avatar', 'aria-hidden': 'true' }, m.displayName.trim().charAt(0).toUpperCase()),
        h('div', null, h('strong', null, m.displayName), h('div', { class: 'muted small' }, m.email)))),
      h('td', { 'data-label': 'Role' }, roleSel),
      h('td', { 'data-label': 'Group' }, m.role === 'TenantAdmin' ? h('span', { class: 'muted' }, 'Everything') : audSel),
      h('td', { 'data-label': 'Status' }, status(m)),
      h('td', { 'data-label': 'Last signed in' }, m.lastLoginUtc ? h('span', { title: formatDate(m.lastLoginUtc) }, relativeTime(m.lastLoginUtc)) : h('span', { class: 'muted' }, 'Never')),
      h('td', { 'data-label': 'Actions' }, h('div', { class: 'actions end' },
        h('button', { class: 'btn btn-sm', type: 'button', disabled: !m.active, onclick: () => resetLink(m) }, icon('key', 14), m.pendingInvite ? 'New invitation' : 'Reset password'),
        h('button', { class: 'btn btn-sm', type: 'button', onclick: () => patch(m, { active: !m.active }, m.active ? 'Switched off. They are signed out.' : 'Switched on.') }, m.active ? 'Switch off' : 'Switch on'),
        h('button', { class: 'btn btn-sm btn-danger', type: 'button', onclick: () => remove(m) }, icon('trash', 14), 'Delete'))));
  }

  async function load() {
    try { members = await api('GET', '/users'); } catch (e) { fill(listHost, h('div', { class: 'notice warn' }, e.message)); return; }
    clear(listHost);
    if (!members.length) { listHost.append(empty('No one has an account yet', 'Invite the people who use BBNavigator. Each signs in with their own email and password.', 'user')); return; }
    listHost.append(h('div', { class: 'table-wrap' }, h('table', { class: 'table stack' },
      h('thead', null, h('tr', null, ['Person', 'Role', 'Group', 'Status', 'Last signed in', ''].map(t => h('th', null, t)))),
      h('tbody', null, members.map(row)))));
  }

  add(main, [
    h('div', { class: 'page-head' }, h('div', null, h('h2', null, 'Team'),
      h('p', null, 'The people who sign in with their own email and password. They reset their own password, and see only what their group allows.')),
      h('button', { class: 'btn btn-primary', type: 'button', onclick: inviteDialog }, icon('plus', 16), 'Invite someone')),
    describeSession(session.get() || {}).isAccount ? null : h('div', { class: 'notice' }, icon('key', 16), h('div', null, h('strong', null, 'You are signed in with the company key. '),
      'Invite yourself first (Invite someone, role Admin): you then sign in with your own email and password, and can reset it yourself. The key keeps working for integrations.')),
    options.emailEnabled ? null : h('div', { class: 'notice warn' }, icon('alert', 16), h('div', null, h('strong', null, 'Email is not set up on this server. '),
      'Invitations and password resets will show you a link to pass on yourself. To send them automatically, the person who runs the server sets the Email settings (see the README).')),
    h('section', { class: 'card' }, listHost)]);   // add() skips the empty ones; the browser's append would print them as the word null
  await load();
  void ctx;
}
