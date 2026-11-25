// Light / dark / follow-the-device. The choice is remembered in this browser only.
import { icon } from './chatkit.js';

const KEY = 'flowmind.theme';
const root = document.documentElement;
const read = () => { try { return localStorage.getItem(KEY); } catch { return null; } };
const systemDark = () => { try { return matchMedia('(prefers-color-scheme: dark)').matches; } catch { return false; } };

export const isDark = () => root.dataset.theme ? root.dataset.theme === 'dark' : systemDark();
function apply(v) { if (v === 'dark' || v === 'light') root.dataset.theme = v; else delete root.dataset.theme; }
// The admin site opens in light unless the person chose otherwise; the visitor chat page follows the share link's own setting.
const isBotPage = location.pathname.endsWith('/bot.html');
apply(read() || (isBotPage ? null : 'light'));

/** The admin's default look for a share link. Applies when the visitor has not chosen one (or always, with `force`). 'auto' = follow the device. */
export function setDefaultMode(mode, force = false) {
  if (force || !read()) apply(mode === 'light' || mode === 'dark' ? mode : null);
}

/** A round icon button that flips between light and dark. */
export function themeToggle(cls = 'ghost icon-only', labels = null) {
  const btn = document.createElement('button');
  btn.type = 'button'; btn.className = cls;
  const paint = () => {
    btn.replaceChildren(icon(isDark() ? 'sun' : 'moon', 18));
    const label = labels ? (isDark() ? labels().toLight : labels().toDark) : (isDark() ? 'Switch to light mode' : 'Switch to dark mode');
    btn.setAttribute('aria-label', label); btn.title = label;
  };
  btn.addEventListener('click', () => {
    const next = isDark() ? 'light' : 'dark';
    apply(next); try { localStorage.setItem(KEY, next); } catch { /* not remembered */ }
    paint();
  });
  paint();
  return btn;
}