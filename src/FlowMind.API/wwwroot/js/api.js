// API client. The JWT lives in sessionStorage (cleared when the tab closes). No AI provider key ever reaches the
// browser: the server holds those. The browser only holds a short-lived token for the signed-in tenant.

const KEY = 'flowmind.session';

export const session = {
  get() {
    try {
      const s = JSON.parse(sessionStorage.getItem(KEY) || 'null');
      if (s && s.expiresUtc && new Date(s.expiresUtc + (s.expiresUtc.endsWith('Z') ? '' : 'Z')) < new Date()) { this.clear(); return null; }
      return s;
    } catch { return null; }
  },
  set(v) { try { sessionStorage.setItem(KEY, JSON.stringify(v)); } catch { /* storage blocked: stay signed in for this page only */ memory = v; } },
  clear() { memory = null; try { sessionStorage.removeItem(KEY); } catch { /* ignore */ } },
};
let memory = null;
const current = () => session.get() || memory;

export class ApiError extends Error {
  constructor(status, message) { super(message); this.status = status; }
}

function friendly(status, d) {
  if (d && d.errors && typeof d.errors === 'object') {
    const first = Object.values(d.errors).flat()[0];
    if (first) return String(first);
  }
  return (d && (d.detail || d.title))
    || ({ 403: 'You do not have access to this.', 404: 'Not found.', 413: 'The file is too large.', 429: 'Too many requests, or a daily limit was reached. Try again later.' })[status]
    || `Request failed (${status}).`;
}

export async function api(method, path, opts = {}) {
  const s = current();
  const headers = {};
  if (s && s.token) headers.Authorization = 'Bearer ' + s.token;
  let body;
  if (opts.form) body = opts.form;
  else if (opts.body !== undefined) { headers['Content-Type'] = 'application/json'; body = JSON.stringify(opts.body); }

  let res;
  try { res = await fetch('/api/v1' + path, { method, headers, body, signal: opts.signal }); }
  catch (e) { if (e && e.name === 'AbortError') throw e; throw new ApiError(0, 'Cannot reach the server.'); }

  if (res.status === 401 && s) {
    session.clear();
    window.dispatchEvent(new Event('flowmind:signed-out'));
    throw new ApiError(401, 'Your session expired. Please sign in again.');
  }
  const text = await res.text();
  let data = null;
  if (text) { try { data = JSON.parse(text); } catch { data = null; } }
  if (!res.ok) throw new ApiError(res.status, friendly(res.status, data));
  return data;
}

// Decode (do not trust) the token payload only to show who is signed in.
export function describeSession(s) {
  try {
    const p = JSON.parse(atob(s.token.split('.')[1].replace(/-/g, '+').replace(/_/g, '/')));
    return { role: p.role, name: p.name || String(p.sub || '').replace(/^tenant:/, ''), email: p.email || '', isAccount: String(p.sub || '').startsWith('user:') };
  } catch { return { role: '', name: '' }; }
}
