import { h, badge, toast, formatDate, confirmDialog } from '../dom.js';
import { api, session } from '../api.js';
import { icon, relativeTime } from '../chatkit.js';
import { passwordField, passwordChecklist, roleLabel } from '../authkit.js';

// My account: the signed-in person's own name and password, and a way to end every session. Only people with an email account have one.
// A company or user KEY has no account: the page says so and points to the people who can make one.

/** A fresh session was issued (name or password changed): keep it, and repaint the shell so the new name shows. */
function adopt(r) { session.set({ token: r.accessToken, expiresUtc: r.expiresUtc }); window.dispatchEvent(new HashChangeEvent('hashchange')); }

export async function accountView(main) {
  const me = await api('GET', '/account/me');
  main.append(h('div', { class: 'page-head' }, h('div', null, h('h2', null, 'My account'), h('p', null, 'Your name, your password and where you are signed in.'))));

  if (me.kind !== 'account') {
    main.append(h('div', { class: 'card' }, h('div', { class: 'card-title' }, icon('key', 18), h('h3', null, 'You are signed in with a key')),
      h('p', { class: 'muted' }, 'A company or access key belongs to a team or an integration, not to one person, so it has no password to change. Ask your administrator for your own account: you then sign in with your email, can reset your own password and keep your own chat history.')));
    return;
  }
  const p = me.profile;

  // ---- profile ----
  const name = h('input', { type: 'text', id: 'acc-name', value: p.displayName, maxlength: 100, required: true, autocomplete: 'name' });
  const nameErr = h('p', { class: 'error-text', role: 'alert' });
  const profile = h('section', { class: 'card' },
    h('div', { class: 'card-title' }, icon('user', 18), h('h3', null, 'Profile')),
    h('div', { class: 'acct-id' }, h('span', { class: 'me-avatar lg', 'aria-hidden': 'true' }, p.displayName.trim().charAt(0).toUpperCase()),
      h('div', null, h('strong', null, p.displayName), h('div', { class: 'muted' }, p.email))),
    h('dl', { class: 'kv' },
      h('dt', null, 'Role'), h('dd', null, badge(roleLabel(p.role), p.role === 'TenantUser' ? 'info' : 'review')),
      p.companyName ? [h('dt', null, 'Company'), h('dd', null, p.companyName)] : null,
      p.audience ? [h('dt', null, 'Group'), h('dd', null, p.audience)] : null,
      h('dt', null, 'Last signed in'), h('dd', null, p.lastLoginUtc ? `${formatDate(p.lastLoginUtc)} (${relativeTime(p.lastLoginUtc)})` : 'Just now'),
      h('dt', null, 'Password changed'), h('dd', null, p.passwordChangedUtc ? relativeTime(p.passwordChangedUtc) : '—')),
    h('form', { class: 'acct-form', onsubmit: async e => {
      e.preventDefault(); nameErr.textContent = '';
      try { adopt(await api('PUT', '/account/me', { body: { displayName: name.value.trim() } })); toast('Name saved.', 'ok'); }
      catch (ex) { nameErr.textContent = ex.message; }
    } }, h('div', { class: 'field' }, h('label', { for: 'acc-name' }, 'Your name'), name), nameErr,
      h('div', { class: 'actions' }, h('button', { class: 'btn btn-primary', type: 'submit' }, 'Save name')),
      h('p', { class: 'hint' }, 'Your email is how you sign in. To change it, ask your administrator.')));

  // ---- password ----
  const cur = passwordField({ id: 'acc-cur', label: 'Current password', autocomplete: 'current-password' });
  const n1 = passwordField({ id: 'acc-new', label: 'New password', autocomplete: 'new-password' });
  const n2 = passwordField({ id: 'acc-new2', label: 'Type the new password again', autocomplete: 'new-password' });
  const rules = passwordChecklist(n1.input, { confirm: n2.input, email: () => p.email });
  const pwErr = h('p', { class: 'error-text', role: 'alert' });
  const go = h('button', { class: 'btn btn-primary', type: 'submit' }, 'Change password');
  const password = h('section', { class: 'card' },
    h('div', { class: 'card-title' }, icon('lock', 18), h('h3', null, 'Password')),
    h('form', { onsubmit: async e => {
      e.preventDefault(); pwErr.textContent = '';
      if (!rules.ok()) { pwErr.textContent = 'Check the list: the two new passwords must match and be long enough.'; return; }
      go.disabled = true;
      try { adopt(await api('POST', '/account/password', { body: { current: cur.input.value, next: n1.input.value } })); toast('Password changed. Your other devices were signed out.', 'ok'); [cur, n1, n2].forEach(x => { x.input.value = ''; }); }
      catch (ex) { pwErr.textContent = ex.message; }
      go.disabled = false;
    } }, cur.el, n1.el, n2.el, rules.el, pwErr, h('div', { class: 'actions' }, go)),
    h('p', { class: 'hint' }, 'Forgotten your current password? Sign out, then use "Forgot password?" on the sign-in page.'));

  // ---- sessions ----
  const sessions = h('section', { class: 'card' },
    h('div', { class: 'card-title' }, icon('shield', 18), h('h3', null, 'Where you are signed in')),
    h('p', { class: 'muted' }, 'Lost a phone, or used a shared computer? End every session. You stay signed in here; everywhere else you will have to sign in again.'),
    h('div', { class: 'actions' }, h('button', { class: 'btn btn-danger', type: 'button', onclick: async () => {
      if (!await confirmDialog('Sign out on every device, including other browsers? You stay signed in on this one.', 'Sign out everywhere')) return;
      try { adopt(await api('POST', '/account/sign-out-everywhere')); toast('Every other session was ended.', 'ok'); } catch (ex) { toast(ex.message, 'bad'); }
    } }, icon('logout', 15), 'Sign out everywhere else')));

  main.append(h('div', { class: 'acct-grid' }, h('div', null, profile), h('div', null, password, sessions)));
}
