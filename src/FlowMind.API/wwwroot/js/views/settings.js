import { h, fill, badge, toast, formatDate, empty, confirmDialog } from '../dom.js';
import { api } from '../api.js';
import { icon, relativeTime } from '../chatkit.js';

// Settings (company admins): where uploaded files are stored, where all the data lives, the activity trail and a security checklist.
// Access keys typed here go to the server and are stored encrypted. They are never sent back to the browser, only a hint of the last 4 characters.

const TABS = [['storage', 'Storage', 'drive'], ['data', 'Where your data lives', 'database'], ['activity', 'Activity', 'activity'], ['security', 'Security', 'shield']];
const PROVIDERS = [
  ['local', 'drive', 'This server', "Files stay in this server's private folder. Simple, and nothing leaves your network.", 'Default'],
  ['r2', 'cloud', 'Cloudflare R2', 'A cloud bucket with a free tier (10 GB) and no download fees.', 'Free tier'],
  ['s3', 'cloud', 'Your own cloud', 'Any S3-compatible storage: MinIO on your network, Wasabi, Backblaze B2, AWS S3.', 'S3-compatible'],
  ['folder', 'server', 'Folder on a server', 'A folder on your IIS server or a file share, for example D:\\FlowMindDocs or \\\\fileserver\\FlowMind. No cloud account needed.', 'Your server'],
];
const LABELS = {
  'document.upload': 'Document uploaded', 'document.update': 'Update added to a document', 'document.approve': 'Document approved', 'document.delete': 'Document deleted',
  'document.retry': 'Processing retried', 'document.reindex': 'Document re-indexed', 'botlink.create': 'Share link created', 'botlink.update': 'Share link changed',
  'botlink.enable': 'Share link switched on', 'botlink.disable': 'Share link switched off', 'botlink.delete': 'Share link deleted', 'botlink.clear-history': 'Chat history cleared',
  'settings.storage': 'Storage location changed', 'settings.storage-copy': 'Files copied between locations', 'support.close': 'Support request closed', 'support.update': 'Support request updated',
  'support.delete': 'Support request deleted', 'insights.gap-handled': 'Question marked handled', 'insights.gap-ignored': 'Question ignored', 'insights.gap-open': 'Question reopened',
  'accesskey.create': 'User key created', 'accesskey.revoke': 'User key revoked', 'accesskey.audience': 'Key moved to another audience',
  'audience.create': 'Audience created', 'audience.update': 'Audience changed', 'audience.delete': 'Audience deleted', 'access.rules': 'Access rules saved', 'botlink.audience': 'Share link audience changed', 'auth.token': 'Signed in', 'application.create': 'Application created',
  'tenant.create': 'Company created', 'tenant.delete': 'Company deleted', 'tenant.disable': 'Company switched off', 'tenant.enable': 'Company switched on',
};
const bytes = b => b >= 1073741824 ? (b / 1073741824).toFixed(1) + ' GB' : b >= 1048576 ? (b / 1048576).toFixed(1) + ' MB' : Math.max(0, Math.round(b / 1024)) + ' KB';
const where = v => v.provider === 'r2' ? 'Cloudflare R2' : v.provider === 's3' ? 'Your own cloud' : v.provider === 'folder' ? 'Folder on your server' : 'This server';

export async function settingsView(main, ctx) {
  let alive = true;
  ctx.onCleanup(() => { alive = false; });
  const q = ctx.routeQuery();
  let tab = TABS.some(([k]) => k === q.get('tab')) ? q.get('tab') : 'storage';
  const body = h('div', { class: 'set-body' });
  const tabBar = h('div', { class: 'tabs', role: 'tablist', 'aria-label': 'Settings' });

  function paintTabs() {
    fill(tabBar, TABS.map(([k, label, ic]) => h('button', { type: 'button', role: 'tab', 'aria-selected': String(k === tab), onclick: () => show(k) }, icon(ic, 15), label)));
  }
  function show(t) { tab = t; paintTabs(); try { history.replaceState(null, '', '#/settings?tab=' + t); } catch { /* cosmetic */ } load(); }
  async function load() {
    const mine = tab;
    fill(body, h('div', { class: 'skel-set' }, h('i'), h('i'), h('i')));
    try {
      const view = mine === 'storage' ? await storageView() : mine === 'data' ? await dataView() : mine === 'activity' ? await activityView() : await securityView();
      if (alive && tab === mine) fill(body, view);
    } catch (e) { if (alive && tab === mine) fill(body, h('div', { class: 'notice warn', role: 'alert' }, e.message || 'Could not load this page.')); }
  }

  // ---- storage ------------------------------------------------------------------------------------------------------
  async function storageView() {
    const view = await api('GET', '/settings/storage');
    let sel = view.provider;
    const f = { accountId: view.accountId || '', endpoint: view.remoteType === 's3' ? view.endpoint : '', bucket: view.bucket || '', region: view.region || '', prefix: view.prefix || '', accessKeyId: '', secretAccessKey: '', folderPath: view.folderPath || '' };
    const status = h('div', { class: 'set-status', role: 'status', hidden: true });
    const formHost = h('div');
    const cardsHost = h('div', { class: 'prov-grid', role: 'radiogroup', 'aria-label': 'Where to store files' });
    let busy = false;

    const say = (kind, text) => { status.hidden = false; status.className = 'set-status ' + kind; fill(status, icon(kind === 'ok' ? 'check' : kind === 'bad' ? 'alert' : 'refresh', 16), h('span', null, text)); };
    const payload = () => ({ provider: sel, accountId: f.accountId, endpoint: f.endpoint, bucket: f.bucket, region: f.region, prefix: f.prefix, accessKeyId: f.accessKeyId, secretAccessKey: f.secretAccessKey, folderPath: f.folderPath });

    async function test() {
      busy = true; paintForm(); say('busy', 'Testing the connection…');
      try { const r = await api('POST', '/settings/storage/test', { body: payload() }); say(r.ok ? 'ok' : 'bad', r.message + (r.ok && r.milliseconds ? ` (${r.milliseconds} ms)` : '')); }
      catch (e) { say('bad', e.message); }
      busy = false; paintForm();
    }
    async function save() {
      busy = true; paintForm(); say('busy', sel === 'local' ? 'Saving…' : 'Testing, then saving…');
      try {
        await api('PUT', '/settings/storage', { body: payload() });
        toast(sel === 'local' ? 'New files will be stored on this server.' : sel === 'folder' ? 'Saved. New files will be stored in your server folder.' : 'Saved. New files will be stored in your cloud.', 'ok');
        if (alive) load();
      } catch (e) { say('bad', e.message); busy = false; paintForm(); }
    }

    const input = (key, props = {}) => h('input', { type: 'text', id: 'st-' + key, autocomplete: 'off', spellcheck: 'false', value: f[key], oninput: e => { f[key] = e.target.value; }, ...props });
    const field = (label, control, hint) => h('div', { class: 'field' }, h('label', { for: control.id }, label), control, hint ? h('small', { class: 'muted' }, hint) : null);

    function paintCards() {
      fill(cardsHost, PROVIDERS.map(([key, ic, title, blurb, chip]) => h('button', { type: 'button', role: 'radio', class: 'prov' + (sel === key ? ' on' : ''), 'aria-checked': String(sel === key),
        onclick: () => { sel = key; status.hidden = true; paintCards(); paintForm(); } },
        h('span', { class: 'prov-ic' }, icon(ic, 22)), h('span', { class: 'prov-t' }, h('strong', null, title), h('small', null, blurb)),
        h('span', { class: 'prov-foot' }, badge(chip, key === 'local' ? 'info' : 'review'), view.provider === key ? badge('In use', 'ok') : null),
        sel === key ? h('span', { class: 'prov-check', 'aria-hidden': 'true' }, icon('check', 14)) : null)));
    }

    function paintForm() {
      if (sel === 'local') {
        fill(formHost, h('div', { class: 'set-form' },
          h('p', { class: 'muted' }, "New files are saved in this server's private storage folder, outside the web site. Back that folder up regularly, because it is the only copy."),
          h('div', { class: 'set-actions' }, h('button', { class: 'btn btn-primary', type: 'button', disabled: busy || view.provider === 'local', onclick: save }, icon('check', 15), view.provider === 'local' ? 'Already in use' : 'Use this server'))));
        return;
      }
      if (sel === 'folder') {
        if (!view.folderEnabled) { fill(formHost, h('div', { class: 'notice warn' }, 'Folder storage is not switched on yet. The platform administrator needs to approve a location on this server first (Companies > Server folders, for example D:\\FlowMindDocs). Then come back and type your folder here.')); return; }
        fill(formHost, h('div', { class: 'set-form' },
          h('div', { class: 'formgrid' }, field('Folder path on the server', input('folderPath', { placeholder: view.folderRoots[0] || 'D:\\FlowMindDocs', maxlength: 400 }),
            'The folder must already exist and be reachable from the server that runs BBNavigator. Allowed: ' + view.folderRoots.join('; ') + '.')),
          h('div', { class: 'set-privacy' }, icon('lock', 15), h('span', null, 'Files are saved in a separate sub-folder per company. Give the account that runs BBNavigator (on IIS, the application pool identity) Modify permission on this folder, and back the folder up, because it holds the originals.')),
          h('div', { class: 'set-actions' },
            h('button', { class: 'btn', type: 'button', disabled: busy, onclick: test }, icon('refresh', 15), 'Test folder'),
            h('button', { class: 'btn btn-primary', type: 'button', disabled: busy, onclick: save }, icon('check', 15), view.provider === 'folder' ? 'Save changes' : 'Save and use this folder'))));
        return;
      }
      if (!view.secretsEnabled) { fill(formHost, h('div', { class: 'notice warn' }, 'Cloud storage needs a server secret to keep your access keys encrypted. Ask the person who runs this server to set Security:SecretsKey, then come back.')); return; }
      const saved = view.hasKeys && view.provider !== 'local' || view.hasKeys;
      const grid = [];
      if (sel === 'r2') grid.push(field('Cloudflare account id', input('accountId', { placeholder: '32 letters and digits', maxlength: 32 }), 'In the Cloudflare dashboard: R2 > Overview, on the right.'));
      else grid.push(field('Storage address', input('endpoint', { placeholder: 'https://storage.example.com', maxlength: 300 }), view.allowPrivateEndpoints ? 'Public or private network addresses are allowed on this server.' : 'Must be a public https address.'));
      grid.push(field('Bucket name', input('bucket', { placeholder: 'e.g. hms-documents', maxlength: 63 }), 'Create it first in your storage service. Keep it private.'));
      grid.push(field('Folder inside the bucket (optional)', input('prefix', { placeholder: 'e.g. flowmind', maxlength: 100 })));
      if (sel === 's3') grid.push(field('Region (optional)', input('region', { placeholder: 'us-east-1', maxlength: 40 })));
      grid.push(field('Access key id', input('accessKeyId', { placeholder: saved ? `Saved, ends ${view.accessKeyHint || '····'} (leave blank to keep)` : 'Access key id', maxlength: 128 })));
      grid.push(field('Secret access key', input('secretAccessKey', { type: 'password', placeholder: saved ? 'Saved (leave blank to keep)' : 'Secret access key', maxlength: 256, autocomplete: 'new-password' }), 'Stored encrypted on the server. It is never shown again.'));
      fill(formHost, h('div', { class: 'set-form' },
        h('div', { class: 'formgrid' }, grid),
        h('div', { class: 'set-privacy' }, icon('lock', 15), h('span', null, 'Your documents will be sent to this storage service. Use synthetic or non-confidential documents until your company has approved it.')),
        h('div', { class: 'set-actions' },
          h('button', { class: 'btn', type: 'button', disabled: busy, onclick: test }, icon('refresh', 15), 'Test connection'),
          h('button', { class: 'btn btn-primary', type: 'button', disabled: busy, onclick: save }, icon('check', 15), view.provider === sel ? 'Save changes' : 'Save and use this location'))));
    }

    // ---- summary at the top ----
    const tested = view.lastTest && view.lastTest.atUtc ? `${view.lastTest.ok ? 'Connected' : 'Last test failed'} · ${relativeTime(view.lastTest.atUtc)}` : 'Not tested yet';
    const heroIc = view.provider === 'r2' || view.provider === 's3' ? 'cloud' : view.provider === 'folder' ? 'server' : 'drive';
    const hero = h('section', { class: 'set-hero' },
      h('span', { class: 'set-hero-ic' }, icon(heroIc, 26)),
      h('div', { class: 'set-hero-t' }, h('small', null, 'New files are stored in'), h('strong', null, where(view) + (view.provider === 'folder' ? ` · ${view.folderPath}` : view.provider !== 'local' && view.bucket ? ` · ${view.bucket}` : '')),
        view.provider === 'local' ? badge('Private folder on this server', 'info') : badge(tested, view.lastTest && view.lastTest.ok === false ? 'bad' : 'ok')),
      h('div', { class: 'set-hero-n' },
        h('div', null, h('b', null, String(view.local.files)), h('span', null, `on this server · ${bytes(view.local.bytes)}`)),
        h('div', null, h('b', null, String(view.remote.files)), h('span', null, `in the cloud · ${bytes(view.remote.bytes)}`)),
        view.folder.files > 0 || view.provider === 'folder' ? h('div', null, h('b', null, String(view.folder.files)), h('span', null, `in your server folder · ${bytes(view.folder.bytes)}`)) : null));

    // ---- move existing files ----
    const prog = h('div', { class: 'set-prog', hidden: true }, h('div', { class: 'ai-bar' }, h('i')), h('small', { class: 'muted' }));
    const removeBox = h('input', { type: 'checkbox', id: 'st-remove' });
    const failures = h('ul', { class: 'set-fails' });
    async function copyAll(to) {
      const total = to === 'remote' ? view.local.files + view.folder.files : to === 'folder' ? view.local.files + view.remote.files : view.remote.files + view.folder.files;
      const name = to === 'remote' ? 'your cloud' : to === 'folder' ? 'your server folder' : 'this server';
      if (!await confirmDialog(`Copy ${total} file${total === 1 ? '' : 's'} to ${name}? Each copy is checked against the original before it is switched over.${removeBox.checked ? ' The old copies will then be removed.' : ' The old copies stay where they are.'}`, 'Copy files')) return;
      prog.hidden = false; fill(failures);
      const bar = prog.querySelector('i'), label = prog.querySelector('small'); let moved = 0, failed = 0;
      for (;;) {
        let r;
        try { r = await api('POST', '/settings/storage/migrate', { body: { to, removeSource: removeBox.checked, take: 10 } }); }
        catch (e) { toast(e.message, 'bad'); break; }
        moved += r.moved; failed += r.failed.length;
        r.failed.forEach(x => failures.append(h('li', null, h('strong', null, x.file), ': ' + x.reason)));
        bar.style.width = Math.min(100, Math.round(((moved + failed) / Math.max(1, total)) * 100)) + '%'; label.textContent = `${moved} of ${total} copied` + (failed ? `, ${failed} failed` : '');
        if (r.moved === 0 || r.remaining === 0) break;
      }
      toast(failed ? `${moved} copied, ${failed} could not be copied. See the list below.` : `${moved} file${moved === 1 ? '' : 's'} copied.`, failed ? 'bad' : 'ok');
      if (alive) { await new Promise(r => setTimeout(r, failed ? 0 : 600)); if (!failed) load(); }
    }
    const toCloud = view.local.files + view.folder.files, toFolder = view.local.files + view.remote.files, back = view.remote.files + view.folder.files;
    const canUp = view.hasKeys && toCloud > 0, canFolder = view.folderEnabled && !!view.folderPath && toFolder > 0, canDown = back > 0;
    const plural = n => `${n} file${n === 1 ? '' : 's'}`;
    const files = h('section', { class: 'card set-card' },
      h('div', { class: 'card-title' }, icon('refresh', 18), h('h3', null, 'Files already stored')),
      h('p', { class: 'muted' }, 'Changing the location above only affects new uploads. Existing files stay where they are and keep working. Copy or move them across when you are ready. Once a file is switched over, BBNavigator opens it from the new location.'),
      h('div', { class: 'set-actions' },
        h('button', { class: 'btn', type: 'button', disabled: !canUp, onclick: () => copyAll('remote') }, icon('upload', 15), canUp ? `Copy ${plural(toCloud)} to the cloud` : 'Nothing to copy to the cloud'),
        h('button', { class: 'btn', type: 'button', disabled: !canFolder, onclick: () => copyAll('folder') }, icon('server', 15), canFolder ? `Copy ${plural(toFolder)} to the server folder` : 'Nothing to copy to the server folder'),
        h('button', { class: 'btn', type: 'button', disabled: !canDown, onclick: () => copyAll('local') }, icon('down', 15), canDown ? `Copy ${plural(back)} back to this server` : 'Nothing to copy back to this server'),
        h('label', { class: 'set-check', for: 'st-remove' }, removeBox, 'Move instead of copy: remove each file from the old location once the new copy is checked')),
      !view.hasKeys && !view.folderPath ? h('p', { class: 'muted small' }, 'Save a cloud location or a server folder above before copying files to it.') : null,
      prog, failures);

    const tips = h('section', { class: 'card set-card' },
      h('div', { class: 'card-title' }, icon('shield', 18), h('h3', null, 'Good practice')),
      h('ul', { class: 'set-tips' },
        h('li', null, h('strong', null, 'Keep the bucket private. '), 'Never make it public. BBNavigator reads files through its own key.'),
        h('li', null, h('strong', null, 'Limit the key. '), 'Create a key that can only read and write this one bucket.'),
        h('li', null, h('strong', null, 'Test before relying on it. '), 'A location is only saved after a test file has been written, read and removed.'),
        h('li', null, h('strong', null, 'Nothing is lost on a switch. '), 'Each file remembers where it is, so older files keep opening after you change the location.'),
        h('li', null, h('strong', null, 'The databases are separate. '), 'Text, knowledge and chats stay in the server database (see "Where your data lives").')));

    paintCards(); paintForm();
    return h('div', { class: 'set-grid' }, hero, h('section', { class: 'card set-card' }, h('div', { class: 'card-title' }, icon('sliders', 18), h('h3', null, 'Storage location')), cardsHost, formHost, status), files, tips);
  }

  // ---- where everything lives ---------------------------------------------------------------------------------------
  async function dataView() {
    const o = await api('GET', '/settings/overview');
    const IC = ['drive', 'database', 'chat', 'search', 'lock'];
    const tile = (n, label) => h('div', { class: 'set-tile' }, h('b', null, String(n)), h('span', null, label));
    return h('div', { class: 'set-grid' },
      h('div', { class: 'set-tiles' }, tile(o.documents, 'documents'), tile(o.pages, 'pages of text'), tile(o.knowledgeItems, 'knowledge items'), tile(o.chats, 'chats'), tile(o.supportRequests, 'support requests')),
      h('section', { class: 'card set-card' }, h('div', { class: 'card-title' }, icon('database', 18), h('h3', null, 'Where each kind of data is kept')),
        h('ul', { class: 'loc-list' }, o.locations.map((l, i) => h('li', null, h('span', { class: 'loc-ic' }, icon(IC[i] || 'database', 18)),
          h('div', { class: 'loc-t' }, h('strong', null, l.what), h('small', null, l.detail)), h('span', { class: 'loc-where' }, l.where))))),
      h('div', { class: 'notice' }, 'Only original files can be moved to a cloud from here. The databases run on the server BBNavigator is installed on. To keep them in your own cloud database instead, the person who runs the server changes the connection settings (ConnectionStrings in the .env file); that is a server setting, not a company one.'));
  }

  // ---- activity -----------------------------------------------------------------------------------------------------
  async function activityView() {
    const items = await api('GET', '/settings/activity?take=60');
    if (!items.length) return empty('No activity yet', 'Uploads, approvals, setting changes and similar actions are listed here.', 'activity');
    return h('section', { class: 'card set-card' }, h('div', { class: 'card-title' }, icon('activity', 18), h('h3', null, 'Recent activity')),
      h('ol', { class: 'timeline' }, items.map(a => h('li', null, h('i', { class: 'tl-dot', 'aria-hidden': 'true' }),
        h('div', { class: 'tl-t' }, h('strong', null, LABELS[a.action] || a.action.replace(/[._-]/g, ' ')), a.detail ? h('small', null, a.detail) : null, h('small', { class: 'muted' }, a.actor)),
        h('time', { datetime: a.timeUtc, title: formatDate(a.timeUtc) }, relativeTime(a.timeUtc))))));
  }

  // ---- security -----------------------------------------------------------------------------------------------------
  async function securityView() {
    const checks = await api('GET', '/settings/security');
    return h('section', { class: 'card set-card' }, h('div', { class: 'card-title' }, icon('shield', 18), h('h3', null, 'Security checklist')),
      h('ul', { class: 'sec-list' }, checks.map(c => h('li', null,
        h('span', { class: 'sec-ic ' + (c.ok === true ? 'ok' : c.ok === false ? 'bad' : 'info') }, icon(c.ok === true ? 'check' : c.ok === false ? 'alert' : 'info', 16)),
        h('div', null, h('strong', null, c.name), h('small', null, c.detail))))));
  }

  main.append(
    h('div', { class: 'page-head' }, h('div', null, h('h2', null, 'Settings'), h('p', null, 'Where your files are stored, where all your data lives, who did what, and how well protected it is.'))),
    tabBar, body);
  paintTabs();
  await load();
}
