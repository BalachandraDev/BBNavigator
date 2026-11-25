import { h, toast, closeOnOutside } from './dom.js';
import { icon, copyText } from './chatkit.js';

// Small pieces shared by the sign-in page, the "choose a password" page and My account.

/** A password box with a show/hide button. autocomplete: "current-password" or "new-password" so password managers behave. */
export function passwordField({ id, label, autocomplete = 'current-password', hint = null, required = true }) {
  const input = h('input', { type: 'password', id, autocomplete, required, spellcheck: 'false', autocapitalize: 'none', maxlength: 200 });
  const eye = h('button', { class: 'pw-eye', type: 'button', 'aria-label': 'Show password', 'aria-pressed': 'false', onclick: () => {
    const show = input.type === 'password';
    input.type = show ? 'text' : 'password';
    eye.setAttribute('aria-pressed', String(show)); eye.setAttribute('aria-label', show ? 'Hide password' : 'Show password');
    eye.replaceChildren(icon(show ? 'eyeoff' : 'eye', 18));
  } }, icon('eye', 18));
  const el = h('div', { class: 'field' }, h('label', { for: id }, label), h('div', { class: 'pw' }, input, eye), hint ? h('p', { class: 'hint' }, hint) : null);
  return { el, input };
}

/**
 * A live checklist under a "new password" box. The server decides what is acceptable (it also refuses common passwords);
 * this only shows the rules that can be checked while typing, so people are not surprised when they press the button.
 */
export function passwordChecklist(input, { confirm = null, email = () => '', min = 10 } = {}) {
  const items = [
    ['len', `At least ${min} characters`, v => v.length >= min],
    ['mail', 'Does not contain your e-mail address', v => { const l = (email() || '').split('@')[0].toLowerCase(); return !l || l.length < 4 || !v.toLowerCase().includes(l); }],
    ...(confirm ? [['same', 'Both boxes match', v => v.length > 0 && v === confirm.value]] : []),
  ];
  const lis = items.map(([k, text]) => h('li', { 'data-k': k }, h('i', { 'aria-hidden': 'true' }, icon('check', 12)), h('span', null, text)));
  const list = h('ul', { class: 'pw-rules', 'aria-live': 'polite' }, lis);
  const paint = () => items.forEach(([k, , test], i) => lis[i].classList.toggle('ok', test(input.value)));
  input.addEventListener('input', paint); if (confirm) confirm.addEventListener('input', paint);
  paint();
  return { el: list, ok: () => items.every(([, , test]) => test(input.value)) };
}

/** A short, plain role name for the screens. */
export const roleLabel = r => r === 'PlatformAdmin' ? 'Platform admin' : r === 'TenantAdmin' ? 'Admin' : 'User';

/** After inviting someone or creating a reset link: says the email was sent, or shows the link for the admin to pass on (it works once). */
export function inviteLinkDialog({ title, name, email, emailSent, link }) {
  const dlg = h('dialog', { class: 'dialog', 'aria-label': title },
    h('div', { class: 'dlg-head' }, h('h2', null, title), h('button', { class: 'icon-btn', type: 'button', 'aria-label': 'Close', onclick: () => dlg.close() }, icon('close', 16))),
    emailSent
      ? h('div', { class: 'notice ok' }, icon('check', 16), h('div', null, `An email with the link was sent to ${email}.`))
      : [h('p', null, 'No email was sent (email is not set up on this server). Give this link to ', h('strong', null, name), '. It works once and expires on its own; whoever opens it can choose the password.'),
        h('div', { class: 'copyrow' }, h('div', { class: 'keybox' }, link),
          h('button', { class: 'btn', type: 'button', onclick: async () => toast(await copyText(link) ? 'Link copied.' : 'Select the link and copy it.', 'ok') }, icon('copy', 15), 'Copy'))],
    h('div', { class: 'actions' }, h('button', { class: 'btn btn-primary', type: 'button', onclick: () => dlg.close() }, 'Done')));
  dlg.addEventListener('close', () => dlg.remove()); closeOnOutside(dlg);
  document.body.append(dlg); dlg.showModal();
}