import { h } from '../dom.js';
import { api, session } from '../api.js';
import { icon } from '../chatkit.js';
import { themeToggle } from '../theme.js';
import { passwordField, passwordChecklist } from '../authkit.js';

// The signed-out pages: sign in with an e-mail and password, "Forgot password?", and choosing a password from an invitation or reset link.
// Company and platform KEYS still work (for integrations and as the emergency way in), under "Use an access key instead".

const hero = () => {
  const point = (ic, title, text) => h('li', null, h('span', { class: 'auth-ic' }, icon(ic, 18)), h('span', null, h('strong', null, title), h('small', null, text)));
  return h('aside', { class: 'auth-hero' },
    h('div', { class: 'auth-brand' }, h('span', { class: 'brand-mark', 'aria-hidden': 'true' }, 'BB'), h('span', null, 'BB', h('b', null, 'Navigator'))),
    h('div', { class: 'auth-pitch' },
      h('h2', null, 'Answers your team can trust.'),
      h('p', null, 'An assistant that answers only from your own approved documentation, and tells you when something is not documented.'),
      h('ul', null,
        point('upload', 'Upload your documents', 'PDF, Word or text. Read and analysed for you.'),
        point('review', 'Review before it goes live', 'You approve what the AI found, nothing is guessed.'),
        point('share', 'Share a chat link', 'Anyone can ask. No sign-in, no admin pages.'))));
};
const frame = (...children) => h('div', { class: 'auth' }, hero(),
  h('main', { class: 'auth-panel' }, h('div', { class: 'floattheme' }, themeToggle('icon-btn')), h('div', { class: 'login' }, ...children)));
const startSession = async (r, onSignedIn) => { session.set({ token: r.accessToken, expiresUtc: r.expiresUtc }); await onSignedIn(); };

export function loginView({ onSignedIn }) {
  let mode = 'email';                                     // email | key | platform
  const email = h('input', { type: 'email', id: 'email', autocomplete: 'username', autocapitalize: 'none', spellcheck: 'false', required: true, placeholder: 'you@hospital.org' });
  const pw = passwordField({ id: 'password', label: 'Password' });
  const slug = h('input', { type: 'text', id: 'slug', autocomplete: 'username', autocapitalize: 'none', spellcheck: 'false', placeholder: 'your-company' });
  const key = h('input', { type: 'password', id: 'key', autocomplete: 'current-password', placeholder: 'Access key' });
  const err = h('p', { class: 'error-text', role: 'alert' });
  const submit = h('button', { class: 'btn btn-primary', type: 'submit' }, 'Sign in');

  const emailBlock = h('div', null,
    h('div', { class: 'field' }, h('label', { for: 'email' }, 'Email'), email),
    pw.el,
    h('div', { class: 'auth-row' }, h('a', { href: '#/forgot' }, 'Forgot password?')));
  const keyBlock = h('div', { hidden: true },
    h('p', { class: 'notice' }, 'For integrations and as an emergency way in. People should use their email and password.'),
    h('div', { class: 'field', id: 'slug-field' }, h('label', { for: 'slug' }, 'Company ID'), slug),
    h('div', { class: 'field' }, h('label', { for: 'key' }, h('span', { id: 'key-label' }, 'Access key')), key));
  const switchRow = h('p', { class: 'auth-alt' });

  function setMode(m) {
    mode = m; err.textContent = '';
    emailBlock.hidden = m !== 'email'; keyBlock.hidden = m === 'email';
    email.required = m === 'email'; pw.input.required = m === 'email';
    slug.closest('.field').hidden = m !== 'key';
    keyBlock.querySelector('#key-label').textContent = m === 'platform' ? 'Platform admin key' : 'Access key';
    switchRow.replaceChildren(...(m === 'email'
      ? ['Use an access key instead: ', h('button', { class: 'btn-link', type: 'button', onclick: () => setMode('key') }, 'Company key'), ' · ', h('button', { class: 'btn-link', type: 'button', onclick: () => setMode('platform') }, 'Platform key')]
      : [h('button', { class: 'btn-link', type: 'button', onclick: () => setMode('email') }, 'Back to email and password')]));
  }

  async function submitForm(e) {
    e.preventDefault(); err.textContent = '';
    submit.disabled = true; submit.textContent = 'Signing in…';
    try {
      let r;
      if (mode === 'email') r = await api('POST', '/auth/login', { body: { email: email.value.trim(), password: pw.input.value } });
      else {
        const body = { apiKey: key.value }; if (mode === 'key') body.tenantSlug = slug.value.trim();
        r = await api('POST', '/auth/token', { body });
      }
      pw.input.value = ''; key.value = '';
      await startSession(r, onSignedIn);
    } catch (ex) {
      err.textContent = ex.status === 401 && mode !== 'email' ? 'That company ID and key do not match. Check them and try again.' : ex.message;
    } finally { submit.disabled = false; submit.textContent = 'Sign in'; }
  }

  setMode('email');
  return frame(
    h('h1', null, 'Welcome back'),
    h('p', { class: 'muted' }, 'Sign in to manage your documents and assistant.'),
    h('form', { onsubmit: submitForm, novalidate: false }, emailBlock, keyBlock, err, submit),
    switchRow);
}

/** "Forgot password?": always answers the same way, so it cannot be used to find out who has an account. */
export async function forgotView() {
  const email = h('input', { type: 'email', id: 'fp-email', autocomplete: 'username', autocapitalize: 'none', spellcheck: 'false', required: true, placeholder: 'you@hospital.org' });
  const err = h('p', { class: 'error-text', role: 'alert' });
  const go = h('button', { class: 'btn btn-primary', type: 'submit' }, 'Send reset link');
  const host = h('div');
  let options = { emailEnabled: true };
  try { options = await api('GET', '/auth/options'); } catch { /* assume it is on */ }

  const done = text => host.replaceChildren(h('div', { class: 'notice ok', role: 'status' }, icon('check', 16), h('div', null, text)),
    h('p', { class: 'auth-alt' }, h('a', { href: '#/' }, 'Back to sign in')));
  const form = h('form', { onsubmit: async e => {
    e.preventDefault(); err.textContent = ''; go.disabled = true;
    try { const r = await api('POST', '/auth/forgot', { body: { email: email.value.trim() } }); done(r.message); }
    catch (ex) { err.textContent = ex.message; go.disabled = false; }
  } }, h('div', { class: 'field' }, h('label', { for: 'fp-email' }, 'Email'), email), err, go);

  host.append(options.emailEnabled
    ? form
    : h('div', null, h('div', { class: 'notice warn' }, 'Email is not set up on this server, so a reset link cannot be sent. Ask your administrator: they can create a reset link for you on the Team page.'),
      h('p', { class: 'auth-alt' }, h('a', { href: '#/' }, 'Back to sign in'))));
  return frame(h('h1', null, 'Reset your password'), h('p', { class: 'muted' }, 'Enter the email you sign in with. If it has an account, we send a link to choose a new password.'), host);
}

/** An invitation ("Choose your password") or a reset link ("Choose a new password"). The secret is in the address after #, which the browser never sends to the server. */
export async function chooseView(page, { onSignedIn, query }) {
  const token = query.get('token') || '';
  const host = h('div');
  let info = null;
  try { info = await api('GET', '/auth/token-info?token=' + encodeURIComponent(token)); } catch { /* shown below */ }
  if (!info) {
    host.append(h('div', { class: 'notice warn' }, 'This link has expired or was already used. Ask for a new one.'),
      h('p', { class: 'auth-alt' }, h('a', { href: '#/forgot' }, 'Send me a new reset link'), ' · ', h('a', { href: '#/' }, 'Sign in')));
    return frame(h('h1', null, 'Link not valid'), host);
  }
  const invite = info.purpose === 'invite';
  const a = passwordField({ id: 'np1', label: invite ? 'Choose a password' : 'New password', autocomplete: 'new-password' });
  const b = passwordField({ id: 'np2', label: 'Type it again', autocomplete: 'new-password' });
  const rules = passwordChecklist(a.input, { confirm: b.input, email: () => info.email });
  const err = h('p', { class: 'error-text', role: 'alert' });
  const go = h('button', { class: 'btn btn-primary', type: 'submit' }, invite ? 'Create my account' : 'Save new password');
  host.append(h('form', { onsubmit: async e => {
    e.preventDefault(); err.textContent = '';
    if (!rules.ok()) { err.textContent = 'Check the list above: the two boxes must match and the password must be long enough.'; return; }
    go.disabled = true;
    try { const r = await api('POST', '/auth/reset', { body: { token, password: a.input.value } }); history.replaceState(null, '', '#/'); await startSession(r, onSignedIn); }
    catch (ex) { err.textContent = ex.message; go.disabled = false; }
  } }, h('div', { class: 'field' }, h('label', null, 'Email'), h('div', { class: 'readonly' }, info.email)), a.el, b.el, rules.el, err, go));
  return frame(h('h1', null, invite ? `Welcome, ${info.displayName.split(' ')[0]}` : 'Choose a new password'),
    h('p', { class: 'muted' }, invite ? 'Choose a password to finish creating your account. You will sign in with your email and this password.' : 'After this, every other device is signed out.'), host);
}
