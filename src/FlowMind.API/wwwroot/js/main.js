import { h, clear, toast } from './dom.js';
import { icon } from './chatkit.js';
import { themeToggle } from './theme.js';
import { openPalette } from './palette.js';
import { overviewView } from './views/overview.js';
import { api, session, describeSession } from './api.js';
import { loginView, forgotView, chooseView } from './views/login.js';
import { accountView } from './views/account.js';
import { teamView } from './views/team.js';
import { documentsView } from './views/documents.js';
import { reviewView } from './views/review.js';
import { knowledgeView } from './views/knowledge.js';
import { chatView } from './views/chat.js';
import { usageView } from './views/usage.js';
import { adminView } from './views/admin.js';
import { accessView } from './views/access.js';
import { shareView } from './views/share.js';
import { insightsView } from './views/insights.js';
import { settingsView } from './views/settings.js';
import { rulesView } from './views/rules.js';

const root = document.getElementById('root');

// Shared state for the signed-in tenant. The tenant itself comes from the token on the server, never from here.
const state = { apps: [], appId: null, cleanup: null };

// [path, label, view, icon, group]. The sidebar groups pages by what you are doing.
const ADMIN_ROUTES = [
  ['overview', 'Home', overviewView, 'home', ''],
  ['documents', 'Documents', documentsView, 'upload', 'Knowledge'],
  ['review', 'Review', reviewView, 'review', 'Knowledge'],
  ['knowledge', 'Knowledge', knowledgeView, 'book', 'Knowledge'],
  ['chat', 'Assistant', chatView, 'chat', 'Share'],
  ['share', 'Share links', shareView, 'share', 'Share'],
  ['insights', 'Insights', insightsView, 'sparkle', 'Share'],
  ['usage', 'AI usage', usageView, 'chart', 'Manage', 'Daily quota for every AI process'],
  ['settings', 'Settings', settingsView, 'sliders', 'Manage', 'Where files are stored, your data, security'],
  ['team', 'Team', teamView, 'user', 'Manage', 'People, their roles and passwords'],
  ['access', 'Access keys', accessView, 'key', 'Manage', 'Keys for integrations and your own apps'],
  ['rules', 'Access rules', rulesView, 'shield', 'Manage', 'Who sees which modules and pages'],
];
// Pages that belong to the signed-in person, not to a role: reached from the account menu.
const EXTRA_ROUTES = [['account', 'My account', accountView, 'user', '']];
// Ordinary company users can read approved knowledge and chat, nothing else (the server enforces this too).
const USER_ROUTES = [
  ['chat', 'Assistant', chatView, 'chat', ''],
  ['knowledge', 'Knowledge', knowledgeView, 'book', ''],
];
function routesFor(role) {
  return role === 'PlatformAdmin' ? [['admin', 'Companies', adminView, 'building', '']] : role === 'TenantUser' ? (state.canBrowse === false ? USER_ROUTES.filter(r => r[0] !== 'knowledge') : USER_ROUTES) : ADMIN_ROUTES;
}

function route() { return (location.hash.replace(/^#\/?/, '').split('?')[0]) || ''; }
export function navigate(name, query) { location.hash = '#/' + name + (query ? '?' + new URLSearchParams(query) : ''); }
export function routeQuery() { return new URLSearchParams(location.hash.split('?')[1] || ''); }

function storedApp() { try { return sessionStorage.getItem('flowmind.app'); } catch { return null; } }
function storeApp(id) { try { sessionStorage.setItem('flowmind.app', id); } catch { /* ignore */ } }

async function loadApps() {
  state.apps = await api('GET', '/applications');
  const saved = storedApp();
  state.appId = state.apps.find(a => a.id === saved)?.id || state.apps[0]?.id || null;
}

async function createApplication(name) {
  const created = await api('POST', '/applications', { body: { name } });
  await loadApps();
  state.appId = created.id; storeApp(created.id);
  return created;
}

// Sidebar: grouped navigation, the application switcher on top, the account menu at the bottom. It collapses to icons on wide screens
// (remembered in this browser) and becomes a drawer on small ones. Routes, roles and the quick-search palette are unchanged.
const SB_KEY = 'flowmind.sidebar';
const sidebarCollapsed = () => { try { return localStorage.getItem(SB_KEY) === 'collapsed'; } catch { return false; } };
function setSidebar(collapsed, remember = true) {
  document.body.classList.toggle('sb-collapsed', collapsed);
  // icon-only rows get a native tooltip (it is never clipped by the scrolling list)
  document.querySelectorAll('.sidebar [data-tip]').forEach(el => { if (collapsed) el.setAttribute('title', el.dataset.tip); else el.removeAttribute('title'); });
  const b = document.getElementById('sb-toggle');
  if (b) { const t = collapsed ? 'Expand sidebar' : 'Collapse sidebar'; b.setAttribute('aria-label', t); b.setAttribute('aria-expanded', String(!collapsed)); b.title = t; }
  if (remember) { try { localStorage.setItem(SB_KEY, collapsed ? 'collapsed' : 'open'); } catch { /* not remembered */ } }
}

function shell(s) {
  const info = describeSession(s);
  const isPlatform = info.role === 'PlatformAdmin';
  const table = routesFor(info.role);
  const currentApp = state.apps.find(a => a.id === state.appId);

  const nav = h('nav', { class: 'sidenav', id: 'topnav', 'aria-label': 'Main' });
  let group = null;
  for (const [k, label, , ic, g] of table) {
    if (g && g !== group) nav.append(h('div', { class: 'nav-group' }, g));
    group = g || null;
    nav.append(h('a', { href: '#/' + k, 'data-route': k, 'data-tip': label, onclick: closeMenu }, icon(ic, 18), h('span', { class: 'nav-label' }, label)));
  }

  const appSelect = isPlatform ? null : h('select', {
    class: 'app-select', id: 'app-select', 'aria-label': 'Application',
    onchange: e => { state.appId = e.target.value; storeApp(state.appId); render(); },
  }, state.apps.map(a => h('option', { value: a.id, selected: a.id === state.appId }, a.name)));
  const newApp = info.role === 'TenantAdmin' ? h('button', { class: 'icon-btn new-app', type: 'button', onclick: promptNewApp, 'aria-label': 'New application', 'data-tip': 'New application' }, icon('plus', 16)) : null;
  const initial = (currentApp ? currentApp.name : 'F').trim().charAt(0).toUpperCase() || 'F';
  const ws = isPlatform
    ? h('div', { class: 'ws' }, h('span', { class: 'ws-mark', 'aria-hidden': 'true' }, icon('building', 16)), h('div', { class: 'ws-t' }, h('small', null, 'Workspace'), h('strong', null, 'All companies')))
    : h('div', { class: 'ws' },
      h('button', { class: 'ws-mark', type: 'button', 'aria-label': 'Expand sidebar', tabindex: '-1', onclick: () => setSidebar(false) }, initial),
      h('div', { class: 'ws-t' }, h('small', null, 'Application'), appSelect), newApp);

  const who = info.isAccount && info.name ? info.name : isPlatform ? 'Platform admin' : info.role === 'TenantUser' ? 'User' : info.name;
  const role = info.isAccount && info.email ? info.email : isPlatform ? 'Full access' : info.role === 'TenantUser' ? 'Chat and read' : 'Company admin';
  const userMenu = h('div', { class: 'usermenu', id: 'usermenu', role: 'menu', hidden: true },
    h('div', { class: 'um-head' }, h('strong', null, who), h('small', null, role)),
    h('a', { href: '#/account', role: 'menuitem', onclick: closeUserMenu }, icon('user', 16), 'My account'),
    h('button', { type: 'button', role: 'menuitem', onclick: () => { closeUserMenu(); openCommands(table); } }, icon('search', 16), 'Quick search', h('kbd', null, 'Ctrl K')),
    h('button', { type: 'button', role: 'menuitem', class: 'um-out', onclick: () => { closeUserMenu(); signOut(); } }, icon('logout', 16), 'Sign out'));
  const userBtn = h('button', { class: 'me-btn acct', type: 'button', 'aria-haspopup': 'menu', 'aria-expanded': 'false', 'aria-controls': 'usermenu', 'aria-label': 'Account menu: ' + who, 'data-tip': who, onclick: toggleUserMenu },
    h('span', { class: 'me-avatar', 'aria-hidden': 'true' }, (who || '?').trim().charAt(0).toUpperCase()),
    h('span', { class: 'acct-t' }, h('strong', null, who), h('small', null, role)), icon('chevron', 14));

  const sidebar = h('aside', { class: 'sidebar', id: 'sidebar', 'aria-label': 'Sidebar' },
    h('div', { class: 'sb-top' },
      h('a', { class: 'brand', href: '#/', 'aria-label': 'BBNavigator home' }, h('span', { class: 'brand-mark', 'aria-hidden': 'true' }, 'BB'), h('span', { class: 'brand-word' }, 'BB', h('b', null, 'Navigator'))),
      h('button', { class: 'icon-btn sb-collapse', id: 'sb-toggle', type: 'button', 'aria-controls': 'sidebar', onclick: () => setSidebar(!document.body.classList.contains('sb-collapsed')) }, icon('panel', 18)),
      h('button', { class: 'icon-btn sb-close', type: 'button', 'aria-label': 'Close menu', onclick: closeMenu }, icon('close', 18))),
    ws, nav,
    h('div', { class: 'sb-foot' }, h('div', { class: 'um-wrap' }, userBtn, userMenu)));

  const scrim = h('div', { class: 'scrim', onclick: closeMenu });
  const bar = h('header', { class: 'pagebar' },
    h('button', { class: 'icon-btn menu-btn', type: 'button', 'aria-label': 'Open menu', 'aria-controls': 'sidebar', onclick: toggleMenu }, icon('menu', 20)),
    h('nav', { class: 'crumbs', 'aria-label': 'Breadcrumb' },
      h('span', { class: 'crumb-app' }, isPlatform ? 'Platform' : (currentApp ? currentApp.name : 'BBNavigator')),
      h('span', { class: 'crumb-sep', 'aria-hidden': 'true' }, icon('chevron', 12)),
      h('strong', { id: 'crumb-title', 'aria-current': 'page' }, '')),
    h('div', { class: 'bar-right' },
      h('button', { class: 'searchbtn', type: 'button', onclick: () => openCommands(table), 'aria-label': 'Quick search' }, icon('search', 16), h('span', null, 'Search'), h('kbd', null, 'Ctrl K')),
      themeToggle('icon-btn')));
  const main = h('main', { id: 'view', tabindex: '-1' });
  clear(root).append(h('a', { class: 'skip', href: '#view', onclick: e => { e.preventDefault(); document.getElementById('view')?.focus(); } }, 'Skip to content'), h('div', { class: 'layout' }, sidebar, scrim, h('div', { class: 'page' }, bar, h('div', { class: 'content' }, main))));
  setSidebar(sidebarCollapsed(), false);
  return { main, nav, isPlatform };
}
function toggleMenu() { document.body.classList.toggle('menu-open'); }
function closeUserMenu() { const m = document.getElementById('usermenu'); if (m) m.hidden = true; const b = document.querySelector('.me-btn'); if (b) b.setAttribute('aria-expanded', 'false'); }
function toggleUserMenu() {
  const m = document.getElementById('usermenu'); if (!m) return;
  const open = m.hidden; m.hidden = !open;
  document.querySelector('.me-btn')?.setAttribute('aria-expanded', String(open));
}
// A click anywhere else, or Escape, closes the account menu and the small-screen drawer.
document.addEventListener('click', e => { if (!e.target.closest('.um-wrap')) closeUserMenu(); });
document.addEventListener('keydown', e => { if (e.key === 'Escape') { closeUserMenu(); document.body.classList.remove('menu-open'); } });
// Tab strips and segmented controls: Left/Right (and Home/End) move between their buttons, the way people expect from a tab list.
document.addEventListener('keydown', e => {
  if (!['ArrowLeft', 'ArrowRight', 'Home', 'End'].includes(e.key)) return;
  const group = e.target.closest && e.target.closest('[role=tablist], .choice, [role=radiogroup]');
  if (!group || e.target.tagName !== 'BUTTON') return;
  const btns = [...group.querySelectorAll(':scope button:not(:disabled)')], i = btns.indexOf(e.target);
  if (i < 0 || btns.length < 2) return;
  e.preventDefault();
  const next = e.key === 'Home' ? btns[0] : e.key === 'End' ? btns[btns.length - 1] : btns[(i + (e.key === 'ArrowRight' ? 1 : -1) + btns.length) % btns.length];
  next.focus();
});

let commandTable = null;
function openCommands(table) {
  const t = table || commandTable; if (!t) return;
  const items = t.map(([k, label, , ic, g]) => ({ label, hint: g || 'Page', icon: ic, run: () => navigate(k) }));
  if (describeSession(session.get() || {}).role === 'TenantAdmin') items.push({ label: 'New application', hint: 'Action', icon: 'plus', run: promptNewApp });
  items.push({ label: 'Sign out', hint: 'Action', icon: 'logout', run: signOut });
  openPalette(items);
}
window.addEventListener('keydown', e => {
  if ((e.ctrlKey || e.metaKey) && e.key.toLowerCase() === 'k' && session.get()) { e.preventDefault(); openCommands(); }
});
function openMenu() { document.body.classList.add('menu-open'); }
function closeMenu() { document.body.classList.remove('menu-open'); }
function promptNewApp() {
  const input = h('input', { type: 'text', id: 'newapp', maxlength: 200, required: true, placeholder: 'e.g. Hospital Management System' });
  const err = h('p', { class: 'error-text', role: 'alert' });
  const dlg = h('dialog', { class: 'dialog', 'aria-labelledby': 'newapp-title' },
    h('h2', { id: 'newapp-title' }, 'New application'),
    h('p', { class: 'muted' }, 'An application is one product whose documentation you want to make searchable.'),
    h('form', {
      method: 'dialog', onsubmit: async e => {
        e.preventDefault();
        try { await createApplication(input.value.trim()); dlg.close(); render(); toast('Application created.', 'ok'); }
        catch (ex) { err.textContent = ex.message; }
      },
    },
      h('div', { class: 'field' }, h('label', { for: 'newapp' }, 'Name'), input, err),
      h('div', { class: 'actions' },
        h('button', { class: 'btn', type: 'button', onclick: () => dlg.close() }, 'Cancel'),
        h('button', { class: 'btn btn-primary', type: 'submit' }, 'Create'))));
  dlg.addEventListener('close', () => dlg.remove());
  document.body.append(dlg); dlg.showModal(); input.focus();
}

function signOut() {
  session.clear(); state.apps = []; state.appId = null; state.loaded = false; state.canBrowse = undefined;
  try { sessionStorage.removeItem('flowmind.app'); } catch { /* ignore */ }
  navigate('login'); render();
}

async function render() {
  if (state.cleanup) { try { state.cleanup(); } catch { /* ignore */ } state.cleanup = null; }
  const s = session.get();
  let name = route();

  if (!s) {
    // Signed out: the sign-in page, "forgot password", and the links from invitation and reset e-mails.
    const signedIn = async () => { navigate(''); await render(); };
    clear(root).append(name === 'forgot' ? await forgotView()
      : name === 'reset' || name === 'invite' ? await chooseView(name, { onSignedIn: signedIn, query: routeQuery() })
      : loginView({ onSignedIn: signedIn }));
    return;
  }

  const info = describeSession(s);
  const isPlatform = info.role === 'PlatformAdmin';
  if (!isPlatform && state.apps.length === 0 && !state.loaded) {
    try { await loadApps(); state.loaded = true; } catch (e) { toast(e.message, 'bad'); }
  }
  // A user key whose audience is chat-only gets no Knowledge page. The server refuses it too; this just hides the tab.
  if (info.role === 'TenantUser' && state.canBrowse === undefined) {
    try { state.canBrowse = (await api('GET', '/me/access')).canBrowseKnowledge !== false; } catch { state.canBrowse = false; }   // when unsure, show less
  }
  closeMenu();
  const { main, nav } = shell(s);

  if (info.role === 'TenantAdmin' && state.appId) navBadges(nav);
  const table = routesFor(info.role);
  const found = table.find(([k]) => k === name) || EXTRA_ROUTES.find(([k]) => k === name) || table[0];
  if (name !== found[0]) { history.replaceState(null, '', '#/' + found[0]); name = found[0]; }
  nav.querySelectorAll('a[data-route]').forEach(a => a.getAttribute('data-route') === name ? a.setAttribute('aria-current', 'page') : a.removeAttribute('aria-current'));
  document.title = found[1] + ' - BBNavigator';
  commandTable = table;
  const crumb = document.getElementById('crumb-title'); if (crumb) crumb.textContent = found[1];

  if (!isPlatform && !state.appId) {
    main.append(info.role === 'TenantAdmin' ? firstApplicationCard()
      : h('section', { class: 'card', style: 'max-width:520px;margin:40px auto' }, h('h2', null, 'Nothing to show yet'),
        h('p', { class: 'muted' }, 'No application has been set up for your company yet. Ask your administrator.')));
    return;
  }
  const ctx = {
    appId: state.appId, apps: state.apps, navigate, routeQuery,
    app: state.apps.find(a => a.id === state.appId),
    onCleanup: fn => { state.cleanup = fn; },
  };
  try { await found[2](main, ctx); }
  catch (e) { if (e && e.status === 401) return; main.append(h('div', { class: 'notice warn', role: 'alert' }, e.message || 'Something went wrong.')); }
}

/** A small count on "Review" when documents are waiting for the admin. Purely a convenience: failures are ignored. */
async function navBadges(nav) {
  try {
    const docs = await api('GET', '/documents?applicationId=' + encodeURIComponent(state.appId));
    const n = docs.filter(d => d.status === 'PendingReview').length;
    const a = nav.querySelector('a[data-route="review"]');
    if (a && n && !a.querySelector('.nav-badge')) a.append(h('span', { class: 'nav-badge', title: `${n} waiting for review` }, String(n)));
  } catch { /* ignore */ }
  try {
    const open = (await api('GET', '/support-requests/count')).open;
    const a = nav.querySelector('a[data-route="insights"]');
    if (a && open && !a.querySelector('.nav-badge')) a.append(h('span', { class: 'nav-badge', title: `${open} support request${open === 1 ? '' : 's'} waiting` }, String(open)));
  } catch { /* ignore */ }
}

function firstApplicationCard() {
  const input = h('input', { type: 'text', id: 'firstapp', maxlength: 200, required: true, placeholder: 'e.g. Hospital Management System' });
  const err = h('p', { class: 'error-text', role: 'alert' });
  return h('section', { class: 'card', style: 'max-width:520px;margin:40px auto' },
    h('h2', null, 'Create your first application'),
    h('p', { class: 'muted' }, 'An application is one product whose documentation you will upload and ask questions about.'),
    h('form', {
      onsubmit: async e => {
        e.preventDefault();
        try { await createApplication(input.value.trim()); render(); } catch (ex) { err.textContent = ex.message; }
      },
    }, h('div', { class: 'field' }, h('label', { for: 'firstapp' }, 'Application name'), input, err),
      h('button', { class: 'btn btn-primary', type: 'submit' }, 'Create application')));
}

window.addEventListener('hashchange', render);
window.addEventListener('flowmind:signed-out', () => { state.apps = []; state.appId = null; state.loaded = false; state.canBrowse = undefined; navigate('login'); render(); });
render();
