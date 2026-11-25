/*
 * BBNavigator demo mode. Used ONLY on the GitHub Pages copy of the site (see scripts/build-pages.ps1); the real server never loads this file.
 *
 * GitHub Pages can only host static files, so there is no API, database or AI behind the demo. This script replaces window.fetch for the
 * app's own API addresses (/api/v1, /api/bot, /health) and answers from fixtures.json: real responses recorded from a running copy of the
 * app, filled with made-up sample documents. Edits (approving, creating a link, chatting) are kept in memory for the page's lifetime and are
 * gone on reload. No data leaves the browser and nothing here talks to an AI provider; the chat answers are canned from the sample documents.
 */
(function () {
  'use strict';
  const BASE = new URL('.', document.currentScript.src).href;
  const realFetch = window.fetch.bind(window);
  const DAY = 864e5, HOUR = 36e5;
  const clone = o => JSON.parse(JSON.stringify(o));
  const uid = () => (window.crypto && crypto.randomUUID) ? crypto.randomUUID() : 'xxxxxxxx-xxxx-4xxx-yxxx-xxxxxxxxxxxx'.replace(/[xy]/g, c => { const r = Math.random() * 16 | 0; return (c === 'x' ? r : (r & 3 | 8)).toString(16); });
  const iso = ms => new Date(ms).toISOString();
  const ymd = ms => iso(ms).slice(0, 10);
  const sleep = ms => new Promise(r => setTimeout(r, ms));

  class Fail extends Error { constructor(status, detail) { super(detail); this.status = status; } }

  // ---------------------------------------------------------------- people you can sign in as
  const ACCOUNTS = {
    'asha.kumar@demo-hospital.example':      { name: 'Asha Kumar',      role: 'TenantAdmin',   audience: null },
    'priya.nair@demo-hospital.example':      { name: 'Priya Nair',      role: 'TenantUser',    audience: 'Front Desk' },
    'platform.admin@bbnavigator.example':    { name: 'Platform Admin',  role: 'PlatformAdmin', audience: null },
  };
  const SLUG = 'demo-hospital';

  const b64 = o => btoa(JSON.stringify(o)).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '');
  const unb64 = s => { try { return JSON.parse(atob(s.replace(/-/g, '+').replace(/_/g, '/'))); } catch { return null; } };
  function issue(email, over) {
    const a = ACCOUNTS[email] || { name: email.split('@')[0], role: 'TenantUser', audience: null };
    const claims = { sub: 'user:' + (a.id || uid()), role: a.role, tid: a.role === 'PlatformAdmin' ? undefined : SLUG, name: (over && over.name) || a.name, email, exp: Math.floor((Date.now() + 12 * HOUR) / 1000) };
    return { accessToken: b64({ alg: 'none', typ: 'JWT' }) + '.' + b64(claims) + '.demo', expiresUtc: iso(Date.now() + 12 * HOUR) };
  }
  function whoIs(init) {
    const h = (init && init.headers) || {};
    const auth = h.Authorization || h.authorization || '';
    const t = auth.replace(/^Bearer\s+/i, '').split('.');
    return t.length === 3 ? unb64(t[1]) : null;
  }

  // ---------------------------------------------------------------- data
  let DB = null;           // everything the pages read, as lists you can edit during the visit
  const FX = new Map();    // recorded GET responses, keyed "GET /path?query" with applicationId and take removed

  const normKey = (method, url) => {
    const u = new URL(url, 'http://x');
    ['applicationId', 'take'].forEach(p => u.searchParams.delete(p));
    const qs = u.searchParams.toString();
    return method + ' ' + u.pathname + (qs ? '?' + qs : '');
  };
  const fx = (method, pathAndQuery) => { const v = FX.get(normKey(method, pathAndQuery)); return v === undefined ? undefined : clone(v); };

  // The recording was made on one day; move every timestamp so the sample data always looks recent.
  const REF = Date.parse('2026-11-25T05:00:00Z');
  function shiftDates(v, delta) {
    if (typeof v === 'string') {
      if (/^\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d(\.\d+)?Z?$/.test(v)) return iso(Date.parse(v.endsWith('Z') ? v : v + 'Z') + delta);
      return v;
    }
    if (Array.isArray(v)) return v.map(x => shiftDates(x, delta));
    if (v && typeof v === 'object') { const o = {}; for (const k of Object.keys(v)) o[k] = shiftDates(v[k], delta); return o; }
    return v;
  }

  function seeded(seed) { let a = seed >>> 0; return () => { a |= 0; a = a + 0x6D2B79F5 | 0; let t = Math.imul(a ^ a >>> 15, 1 | a); t = t + Math.imul(t ^ t >>> 7, 61 | t) ^ t; return ((t ^ t >>> 14) >>> 0) / 4294967296; }; }

  function init(raw) {
    const delta = Date.now() - REF;
    for (const k of Object.keys(raw)) {
      const [m, ...rest] = k.split(' ');
      FX.set(normKey(m, rest.join(' ')), shiftDates(raw[k], delta));
    }
    const apps = fx('GET', '/applications');
    const APP = apps[0];
    const docsRec = fx('GET', '/documents');
    const now = Date.now();

    DB = {
      appId: APP.id,
      apps,
      audiences: fx('GET', '/access/audiences'),
      users: fx('GET', '/users'),
      links: fx('GET', '/bot-links'),
      keys: [],
      docs: docsRec,
      docStatus: {}, docPages: {},
      review: fx('GET', '/review?status=Approved'),
      convs: fx('GET', '/conversations'),
      convMsgs: {},
      visitor: { convs: [], msgs: {} },
      tenants: [], platformAdmins: [], folders: [],
      tokens: {},
      gaps: [], support: [],
    };

    // documents: the recorded PDF waited for OCR (not configured on the recording machine). Show it as finished, and add one waiting for review.
    for (const d of DB.docs) {
      const st = fx('GET', `/documents/${d.id}/status`), pg = fx('GET', `/documents/${d.id}/pages`);
      DB.docStatus[d.id] = st; DB.docPages[d.id] = pg;
      if (d.status === 'AwaitingOcr') {
        d.status = 'Approved'; st.status = 'Approved'; st.pagesDigital = 3; st.pagesOcr = 0; st.pagesAwaitingOcr = 0;
        st.latestJob = { stage: 'Embedding', status: 'Succeeded', attempts: 1, error: null, createdUtc: iso(now - 3 * DAY), startedUtc: iso(now - 3 * DAY), completedUtc: iso(now - 3 * DAY + 4000) };
      }
    }
    const pdf = DB.docs.find(d => d.fileName === 'sample-registration.pdf');
    if (pdf) DB.docPages[pdf.id] = [
      { pageNumber: 1, source: 'Digital', hasImages: false, charCount: 72, text: 'Patient Registration: the user enters demographics and saves the record.' },
      { pageNumber: 2, source: 'Digital', hasImages: false, charCount: 96, text: 'Duplicate check: a patient is blocked when the National ID already exists in the register.' },
      { pageNumber: 3, source: 'Digital', hasImages: false, charCount: 71, text: 'After saving, the front-desk user continues to Appointment Booking.' }];
    addDocument('sample-discharge-policy.docx', 'PendingReview', [
      'Discharge Policy\nA patient is discharged only after the treating doctor signs the discharge summary.',
      'The billing desk settles the final invoice before the patient leaves the ward.'], now - 2 * HOUR);

    // items waiting for review (from the discharge policy document)
    const src = { document: 'sample-discharge-policy.docx', page: 1 };
    DB.review.push(
      { type: 'page', id: uid(), title: 'Patient Discharge', text: 'Record the discharge of a patient from the ward.', review: 'Pending', source: src },
      { type: 'validation', id: uid(), title: 'Discharge summary', text: 'Must be signed by the treating doctor before discharge.', review: 'Pending', source: src },
      { type: 'businessrule', id: uid(), title: 'Final invoice', text: 'The final invoice must be settled before the patient leaves the ward.', review: 'Pending', source: { document: 'sample-discharge-policy.docx', page: 2 } },
      { type: 'role', id: uid(), title: 'Billing desk', text: 'Settles the final invoice and releases the patient.', review: 'Pending', source: { document: 'sample-discharge-policy.docx', page: 2 } });

    // chats
    for (const c of DB.convs) DB.convMsgs[c.id] = fx('GET', `/conversations/${c.id}`);
    for (const [t, ago] of [['What are the steps of an outpatient visit?', 5], ['Can a doctor create a registration?', 26], ['How do I cancel an appointment?', 49]]) {
      const id = uid(); DB.convs.push({ id, title: t, updatedUtc: iso(now - ago * HOUR) });
      DB.convMsgs[id] = makeConversation(id, t, now - ago * HOUR);
    }
    DB.convs.sort((a, b) => b.updatedUtc.localeCompare(a.updatedUtc));

    // share links: the recorded one plus a second, open to every approved document
    const l1 = DB.links[0]; l1.todayMessages = 14; l1.storedChats = 41; l1.audienceId = DB.audiences.find(a => a.name === 'Front Desk')?.id || null; l1.audienceName = l1.audienceId ? 'Front Desk' : null;
    const l2 = clone(l1); Object.assign(l2, { id: uid(), name: 'clinic-visitors', title: 'Clinic Visitor Help', token: 'demo-clinic-visitors-token', welcome: 'Welcome. Ask about registration or appointments.', todayMessages: 3, storedChats: 9, audienceId: null, audienceName: null, accentColor: '#0a7a43', starters: ['How do I book an appointment?', 'What if I need to cancel?'] });
    DB.links.push(l2);
    for (const a of DB.audiences) { a.keys = a.name === 'Front Desk' ? 2 : 0; a.links = a.name === 'Front Desk' ? 1 : 0; a.decisions = a.name === 'Front Desk' ? 6 : 2; }

    // access keys (one for an integration)
    const fd = DB.audiences.find(a => a.name === 'Front Desk');
    DB.keys.push({ id: uid(), name: 'Reception tablet', prefix: 'bbn_7f3a', active: true, audienceId: fd ? fd.id : null, audienceName: fd ? 'Front Desk' : null, createdUtc: iso(now - 9 * DAY), lastUsedUtc: iso(now - 3 * HOUR), canCopy: true });

    // people
    const priya = DB.users.find(u => /priya/i.test(u.email));
    if (priya) { priya.pendingInvite = false; priya.lastLoginUtc = iso(now - 5 * HOUR); ACCOUNTS[priya.email] = ACCOUNTS[priya.email] || {}; }
    DB.users.push({ id: uid(), email: 'ravi.menon@demo-hospital.example', displayName: 'Dr Ravi Menon', role: 'TenantUser', audienceId: DB.audiences.find(a => a.name === 'Doctors')?.id || null, audience: 'Doctors', active: true, pendingInvite: true, locked: false, createdUtc: iso(now - 1 * DAY), lastLoginUtc: null });
    const asha = DB.users.find(u => u.role === 'TenantAdmin'); if (asha) asha.lastLoginUtc = iso(now - 20 * 60000);

    // platform view
    const t0 = fx('GET', '/tenants')[0];
    DB.tenants = [Object.assign(t0, { name: 'Demo Hospital', slug: SLUG, applications: 1, documents: 4, userKeys: 1 }),
      { id: uid(), name: 'Northside Clinic', slug: 'northside-clinic', isActive: true, createdUtc: iso(now - 40 * DAY), applications: 2, documents: 18, userKeys: 3, keyViewable: true },
      { id: uid(), name: 'Riverside Medical Group', slug: 'riverside-medical', isActive: false, createdUtc: iso(now - 95 * DAY), applications: 1, documents: 7, userKeys: 0, keyViewable: false }];
    DB.platformAdmins = [{ id: uid(), email: 'platform.admin@bbnavigator.example', displayName: 'Platform Admin', role: 'PlatformAdmin', active: true, pendingInvite: false, locked: false, createdUtc: iso(now - 60 * DAY), lastLoginUtc: iso(now - 2 * HOUR) }];
    DB.folders = [{ id: uid(), path: 'D:\\BBNavigatorDocs', exists: true, fromConfig: false, companies: 1 }];

    // unanswered questions and support requests
    const g = (q, n, state, closest, ch, ago, reopened) => ({ key: 'q:' + q.toLowerCase().replace(/\W+/g, '-'), question: q, count: n, reopened: !!reopened, lastAskedUtc: iso(now - ago * HOUR), channels: ch, closest, state });
    DB.gaps = [
      g('Can a doctor cancel an appointment for a patient?', 4, 'open', [{ document: 'sample-hospital-guide.txt', page: 2 }], ['clinic-visitors'], 3),
      g('What is the refund policy for cancelled visits?', 3, 'open', [{ document: 'sample-billing.docx', page: 1 }], ['front-desk-help'], 20),
      g('Where do I record insurance details?', 2, 'open', [], ['front-desk-help', 'Signed-in users'], 31),
      g('How do I merge two duplicate patient records?', 1, 'open', [{ document: 'sample-registration.pdf', page: 2 }], ['Signed-in users'], 70, true),
      g('Who approves a discount above 20 percent?', 5, 'handled', [{ document: 'sample-billing.docx', page: 1 }], ['front-desk-help'], 96),
      g('What is the weather today?', 2, 'ignored', [], ['clinic-visitors'], 130)];
    DB.support = [
      { id: uid(), question: 'Can a doctor cancel an appointment for a patient?', status: 'open', wasAnswered: false, createdUtc: iso(now - 3 * HOUR), channel: 'clinic-visitors', note: 'A patient called to cancel while at the ward.', contact: 'ext 4417', resolution: null },
      { id: uid(), question: 'How do I refund a duplicate payment?', status: 'open', wasAnswered: true, createdUtc: iso(now - 26 * HOUR), channel: 'front-desk-help', note: '', contact: null, resolution: null },
      { id: uid(), question: 'Where is the discharge form kept?', status: 'done', wasAnswered: false, createdUtc: iso(now - 5 * DAY), channel: 'front-desk-help', note: '', contact: 'priya.nair', resolution: 'Added to the discharge policy document.' }];
  }

  function addDocument(fileName, status, pages, createdMs) {
    const id = uid();
    DB.docs.push({ id, applicationId: DB.appId, fileName, status, createdUtc: iso(createdMs || Date.now()) });
    DB.docPages[id] = pages.map((t, i) => ({ pageNumber: i + 1, source: 'Digital', hasImages: false, charCount: t.length, text: t }));
    DB.docStatus[id] = { id, fileName, status, versionNumber: 1, pageCount: pages.length, pagesDigital: pages.length, pagesOcr: 0, pagesAwaitingOcr: 0,
      contentHash: Array.from({ length: 64 }, () => '0123456789abcdef'[Math.random() * 16 | 0]).join(''),
      latestJob: { stage: 'Analysis', status: 'Succeeded', attempts: 1, error: null, createdUtc: iso(createdMs || Date.now()), startedUtc: iso(createdMs || Date.now()), completedUtc: iso((createdMs || Date.now()) + 5000) } };
    return id;
  }

  // ---------------------------------------------------------------- chat answers (canned from the sample documents)
  const SRC = { guide: 'sample-hospital-guide.txt', bill: 'sample-billing.docx', reg: 'sample-registration.pdf' };
  const QA = [
    { re: /\b(hi|hello|hey|good (morning|afternoon|evening))\b|^thanks?\b|thank you/i, small: true,
      a: 'Hello! I answer questions about registration, appointments and billing from your approved documents. What would you like to know?' },
    { re: /(steps?|flow|workflow|outpatient|process).*(visit|outpatient)|outpatient/i,
      a: 'The **outpatient visit** goes in this order:\n\n1. **Register patient**\n2. **Book appointment**\n3. **Doctor consultation**\n4. **Generate invoice**', src: [[SRC.guide, 2]] },
    { re: /discount|approve.*(invoice|bill)|billing manager|refund/i,
      a: 'Only users with the **Billing Manager** role can approve a discount, and a discount cannot exceed **20 percent** of the invoice total. Billing Managers can also issue refunds; front-desk staff can only collect payments.', src: [[SRC.guide, 3], [SRC.bill, 1]] },
    { re: /cancel|resched/i,
      a: 'Appointments can be cancelled **up to 2 hours before** the time slot.', src: [[SRC.guide, 2]] },
    { re: /(validation|validate|required|mandatory|rules?).*(regist|patient)|regist.*(validation|rules?|mandatory)/i,
      a: 'The Patient Registration page requires the following validations:\n- **Full name** is mandatory.\n- **Date of birth** cannot be in the future.\n- **Phone number** must contain exactly 10 digits.\n- **National ID** must be unique across all patients.', src: [[SRC.guide, 1]] },
    { re: /after (saving|save)|what happens after|next page/i,
      a: 'After saving, the user is taken to the **Appointment Booking** page.', src: [[SRC.guide, 1]] },
    { re: /regist|new patient|national id|duplicate/i,
      a: 'Register a new patient by using the **Patient Registration** page.\n\n**Steps**\n\n1. **Open Patient Registration page** – Front‑desk staff go to the **Patient Registration** page.\n2. **Enter required information** –\n   - **Full name** (mandatory)\n   - **Date of birth** (cannot be in the future)\n   - **Gender**\n   - **Phone number** (exactly 10 digits)\n   - **National ID** (must be unique)\n3. **Save the record** – After saving, the system takes you to the **Appointment Booking** page.', src: [[SRC.guide, 1], [SRC.reg, 1]] },
    { re: /book|appointment|slot|doctor.*(twice|double)/i,
      a: 'On the **Appointment Booking** page, front-desk staff choose the patient, department, doctor, date and time slot.\n\n- The appointment date cannot be in the past.\n- A doctor cannot be booked twice for the same time slot.\n- Department is mandatory.', src: [[SRC.guide, 2]] },
    { re: /role|who can|permission|doctor.*(create|edit|view)|front.?desk.*(create|edit)/i,
      a: 'Front-desk staff can create and edit registrations; doctors can only view them. For billing, the **Billing Manager** approves discounts and issues refunds, while front-desk staff only collect payments.', src: [[SRC.guide, 1], [SRC.guide, 3]] },
    { re: /invoice|bill|payment/i,
      a: 'The **Invoice** page is generated after a consultation is completed. Payment mode is mandatory, and a discount cannot exceed 20 percent of the invoice total.', src: [[SRC.bill, 1]] },
  ];
  function answerFor(text) {
    const t = String(text || '');
    const hit = QA.find(q => q.re.test(t));
    if (hit) return { answer: hit.a, documented: !hit.small, sources: (hit.src || []).map(([document, page]) => ({ document, page })) };
    return { answer: 'I could not find that in the approved documents, so I would rather not guess. You can send this question to the team and they can add the missing information.', documented: false, sources: [] };
  }
  function makeConversation(id, title, ms) {
    const a = answerFor(title);
    return { id, applicationId: DB.appId, title, messages: [
      { role: 'user', content: title, documented: null, sources: [], createdUtc: iso(ms) },
      { role: 'assistant', content: a.answer, documented: a.documented, sources: a.sources, createdUtc: iso(ms + 4000) }] };
  }
  function noteGap(question) {
    const key = 'q:' + question.toLowerCase().replace(/\W+/g, '-');
    const g = DB.gaps.find(x => x.key === key);
    if (g) { g.count++; g.lastAskedUtc = iso(Date.now()); if (g.state !== 'open') { g.state = 'open'; g.reopened = true; } }
    else DB.gaps.unshift({ key, question, count: 1, reopened: false, lastAskedUtc: iso(Date.now()), channels: ['Signed-in users'], closest: [], state: 'open' });
  }

  // ---------------------------------------------------------------- generated reports
  function usageReport(days) {
    const rng = seeded(days * 7919 + 13), now = Date.now(), today = Date.parse(ymd(now));
    const daily = [];
    for (let i = days - 1; i >= 0; i--) {
      const ms = today - i * DAY, dow = new Date(ms).getUTCDay();
      const base = (dow === 0 || dow === 6) ? 7 : 30, growth = 0.75 + 0.5 * ((days - i) / days);
      const q = Math.round(base * growth * (0.7 + rng() * 0.6));
      daily.push({ date: ymd(ms), questions: q, notDocumented: Math.round(q * (0.03 + rng() * 0.09)) });
    }
    const questions = daily.reduce((s, d) => s + d.questions, 0), notDocumented = daily.reduce((s, d) => s + d.notDocumented, 0), answered = questions - notDocumented;
    const top = [['How do I register a new patient?', .16, true], ['Who can approve a discount?', .12, true], ['What validations apply when registering a patient?', .10, true],
      ['How do I cancel an appointment?', .09, true], ['What are the steps of an outpatient visit?', .08, true], ['Can a doctor cancel an appointment for a patient?', .05, false]];
    const openGaps = DB.gaps.filter(g => g.state === 'open').length;
    return { application: 'Hospital System', fromUtc: iso(now - days * DAY), toUtc: iso(now), days, questions, answered, notDocumented,
      answerRatePercent: questions ? Math.round(answered / questions * 100) : 0, previousQuestions: Math.round(questions * 0.82), conversations: Math.round(questions * 0.55),
      people: Math.max(2, Math.round(questions / 7)), activeDays: daily.filter(d => d.questions > 0).length, tamilQuestions: Math.round(questions * 0.07), daily,
      topQuestions: top.map(([question, f, allAnswered]) => ({ question, count: Math.max(1, Math.round(questions * f)), allAnswered })),
      channels: [{ name: 'front-desk-help', questions: Math.round(questions * .58) }, { name: 'Signed-in users', questions: Math.round(questions * .32) }, { name: 'clinic-visitors', questions: questions - Math.round(questions * .58) - Math.round(questions * .32) }],
      supportReceived: DB.support.length, supportClosed: DB.support.filter(s => s.status === 'done').length, supportOpenNow: DB.support.filter(s => s.status === 'open').length,
      openGaps, documentsChanged: DB.docs.length, truncated: false };
  }
  function linkUsage(days) {
    const rng = seeded(days * 31 + 5), f = days / 30;
    const rows = DB.links.map((l, i) => {
      const questions = Math.round((i === 0 ? 410 : 96) * f * (0.9 + rng() * 0.2)), aiCalls = Math.round(questions * 0.78), inputTokens = aiCalls * 1180, outputTokens = aiCalls * 140;
      return { linkId: l.id, name: l.name, exists: true, active: l.active, questions, aiCalls, inputTokens, outputTokens, embeddingUnits: questions * 18, cachedAnswers: questions - aiCalls, failed: Math.round(questions * 0.01),
        totalTokens: inputTokens + outputTokens, tokensPerCall: aiCalls ? Math.round((inputTokens + outputTokens) / aiCalls) : 0, sharePercent: 0 };
    });
    const other = Math.round(52000 * f), total = rows.reduce((s, r) => s + r.totalTokens, 0) + other;
    rows.forEach(r => { r.sharePercent = Math.round(r.totalTokens / total * 1000) / 10; });
    return { days, links: rows, otherTokens: other, totalTokens: total };
  }

  // ---------------------------------------------------------------- routes
  const R = [];
  const on = (m, re, fn, open) => R.push({ m, re, fn, open });
  const findBy = (list, id) => list.find(x => x.id === id);
  const needAdmin = ctx => { if (!ctx.who || ctx.who.role === 'TenantUser') throw new Fail(403, 'You do not have access to this.'); };
  const needPlatform = ctx => { if (!ctx.who || ctx.who.role !== 'PlatformAdmin') throw new Fail(403, 'You do not have access to this.'); };
  const linkFor = (token, fragment) => location.href.split('#')[0] + '#/' + fragment + '?token=' + encodeURIComponent(token);
  const newToken = (purpose, email, displayName) => { const t = 'demo-' + purpose + '-' + uid().slice(0, 8); DB.tokens[t] = { purpose, email, displayName }; return t; };
  const member = (list, body, role) => {
    const aud = body.audienceId ? DB.audiences.find(a => a.id === body.audienceId) : null;
    const m = { id: uid(), email: String(body.email || '').trim().toLowerCase(), displayName: body.name || body.email, role: role || body.role || 'TenantUser', audienceId: aud ? aud.id : null, audience: aud ? aud.name : null, active: true, pendingInvite: true, locked: false, createdUtc: iso(Date.now()), lastLoginUtc: null };
    if (list.some(x => x.email === m.email)) throw new Fail(409, 'Someone with this email already has an account.');
    list.push(m); return m;
  };

  // ---- signing in
  on('POST', /^\/auth\/login$/, ctx => {
    const email = String((ctx.body && ctx.body.email) || '').trim().toLowerCase();
    const known = ACCOUNTS[email] || DB.users.some(u => u.email === email && !u.pendingInvite);
    if (!email || !ctx.body.password || !known) throw new Fail(401, 'That email and password do not match.');
    const u = DB.users.find(x => x.email === email); if (u) u.lastLoginUtc = iso(Date.now());
    return issue(email, u ? { name: u.displayName } : null);
  }, true);
  on('POST', /^\/auth\/token$/, ctx => {
    const k = String((ctx.body && ctx.body.apiKey) || '');
    if (/platform/i.test(k)) return issue('platform.admin@bbnavigator.example');
    if (k) return issue('asha.kumar@demo-hospital.example');
    throw new Fail(401, 'That company ID and key do not match.');
  }, true);
  on('GET', /^\/auth\/options$/, () => ({ emailEnabled: false, minPasswordLength: 10 }), true);
  on('POST', /^\/auth\/forgot$/, () => ({ message: 'If that email has an account, a link to choose a new password is on its way.' }), true);
  on('GET', /^\/auth\/token-info$/, ctx => { const t = DB.tokens[ctx.q.get('token')]; if (!t) throw new Fail(404, 'Not found.'); return { purpose: t.purpose, email: t.email, displayName: t.displayName }; }, true);
  on('POST', /^\/auth\/reset$/, ctx => {
    const t = DB.tokens[ctx.body && ctx.body.token]; if (!t) throw new Fail(400, 'This link has expired or was already used.');
    delete DB.tokens[ctx.body.token];
    const u = DB.users.find(x => x.email === t.email) || DB.platformAdmins.find(x => x.email === t.email);
    if (u) { u.pendingInvite = false; u.lastLoginUtc = iso(Date.now()); ACCOUNTS[t.email] = ACCOUNTS[t.email] || { name: u.displayName, role: u.role, audience: u.audience }; }
    return issue(t.email, { name: t.displayName });
  }, true);

  // ---- account
  on('GET', /^\/account\/me$/, ctx => {
    const w = ctx.who; if (!w || !w.email) throw new Fail(404, 'Not found.');
    const a = ACCOUNTS[w.email] || {};
    return { kind: 'account', profile: { id: uid(), email: w.email, displayName: w.name, role: w.role, companyName: w.role === 'PlatformAdmin' ? null : 'Demo Hospital', companyId: w.role === 'PlatformAdmin' ? null : SLUG, audience: a.audience || null, lastLoginUtc: iso(Date.now() - 20 * 60000), passwordChangedUtc: iso(Date.now() - 12 * DAY) } };
  });
  on('PUT', /^\/account\/me$/, ctx => { const w = ctx.who; if (ACCOUNTS[w.email]) ACCOUNTS[w.email].name = ctx.body.displayName; return issue(w.email, { name: ctx.body.displayName }); });
  on('POST', /^\/account\/password$/, ctx => issue(ctx.who.email));
  on('POST', /^\/account\/sign-out-everywhere$/, ctx => issue(ctx.who.email));

  // ---- team
  on('GET', /^\/users$/, ctx => { needAdmin(ctx); return DB.users; });
  on('POST', /^\/users$/, ctx => { needAdmin(ctx); const m = member(DB.users, ctx.body); return { member: m, emailSent: false, link: linkFor(newToken('invite', m.email, m.displayName), 'invite') }; });
  on('PATCH', /^\/users\/([^/]+)$/, ctx => {
    needAdmin(ctx); const u = findBy(DB.users, ctx.m[1]); if (!u) throw new Fail(404, 'Not found.');
    const b = ctx.body || {};
    if ('active' in b) u.active = !!b.active;
    if (b.role) u.role = b.role;
    if ('audienceId' in b) { const a = DB.audiences.find(x => x.id === b.audienceId); u.audienceId = a ? a.id : null; u.audience = a ? a.name : null; }
    return u;
  });
  on('POST', /^\/users\/([^/]+)\/reset$/, ctx => { needAdmin(ctx); const u = findBy(DB.users, ctx.m[1]); if (!u) throw new Fail(404, 'Not found.'); return { member: u, emailSent: false, link: linkFor(newToken(u.pendingInvite ? 'invite' : 'reset', u.email, u.displayName), u.pendingInvite ? 'invite' : 'reset') }; });
  on('DELETE', /^\/users\/([^/]+)$/, ctx => { needAdmin(ctx); DB.users = DB.users.filter(x => x.id !== ctx.m[1]); return {}; });

  // ---- platform
  on('GET', /^\/tenants$/, ctx => { needPlatform(ctx); return DB.tenants; });
  on('POST', /^\/tenants$/, ctx => {
    needPlatform(ctx); const b = ctx.body || {};
    const slug = String(b.slug || '').trim().toLowerCase(); if (!slug) throw new Fail(400, 'Give the company a short ID.');
    if (DB.tenants.some(t => t.slug === slug)) throw new Fail(409, 'That company ID is already taken.');
    const t = { id: uid(), name: b.name, slug, isActive: true, createdUtc: iso(Date.now()), applications: 0, documents: 0, userKeys: 0, keyViewable: true };
    DB.tenants.unshift(t);
    const out = Object.assign({}, t, { apiKey: 'demo-company-key-' + uid().slice(0, 8) });
    if (b.adminEmail) { out.adminEmail = b.adminEmail; out.inviteEmailed = false; out.inviteLink = linkFor(newToken('invite', b.adminEmail, b.adminName || b.adminEmail), 'invite'); }
    return out;
  });
  on('GET', /^\/tenants\/([^/]+)\/key$/, ctx => { needPlatform(ctx); const t = findBy(DB.tenants, ctx.m[1]); return { companyId: t.slug, key: 'demo-company-key-' + t.slug.slice(0, 8) }; });
  on('POST', /^\/tenants\/([^/]+)\/reset-key$/, ctx => { needPlatform(ctx); const t = findBy(DB.tenants, ctx.m[1]); return { companyId: t.slug, key: 'demo-company-key-' + uid().slice(0, 8) }; });
  on('POST', /^\/tenants\/([^/]+)\/(enable|disable)$/, ctx => { needPlatform(ctx); const t = findBy(DB.tenants, ctx.m[1]); if (t) t.isActive = ctx.m[2] === 'enable'; return {}; });
  on('DELETE', /^\/tenants\/([^/]+)$/, ctx => { needPlatform(ctx); DB.tenants = DB.tenants.filter(t => t.id !== ctx.m[1]); return {}; });
  on('GET', /^\/platform-admins$/, ctx => { needPlatform(ctx); return DB.platformAdmins; });
  on('POST', /^\/platform-admins$/, ctx => { needPlatform(ctx); const m = member(DB.platformAdmins, ctx.body, 'PlatformAdmin'); return { member: m, emailSent: false, link: linkFor(newToken('invite', m.email, m.displayName), 'invite') }; });
  on('POST', /^\/platform-admins\/([^/]+)\/reset$/, ctx => { needPlatform(ctx); const u = findBy(DB.platformAdmins, ctx.m[1]); return { member: u, emailSent: false, link: linkFor(newToken('reset', u.email, u.displayName), 'reset') }; });
  on('DELETE', /^\/platform-admins\/([^/]+)$/, ctx => { needPlatform(ctx); DB.platformAdmins = DB.platformAdmins.filter(x => x.id !== ctx.m[1]); return {}; });
  on('GET', /^\/server-folders$/, ctx => { needPlatform(ctx); return DB.folders; });
  on('POST', /^\/server-folders$/, ctx => { needPlatform(ctx); const f = { id: uid(), path: ctx.body.path, exists: true, fromConfig: false, companies: 0 }; DB.folders.push(f); return f; });
  on('DELETE', /^\/server-folders\/([^/]+)$/, ctx => { needPlatform(ctx); DB.folders = DB.folders.filter(f => f.id !== ctx.m[1]); return {}; });

  // ---- applications, audiences, keys
  on('GET', /^\/applications$/, () => DB.apps);
  on('POST', /^\/applications$/, ctx => { const a = { id: uid(), name: ctx.body.name, description: ctx.body.description || null, createdUtc: iso(Date.now()) }; DB.apps.push(a); return a; });
  on('GET', /^\/access\/audiences$/, () => DB.audiences);
  on('GET', /^\/access\/preview$/, ctx => {   // recorded for the two sample groups; made up for groups created during the visit
    const id = ctx.q.get('audienceId'), rec = fx('GET', '/access/preview?audienceId=' + id), a = findBy(DB.audiences, id);
    return rec || { audience: a ? a.name : 'Group', modules: [], openPages: 0, totalPages: 5, openDocumentPages: 0, totalDocumentPages: 5, openWorkflows: 0, totalWorkflows: 1 };
  });
  on('POST', /^\/access\/audiences$/, ctx => {
    const b = ctx.body || {}; if (DB.audiences.some(a => a.name.toLowerCase() === String(b.name || '').toLowerCase())) throw new Fail(409, 'A group with this name already exists.');
    const a = { id: uid(), name: b.name, aliases: b.aliases || '', description: b.description || '', keys: 0, links: 0, decisions: 0, createdUtc: iso(Date.now()), canBrowseKnowledge: false }; DB.audiences.push(a); return a;
  });
  on('PUT', /^\/access\/audiences\/([^/]+)$/, ctx => { const a = findBy(DB.audiences, ctx.m[1]); if (a) Object.assign(a, ctx.body); return a || {}; });
  on('DELETE', /^\/access\/audiences\/([^/]+)$/, ctx => { DB.audiences = DB.audiences.filter(a => a.id !== ctx.m[1]); return {}; });
  on('PUT', /^\/access\/rules$/, ctx => ({ saved: ((ctx.body && ctx.body.changes) || []).length }));
  on('GET', /^\/access-keys$/, () => DB.keys);
  on('POST', /^\/access-keys$/, ctx => {
    const b = ctx.body || {}, a = b.audienceId ? DB.audiences.find(x => x.id === b.audienceId) : null;
    const k = { id: uid(), name: b.name, prefix: 'bbn_' + uid().slice(0, 4), active: true, audienceId: a ? a.id : null, audienceName: a ? a.name : null, createdUtc: iso(Date.now()), lastUsedUtc: null, canCopy: true };
    DB.keys.push(k); return Object.assign({ key: 'bbn_demo_' + uid().replace(/-/g, '') }, k);
  });
  on('GET', /^\/access-keys\/([^/]+)\/key$/, () => ({ key: 'bbn_demo_' + uid().replace(/-/g, '') }));
  on('POST', /^\/access-keys\/([^/]+)\/replace$/, ctx => { const k = findBy(DB.keys, ctx.m[1]); return Object.assign({ key: 'bbn_demo_' + uid().replace(/-/g, '') }, k); });
  on('PATCH', /^\/access-keys\/([^/]+)$/, ctx => { const k = findBy(DB.keys, ctx.m[1]); if (k) Object.assign(k, ctx.body); return k || {}; });
  on('DELETE', /^\/access-keys\/([^/]+)$/, ctx => { DB.keys = DB.keys.filter(k => k.id !== ctx.m[1]); return {}; });

  // ---- documents
  on('GET', /^\/documents$/, () => DB.docs);
  on('POST', /^\/documents$/, ctx => {
    const f = ctx.body instanceof FormData ? ctx.body.get('File') : null; const name = (f && f.name) || 'uploaded-document.txt';
    if (DB.docs.some(d => d.fileName === name)) return { documentId: DB.docs.find(d => d.fileName === name).id, versionNumber: 1, status: 'Approved', duplicate: true };
    const id = addDocument(name, 'PendingReview', ['Uploaded in the demo. In the real app this text is read from your file and analysed by the AI.']);
    return { documentId: id, versionNumber: 1, status: 'Queued', duplicate: false };
  });
  on('GET', /^\/documents\/([^/]+)\/status$/, ctx => { const s = DB.docStatus[ctx.m[1]]; if (!s) throw new Fail(404, 'Not found.'); const d = findBy(DB.docs, ctx.m[1]); if (d) s.status = d.status; return s; });
  on('GET', /^\/documents\/([^/]+)\/pages$/, ctx => DB.docPages[ctx.m[1]] || []);
  on('POST', /^\/documents\/([^/]+)\/approve$/, ctx => {
    const d = findBy(DB.docs, ctx.m[1]); if (!d) throw new Fail(404, 'Not found.'); d.status = 'Approved';
    let n = 0; DB.review.forEach(r => { if (r.review === 'Pending' && r.source && r.source.document === d.fileName) { r.review = 'Approved'; n++; } });
    return { approved: n };
  });
  on('POST', /^\/documents\/([^/]+)\/(retry|reindex)$/, () => ({}));
  on('POST', /^\/documents\/([^/]+)\/updates\/(text|file)$/, () => ({ status: 'Queued' }));
  on('DELETE', /^\/documents\/([^/]+)$/, ctx => { DB.docs = DB.docs.filter(d => d.id !== ctx.m[1]); return {}; });

  // ---- review and knowledge
  on('GET', /^\/review$/, ctx => { const s = ctx.q.get('status') || 'Pending'; return DB.review.filter(r => r.review === s); });
  on('PUT', /^\/review\/([^/]+)\/([^/]+)$/, ctx => {
    const r = DB.review.find(x => x.id === ctx.m[2]); if (!r) throw new Fail(404, 'Not found.'); const b = ctx.body || {};
    if (b.status) r.review = b.status; if (b.name) r.title = b.name; if (b.text) r.text = b.text; return {};
  });
  on('GET', /^\/support-requests\/count$/, () => ({ open: DB.support.filter(s => s.status === 'open').length }));
  on('GET', /^\/support-requests$/, ctx => { const s = ctx.q.get('status') || 'open'; return { openTotal: DB.support.filter(x => x.status === 'open').length, items: DB.support.filter(x => s === 'all' || x.status === s) }; });
  on('PATCH', /^\/support-requests\/([^/]+)$/, ctx => { const s = findBy(DB.support, ctx.m[1]); if (s) { Object.assign(s, ctx.body); if (ctx.body.status === 'open') s.resolution = null; } return s || {}; });
  on('DELETE', /^\/support-requests\/([^/]+)$/, ctx => { DB.support = DB.support.filter(s => s.id !== ctx.m[1]); return {}; });
  on('GET', /^\/insights\/gaps$/, ctx => {
    const days = Number(ctx.q.get('days')) || 30, cut = Date.now() - days * DAY;
    const items = DB.gaps.filter(g => Date.parse(g.lastAskedUtc) >= cut);
    return { days, open: items.filter(g => g.state === 'open').length, handled: items.filter(g => g.state === 'handled').length, ignored: items.filter(g => g.state === 'ignored').length, items };
  });
  on('POST', /^\/insights\/gaps\/state$/, ctx => { const g = DB.gaps.find(x => x.key === ctx.body.key); if (g) { g.state = ctx.body.status; g.reopened = false; } return {}; });
  on('GET', /^\/insights\/usage$/, ctx => usageReport(Number(ctx.q.get('days')) || 30));
  on('GET', /^\/bot-links\/usage$/, ctx => linkUsage(Number(ctx.q.get('days')) || 30));

  // ---- share links
  on('GET', /^\/bot-links$/, () => DB.links);
  on('POST', /^\/bot-links$/, ctx => {
    const b = ctx.body || {}; const base = DB.links[0] || {};
    const a = b.audienceId ? DB.audiences.find(x => x.id === b.audienceId) : null;
    const docs = (b.documentIds || []).map(id => findBy(DB.docs, id)).filter(Boolean);
    const l = Object.assign({}, clone(base), { id: uid(), name: b.name, title: b.title, application: 'Hospital System', active: true, dailyMessageLimit: b.dailyMessageLimit || 300, todayMessages: 0, createdUtc: iso(Date.now()), lastUsedUtc: null,
      token: 'demo-' + String(b.name || 'link').toLowerCase().replace(/[^a-z0-9]+/g, '-') + '-token', welcome: b.welcome || null, starters: b.starters || [], accentColor: b.accentColor || '#2459e6', documentIds: b.documentIds || [], documentNames: docs.map(d => d.fileName),
      theme: Object.assign({}, base.theme, b.theme), applicationId: b.applicationId || DB.appId, storedChats: 0, audienceId: a ? a.id : null, audienceName: a ? a.name : null });
    DB.links.push(l); return l;
  });
  on('PATCH', /^\/bot-links\/([^/]+)$/, ctx => {
    const l = findBy(DB.links, ctx.m[1]); if (!l) throw new Fail(404, 'Not found.'); const b = ctx.body || {};
    ['title', 'welcome', 'starters', 'dailyMessageLimit', 'accentColor', 'documentIds', 'active'].forEach(k => { if (k in b) l[k] = b[k]; });
    if (b.theme) l.theme = Object.assign({}, l.theme, b.theme);
    if (b.documentIds) l.documentNames = b.documentIds.map(id => (findBy(DB.docs, id) || {}).fileName).filter(Boolean);
    if (b.noAudience) { l.audienceId = null; l.audienceName = null; }
    if (b.audienceId) { const a = DB.audiences.find(x => x.id === b.audienceId); l.audienceId = a ? a.id : null; l.audienceName = a ? a.name : null; }
    return l;
  });
  on('DELETE', /^\/bot-links\/([^/]+)\/conversations$/, () => ({}));
  on('DELETE', /^\/bot-links\/([^/]+)$/, ctx => { DB.links = DB.links.filter(l => l.id !== ctx.m[1]); return {}; });

  // ---- chat (signed in)
  on('GET', /^\/conversations$/, () => DB.convs);
  on('GET', /^\/conversations\/([^/]+)$/, ctx => DB.convMsgs[ctx.m[1]] || (() => { throw new Fail(404, 'Not found.'); })());
  on('DELETE', /^\/conversations\/([^/]+)$/, ctx => { DB.convs = DB.convs.filter(c => c.id !== ctx.m[1]); delete DB.convMsgs[ctx.m[1]]; return {}; });
  on('POST', /^\/chat$/, async ctx => {
    const b = ctx.body || {}, a = answerFor(b.message); await sleep(700 + Math.random() * 500);
    let id = b.conversationId, c = id && DB.convMsgs[id];
    if (!c) { id = uid(); c = { id, applicationId: DB.appId, title: String(b.message).slice(0, 60), messages: [] }; DB.convMsgs[id] = c; DB.convs.unshift({ id, title: c.title, updatedUtc: iso(Date.now()) }); }
    c.messages.push({ role: 'user', content: b.message, documented: null, sources: [], createdUtc: iso(Date.now()) }, { role: 'assistant', content: a.answer, documented: a.documented, sources: a.sources, createdUtc: iso(Date.now()) });
    const row = DB.convs.find(x => x.id === id); if (row) row.updatedUtc = iso(Date.now());
    if (!a.documented && !/^(hi|hello|thanks?)/i.test(b.message)) noteGap(String(b.message));
    return { conversationId: id, answer: a.answer, documented: a.documented, sources: a.sources, understood: null, related: null };
  });
  on('POST', /^\/chat\/support$/, ctx => { DB.support.unshift({ id: uid(), question: String((ctx.body && ctx.body.question) || 'Question from chat'), status: 'open', wasAnswered: false, createdUtc: iso(Date.now()), channel: 'Signed-in users', note: (ctx.body && ctx.body.note) || '', contact: (ctx.body && ctx.body.contact) || null, resolution: null }); return {}; });

  // ---- settings
  on('PUT', /^\/settings\/storage$/, () => ({}));
  on('POST', /^\/settings\/storage\/test$/, () => ({ ok: true, message: 'Demo: nothing was tested and nothing was changed.', milliseconds: 42 }));
  on('POST', /^\/settings\/storage\/migrate$/, () => ({ moved: 0, failed: [], remaining: 0 }));
  on('POST', /^\/(insights|settings)\/.*$/, () => ({}));

  // ---- fixtures for everything else that only reads
  const fixtureRoute = (method, path, q) => {
    const v = fx(method, path + (q ? '?' + q : '')); if (v !== undefined) return v;
    return undefined;
  };
  on('GET', /^\/me\/access$/, ctx => {
    if (ctx.who && ctx.who.role === 'TenantUser') return { canBrowseKnowledge: false, audience: (ACCOUNTS[ctx.who.email] || {}).audience || 'Front Desk' };
    return fx('GET', '/me/access');
  });

  // ---------------------------------------------------------------- visitor chat page (/api/bot)
  function botLink(headers) {
    const t = headers['X-Bot-Token'] || headers['x-bot-token'] || '';
    return DB.links.find(l => l.token === t) || DB.links[0];
  }
  function botRoute(method, path, ctx) {
    const link = botLink(ctx.headers), V = DB.visitor;
    if (method === 'GET' && path === '/info') return Object.assign(fx('GET', '/__bot_info') || {}, { title: link.title, welcome: link.welcome, starters: link.starters, accentColor: link.accentColor, theme: link.theme });
    if (method === 'POST' && path === '/ask') {
      const b = ctx.body || {}, a = answerFor(b.message); let id = b.conversationId, c = id && V.msgs[id];
      if (!c) { id = uid(); c = []; V.msgs[id] = c; V.convs.unshift({ id, title: String(b.message).slice(0, 60), updatedUtc: iso(Date.now()) }); }
      c.push({ role: 'user', content: b.message, documented: null, sources: [], createdUtc: iso(Date.now()) }, { role: 'assistant', content: a.answer, documented: a.documented, sources: [], createdUtc: iso(Date.now()) });
      if (!a.documented && !/^(hi|hello|thanks?)/i.test(b.message)) noteGap(String(b.message));
      return sleep(900 + Math.random() * 600).then(() => ({ answer: a.answer, documented: a.documented, conversationId: id }));
    }
    if (method === 'GET' && path === '/conversations') return V.convs;
    let m;
    if (method === 'GET' && (m = path.match(/^\/conversations\/([^/]+)$/))) return V.msgs[m[1]] || [];
    if (method === 'POST' && path === '/conversations/clear') { V.convs = []; V.msgs = {}; return {}; }
    if (method === 'POST' && (m = path.match(/^\/conversations\/([^/]+)\/delete$/))) { V.convs = V.convs.filter(c => c.id !== m[1]); delete V.msgs[m[1]]; return {}; }
    if (method === 'POST' && path === '/support') { DB.support.unshift({ id: uid(), question: 'Question from a visitor', status: 'open', wasAnswered: false, createdUtc: iso(Date.now()), channel: link.name, note: (ctx.body && ctx.body.note) || '', contact: (ctx.body && ctx.body.contact) || null, resolution: null }); return {}; }
    throw new Fail(404, 'Not found.');
  }

  // ---------------------------------------------------------------- fetch replacement
  const json = (status, data) => new Response(data === undefined ? '' : JSON.stringify(data), { status, headers: { 'Content-Type': 'application/json' } });
  async function serve(url, init) {
    init = init || {};
    const u = new URL(url, location.href), method = (init.method || 'GET').toUpperCase();
    const headers = init.headers || {};
    let body = init.body;
    if (typeof body === 'string') { try { body = JSON.parse(body); } catch { /* leave as text */ } }
    const ctx = { q: u.searchParams, body, headers, who: whoIs(init) };
    await ready;
    try {
      if (u.pathname.startsWith('/health')) return json(200, { status: 'Healthy', checks: [
        { name: 'SQL Server', status: 'Healthy', detail: 'Demo' }, { name: 'PostgreSQL + pgvector', status: 'Healthy', detail: 'Demo' }, { name: 'Background worker', status: 'Healthy', detail: 'Demo' }] });
      if (u.pathname.startsWith('/api/bot')) { await sleep(120); return json(200, await botRoute(method, u.pathname.slice('/api/bot'.length), ctx)); }
      const path = u.pathname.slice('/api/v1'.length);
      let route = null;
      for (const r of R) { if (r.m !== method) continue; const m = path.match(r.re); if (m) { route = r; ctx.m = m; break; } }
      if (route && !route.open && !ctx.who) throw new Fail(401, 'Your session expired. Please sign in again.');
      await sleep(80 + Math.random() * 160);
      if (route) return json(200, await route.fn(ctx));
      if (!ctx.who) throw new Fail(401, 'Your session expired. Please sign in again.');
      if (method === 'GET') { const v = fixtureRoute(method, path, ctx.q.toString()); if (v !== undefined) return json(200, v); }
      if (method !== 'GET') return json(200, {});
      if (window.console) console.warn('[demo] no sample data for', method, path);
      throw new Fail(404, 'Not found.');
    } catch (e) {
      if (e instanceof Fail) return json(e.status, { title: e.message, detail: e.message });
      if (window.console) console.error('[demo]', e);
      return json(500, { title: 'The demo hit a problem.', detail: 'The demo hit a problem: ' + (e && e.message) });
    }
  }
  window.fetch = function (input, init) {
    const url = typeof input === 'string' ? input : (input && input.url) || String(input);
    if (/^\/(api\/(v1|bot)|health)(\/|\?|$)/.test(url)) return serve(url, init);
    return realFetch(input, init);
  };

  const ready = realFetch(BASE + 'fixtures.json').then(r => r.json()).then(raw => {
    // the visitor page's info is recorded under a "BOT" prefix; keep it under a name that cannot clash with an API path
    const info = raw['BOT GET /info']; delete raw['BOT GET /info']; delete raw['BOT POST /ask'];
    raw['GET /__bot_info'] = info;
    init(raw);
  });

  // ---------------------------------------------------------------- the demo banner and one-click sign-in
  function chrome() {
    if (window.parent !== window) return;   // inside the share page's preview frame: the page around it already says "demo"
    const bar = document.createElement('div'); bar.className = 'demo-pill' + (/bot\.html$/.test(location.pathname) ? ' demo-pill-bot' : ''); bar.setAttribute('role', 'note');
    bar.title = 'This is a demo with sample data. Nothing you do is saved.';
    bar.innerHTML = '<strong>Demo</strong><span> sample data, nothing is saved</span><button type="button" aria-label="Hide this note">×</button>';
    bar.querySelector('button').addEventListener('click', () => bar.remove());
    document.body.append(bar);
  }
  function quickSignIn() {
    const email = document.getElementById('email'), pw = document.getElementById('password');
    if (!email || !pw || document.querySelector('.demo-quick')) return;
    const form = email.closest('form'); if (!form) return;
    const box = document.createElement('div'); box.className = 'notice demo-quick';
    box.innerHTML = '<div><strong>This is a demo with sample data.</strong> Pick a person to sign in as, or type any password for the emails shown.</div>';
    const row = document.createElement('div'); row.className = 'demo-quick-row';
    [['Company admin', 'asha.kumar@demo-hospital.example'], ['Front-desk user', 'priya.nair@demo-hospital.example'], ['Platform admin', 'platform.admin@bbnavigator.example']].forEach(([label, addr]) => {
      const b = document.createElement('button'); b.type = 'button'; b.className = 'btn btn-sm'; b.textContent = label;
      b.addEventListener('click', () => { email.value = addr; pw.value = 'demo-password-1'; form.requestSubmit(); });
      row.append(b);
    });
    box.append(row); form.parentNode.insertBefore(box, form);
  }
  function start() {
    chrome(); quickSignIn();
    new MutationObserver(quickSignIn).observe(document.body, { childList: true, subtree: true });
  }
  if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', start); else start();
})();
