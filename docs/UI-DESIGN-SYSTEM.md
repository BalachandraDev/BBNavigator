# BBNavigator UI: audit and design system

Plain HTML/CSS/JS served by the API (`src/FlowMind.API/wwwroot`). No framework, no build step, no UI dependencies. Geist (self-hosted) is the only font. Everything below keeps that stack.

## 1. Audit of the previous UI (November 2026)

Method: every screen was captured in a real browser (Edge, light and dark, 1366, 820 and 390 px wide) against a test company, and every CSS class was cross-checked against the JavaScript that uses it.

| Area | Finding |
|---|---|
| CSS architecture | One 1,276-line `app.css`. It is the sum of six redesign passes layered on top of each other ("v3", "v4", "Design pass", "Volt", "Blue and white palette", plus many "fixes" blocks). Later rules fight earlier ones, so a change in one place leaks into others. 561 classes are defined, about 95 are never referenced. |
| Tokens | Colours are variables, but type sizes, spacing, radii (6, 8, 9, 10, 12, 14, 16, 99 all appear), shadows and motion timings are hard-coded per rule. |
| Navigation | A single top bar carries 8 items, a "Manage" menu, the application picker (its label truncates: "Hospital Syst..."), search, theme, new-application and account. Wide screens are crowded, tablets wrap. No breadcrumb, no sense of where you are. |
| Hierarchy | Page title, card title, section label and table header sizes are close to each other. Everything sits on a blue-tinted page with low contrast to the cards; large soft shadows and glowing gradient buttons make every element look equally important. |
| Components | Native checkboxes, select arrows and bullet lists mixed with styled controls. Disabled buttons turn into long grey sentences. Secondary, ghost, danger and link buttons differ per page. Tooltips do not exist (only `title`). Loading is a mix of spinners and shimmer. Tables have no hover or sticky header. |
| States | Empty states exist but differ per page. Error states are plain red text in places. Focus rings are inconsistent. |
| Responsive | Works, but is the desktop layout squeezed: the home cards stack in a very long column on phones, tab strips overflow, tables scroll sideways without a hint. |
| Accessibility | Good base (labels, focus-visible, aria on menus). Gaps: small muted text below 4.5:1 contrast in places, tab strips without arrow-key support, icon buttons that only rely on `title`. |
| Visitor chat page (`bot.html`) | Separate small stylesheet, its own tokens. Per-link accent colour must stay configurable. |

## 2. Direction

Calm, precise, clinical-grade enterprise software: a neutral cool-grey canvas, white surfaces separated by hairline borders instead of heavy shadows, one confident blue accent used only for actions and selection, tight consistent typography, and motion that is quick and quiet (120 to 220 ms, easing out, disabled for people who ask for reduced motion).

Navigation moves to a **left sidebar** (grouped, collapsible to icons, drawer on phones) with a slim page header carrying the breadcrumb, search and theme. The application picker becomes a workspace switcher at the top of the sidebar. Routes, role rules and the quick-search palette are unchanged.

## 3. Tokens (css/tokens.css)

- **Colour**: neutral scale `--n-0 ... --n-950` plus semantic roles `--bg`, `--surface`, `--surface-2`, `--surface-3`, `--line`, `--line-strong`, `--text`, `--text-2`, `--muted`, `--brand`, `--brand-hover`, `--brand-tint`, `--brand-ink`, and status pairs `--ok/--warn/--bad/--info/--review` with `-tint` and `-line`. Light and dark are separate token sets chosen by `data-theme` or the device setting.
- **Type**: 12 / 13 / 14 (base) / 16 / 18 / 22 / 28 px. Weights 400, 500, 600, 700. Page title 22 px, section title 15 px, table header 12 px, numbers use tabular figures.
- **Space**: 4 px grid, `--s-1` (4) ... `--s-12` (48).
- **Radius**: `--r-xs` 4 (kbd, chips in tables), `--r-sm` 6 (small buttons, badges), `--r-md` 8 (buttons, inputs), `--r-lg` 12 (cards), `--r-xl` 16 (dialogs, hero), `--r-pill`.
- **Elevation**: `--sh-xs` (cards at rest), `--sh-sm`, `--sh-md` (menus), `--sh-lg` (dialogs). No coloured glows.
- **Motion**: `--t-fast` 120 ms, `--t-base` 180 ms, `--t-slow` 260 ms, `--ease` cubic-bezier(.2,.7,.2,1).
- **Sizes**: control heights 32 (sm), 38 (md), 44 (lg, touch); icons 16 and 20; sidebar 252 px (64 collapsed); header 56 px; content max-width 1240 px.

## 4. Files

```
css/tokens.css      colours, type, spacing, radii, shadows, motion
css/base.css        reset, typography, focus, scrollbars, utilities, reduced motion
css/components.css  buttons, fields, checkbox/radio/switch, badges, chips, cards, tables, tabs, segmented controls,
                    dialogs, menus, toasts, tooltips, progress, skeleton, empty/error states, alerts, upload, avatars
css/shell.css       sidebar, page header, breadcrumbs, drawer, command palette, login
css/views.css       per-screen layout (home, documents, review, knowledge, chat, share, insights, usage, settings, access, rules, companies)
css/bot.css         visitor chat page (uses the same tokens)
```

## 5. Component rules

- One primary action per view region. Primary = solid accent. Secondary = surface with border. Ghost = text only. Danger = outlined red that fills on hover, confirmation dialog before anything destructive.
- Fields share one height, border, focus ring (2 px accent ring with 3 px offset glow) and error style. Hints below, errors replace hints.
- Badges are tinted pills with a leading dot for status. Counts use a neutral pill.
- Tables: 44 px rows, 12 px uppercase header on `--surface-2`, hover row tint, numeric columns right-aligned and tabular, sticky header inside scroll areas, first column carries the name.
- Cards: `--surface`, 1 px `--line`, `--r-lg`, `--sh-xs`, 20 px padding, a header row (title left, actions right) and optional footer.
- Dialogs: centred, `--r-xl`, title bar with close, scroll inside, backdrop blur, Escape and backdrop close.
- Every list or table has an empty state (icon, title, one sentence, one action) and every load has a skeleton that matches the final layout.

## 6. Verification log

See the bottom of `DEVLOG.md` for the dated entries of what was checked (screens, widths, themes, console errors, feature walk-through).

## 7. What was verified (20 November 2026)

- Every screen captured in a real browser against a test company: light and dark at 1366 px, all screens at 390 px (no horizontal overflow on any), the visitor chat page at 390 and 1366 px in both themes, the platform admin page, the collapsed sidebar, the account menu, the command palette, dialogs and toasts.
- Click-through checks with assertions: document filters and search, Knowledge tree, Settings tabs (with Left/Right arrow keys), share-link editor tabs and segmented controls, Access rules ticking, the unsaved-changes bar and Discard, the audiences dialog, creating a user key. No console errors in any run.
- Every class used by the JavaScript has a rule in the new CSS (dynamic prefixes aside); the old `app.css` is deleted. 490 automated tests pass, including new ones that every page links only existing stylesheets and uses no inline styles (the strict CSP).
- Not covered: Safari and Firefox were not opened (Edge/Chromium only); no screen reader pass; the Tamil visitor page was not re-checked after the restyle.