# Design Review — OpenWebUI.Client

| | |
|---|---|
| **Mode** | Static code review + Lighthouse verification |
| **Scope** | Entire client (`src/OpenWebUI.Client`) — 37 `.razor`, `index.html`, `tailwind.input.css`, `wwwroot/js/*`, `wwwroot/i18n/*` |
| **Date** | 2026-10-08 |
| **Tools** | Lighthouse 13.5.0, Chrome for Testing headless-shell 155.0.8059.39 (arm64), Docker compose app on `http://localhost:3032` |
| **Target** | `http://localhost:3032` — local Docker Release build. **Indicative measurements, not production.** No auth wall bypassed; Lighthouse measured the unauthenticated landing (chat shell + composer). |

---

## Lighthouse results (3 runs per preset)

### Mobile (Moto G4-class throttling, simulated)

| Run | Perf | A11y | Best-Pract. | SEO | FCP | LCP | TBT | CLS | TTI |
|-----|------|------|-------------|-----|-----|-----|-----|-----|-----|
| 1 | 30 | 100 | 100 | 91 | 5.9 s | 33.4 s¹ | 4,590 ms | 0 | 33.8 s¹ |
| 2 | 34 | 100 | 100 | 91 | 6.3 s | 6.5 s | 2,090 ms | 0 | 8.5 s |
| 3 | 34 | 100 | 100 | 91 | 6.2 s | 6.4 s | 2,020 ms | 0 | 8.3 s |

¹ Run 1 outlier — cold WASM boot under contention; runs 2–3 are representative.

### Desktop

| Run | Perf | A11y | Best-Pract. | SEO | FCP | LCP | TBT | CLS | TTI |
|-----|------|------|-------------|-----|-----|-----|-----|-----|-----|
| 1 | 57 | 100 | 100 | 91 | 1.7 s | 1.8 s | 790 ms | 0 | 2.9 s |
| 2 | 58 | 100 | 100 | 91 | 1.7 s | 1.8 s | 730 ms | 0 | 2.8 s |
| 3 | 60 | 100 | 100 | 91 | 1.6 s | 1.7 s | 700 ms | 0 | 2.7 s |

**Verdict:** Performance fails the 95 bar on both presets (median mobile 34 / desktop 58). Accessibility/Best-practices score 100 on automated checks — the manual findings below are the kind axe/Lighthouse cannot detect. SEO 91 (only failure: missing meta description).

### Key Lighthouse evidence

- `bootup-time` (mobile-3): **6.5 s total script boot** — `/_framework/dotnet.native.*.js` alone 4.0 s script evaluation.
- `mainthread-work-breakdown`: 7.7 s — scriptEvaluation 6.5 s dominant; `long-tasks`: 18 tasks, worst 1,926 ms (dotnet.native).
- `render-blocking-insight`: **est. 4,610 ms savings** — `lib/xterm/xterm.js` (908 ms wasted), `js/terminal.js`, `js/audio.js`, `lib/xterm/xterm-addon-fit.js`, `js/collab.js`, `js/codeexec.js`.
- `unused-javascript`: `xterm.js` 85 % wasted (48 KiB of 56 KiB) — loaded on every page, used only by `/terminal` + chat panel tab.
- `resource-summary`: **909 KiB total; font = 805 KiB (86 %) — `assets/fonts/Inter-Variable.ttf`.**
- `image-delivery-insight`: `splash-dark.png` 500×500 served for 224×224 display (~4 KiB wasted).
- `meta-description`: absent → sole SEO failure.
- `lcp-discovery-insight`: `fetchpriority=high` not applied to LCP resource.
- `network-dependency-tree-insight`: serial chain document → `js/boot.js` → `_framework/dotnet.js` → `dotnet.native.*.js`; no preconnect candidates.

---

## Findings

Severity: `critical` blocks a user group entirely · `high` significant barrier or broken UI · `medium` degrades quality · `low` polish.

### A. Accessibility — keyboard & focus

**A1. Focus indicators missing on nearly all interactive elements — `critical`**
`tailwind.input.css` defines focus styling only for `.input` (L195 `focus:ring-2`) and `.skip-link:focus-visible` (L142). Every other interactive class — `.msg-action` (L259), `.icon-btn` (L251), `.icon-mini` (L255), `.modal-tab` (L287), `.auth-submit`, `.danger-btn`, `.sidebar-item`, `.menu-item`, `.check`, plus all ad-hoc Tailwind buttons — has **zero `:focus-visible` styling**. Keyboard users cannot see where focus is anywhere except text inputs and the skip link.
Compounding: raw `outline-none` without replacement on `Sidebar.razor:66` (new-chat button), `Sidebar.razor:83` (search input), `Sidebar.razor:323` (share URL input), `Auth.razor:16,52,58,63` (all login fields — only a `border-b`, focus changes nothing), `ChannelPage.razor:146,189` (message + reply composers). `ChatView.razor:321` (composer) gets a weak `focus-within:border-gray-100` on the container (`:308`) — visible but sub-visible contrast.
*Fix direction:* one global `:focus-visible { outline/ring }` rule + remove `outline-none` where no replacement exists; restyle Auth inputs to use `.input` or add focus border.

**A2. Core actions hidden behind hover — unreachable by keyboard — `high`**
- `MessageBubble.razor:48` — message action bar `opacity-0 group-hover:opacity-100`: buttons (copy, rate, regenerate, version-nav, edit, TTS) stay **focusable while invisible** — keyboard users tab onto invisible controls. No `group-focus-within`.
- `Sidebar.razor:~152` — per-chat `⋯` overflow button `invisible group-hover:visible`: `visibility:hidden` removes it from the a11y tree and tab order entirely → keyboard users can never open a chat's menu (rename/delete/pin/archive unreachable).
- `ChannelPage.razor:91` — `invisible ... group-hover:visible` on quick-react/reply/pin row: same — keyboard users cannot react, reply, or pin.
*Fix direction:* `group-focus-within` + `focus-visible` visibility, and on touch/`(hover:none)` show actions always or provide an always-visible affordance.

**A3. Nested interactive elements — `high`**
- `Sidebar.razor` folder rows: `<span @onclick="DeleteFolder…">` inside the folder `<button>` — invalid nesting; the inner span is not keyboard-focusable.
- `TerminalView.razor:35-36` — `<span title kill @onclick>✕</span>` inside session-tab `<button>`: same violation.
*Fix direction:* move the secondary action out of the button (sibling), make it a real `<button>` with an accessible name.

**A4. Touch targets below 24 px — `medium`**
`.icon-mini` = `size-5` (20 px, `tailwind.input.css:255`) used for memory/connection deletion (`SettingsModal.razor:170,220,234`, Admin rows). `.msg-action` (`:259`) is bare `text-xs` text with no padding — effective target ≈20 px tall — and carries destructive actions (🗑) across `Workspace`, `Notes`, `Automations`, `Admin`, `Archived`. Fails WCAG 2.5.8 (24 px min) and is far below the 44 px mobile norm.
*Fix direction:* min 24×24 (prefer 32–44) hit area via padding or `min-h/min-w`, keep visual size.

**A5. Live-region / announcement gaps — `medium`**
Chat stream is exemplary (`ChatView.razor:115` `role="log" aria-live="polite"`; status pill `:28`; toasts `ToastHost.razor:5`), but the channel view lacks parity: `ChannelPage.razor:58` message container has no `role="log"`/live region — realtime messages arrive silently for SR users; typing indicator `:131-136` is a plain div.
*Fix direction:* mirror the `role="log"` pattern on the channel list; `aria-live="polite"` on typing indicator.

**A6. Status communicated by color alone — `medium`**
Job/MCP/session status is a bare colored dot: `ChatWorkspacePanel.razor:187` (jobs), `:227` (MCP green/amber/gray), `TerminalView.razor:33`, `Calendar.razor:89,95,99,103` (today/events/failed/scheduled). No text alternative or legend. Fails WCAG 1.4.1.
*Fix direction:* pair dots with text/`title`/`aria-label` or a legend.

**A7. Decorative emoji/SVG announced to screen readers — `low`**
89 inline SVGs mostly inside `aria-label`ed buttons (fine), but decorative emoji are exposed: `ChannelPage.razor:50` 📌, `Workspace.razor:70` 📎 `:111` 📄, `FolderPage.razor:10` 📁, `SharedChat.razor:34` ◈ avatar glyph, `WorkspaceItemEditor.razor:136` 📄. `ChannelPage.razor:94-95` emoji react buttons rely on emoji as accessible name (works but announces inconsistently across SRs).
*Fix direction:* `aria-hidden="true"` on decorative glyphs; explicit `aria-label` on emoji-only buttons.

**A8. `blazor-error-ui` dismiss is a `<span>` — `low`**
`index.html:49` `<span class="dismiss">🗙</span>` is click-wired by the Blazor runtime but keyboard-inaccessible and has no accessible name/role.
*Fix direction:* `<button class="dismiss" aria-label="…">`.

### B. Forms

**B1. Visible labels not programmatically associated — `high` (systemic)**
The `.field > <label> + .input` pattern never sets `for`/`id` and does not wrap the input — ~40 fields in `SettingsModal.razor` (e.g. :36-37, :44-45, :82-83), ~100+ across `Admin.razor` (e.g. :368-377, :914-947, :1263-1295), all of `WorkspaceItemEditor`, `Automations.razor:27-78`, `NoteEditor.razor:45-52`, `Calendar.razor:31-44`, `Auth.razor:51,57,62`. Screen readers announce these inputs as unlabeled.
*Fix direction:* `for`/`id` pairs or wrap the input in the label.

**B2. Controls with no accessible name — `high`**
- `ChatView.razor:321` composer textarea — placeholder only; `:302` attachment-remove `×`.
- `ChannelPage.razor:146,189` textareas; `:150-155` and `:192-197` send buttons (icon-only, no `aria-label`/`title` — the primary action!); `:166` thread-close `✕`; `:94-95` emoji react buttons.
- `SettingsModal.razor:170` memory-delete `×`; `:220,234` connection-remove `✕`.
- `ShareDialog.razor:41,50,67,80` — three selects + principal input unlabeled.
- `Workspace.razor:112` file-remove `×`; `:123-125` expand `▲/▼` (also no `aria-expanded`); `:130-132` batch-select checkbox bare.
- `Admin.razor` — glyph-only buttons ✎ 🗑 ⏻ ◌ (e.g. :616-617, :1227) and dozens of placeholder-only inputs (:65-73 search/role filter, :398-448 provider fields, :627-699 audio, :966-981 integrations).
- `QuestionPromptCard.razor:38,52`, `PermissionPromptCard.razor:39` — inputs placeholder-only.
*Fix direction:* `aria-label` (localized) on icon buttons/inputs; `<label>` or `aria-label` on every field.

**B3. Auth form lacks `<form>` and `autocomplete` — `medium`**
`Auth.razor:47-79` — inputs are siblings of labels (not wrapped/associated), no `<form>` element, no `autocomplete` (`email`, `current-password`, `new-password`, `name`). Enter submits only from the password field (`:65`). Password managers and SR form navigation degrade.
*Fix direction:* wrap in `<form @onsubmit>`; add `autocomplete` + `type` + association.

**B4. Custom dropdown menus lack menu semantics — `medium`**
`ModelSelector.razor:16-41`, ChatView tools menu `:336-415` and mode menu `:475-505`, Sidebar `⋯` menus: triggers lack `aria-haspopup`/`aria-expanded`; menus lack `role="menu"`/`listbox`; no Escape close (backdrop-click only); no arrow-key navigation; focus not moved into/returned from the menu.
*Fix direction:* adopt menu/listbox keyboard contract (Esc, ↑/↓, focus return) or use native `<select>` where possible.

**B5. Overlays without full dialog behavior — `medium`**
`CallOverlay.razor:8` — full-screen takeover with no `role="dialog"`, no focus trap, no Escape close. `ChatView.razor:567-578` mobile workspace drawer has `role="dialog" aria-modal` but no focus trap and no Escape. `SettingsModal` is the correct in-repo reference (dialog + `trapFocus` + Escape at :622-624).
*Fix direction:* reuse `openwebui.trapFocus` + Escape handler on both overlays.

### C. Navigation & semantics

**C1. Route navigation implemented as buttons instead of links — `medium`**
- `Sidebar.razor:66` new-chat and chat items navigate via `@onclick`/`NavigateTo` — no `href` → no open-in-new-tab, no status-bar URL, weaker SR announcement ("button" not "link"), uncrawlable.
- `Workspace.razor:19-30` section tabs are real routes (`/workspace/*`) rendered as `<button @onclick="NavigateTo">`; edit buttons `:49,:158,:227` same.
- `Notes.razor:12,33`, `Automations.razor` new/edit, `AutomationDetail.razor:11`, `NoteEditor.razor:16`, `WorkspaceItemEditor.razor:14` (back buttons), `FolderPage.razor:31` (`role="link" tabindex="0"` + Enter handler — reinvents `<a>`).
*Fix direction:* real `<a href>` (Blazor intercepts client-side anyway) or `NavLink`; keep buttons for non-navigation actions.

**C2. Missing `h1` on primary routes + missing `nav` landmark — `medium`**
`FocusOnNavigate Selector="h1"` (`App.razor:4`) silently no-ops where no `h1` exists: chat routes (`/` `/c/*` — `ChatView` has no heading), `/channels/*` (`ChannelPage.razor:18-20` channel name is a plain div). `Sidebar` — the app's primary navigation — is `<div>`s, not `<nav>`. `Admin.razor:32` has the right `<nav aria-label>`; `MainLayout.razor:25` has `<main id="main-content">` + skip link (good).
*Fix direction:* visually-hidden `h1` per page (or promote channel name); wrap sidebar in `<nav aria-label>`.

**C3. `html lang` static mismatch — `low`**
`index.html:2` hardcodes `lang="en"`; runtime fixes it via `openwebui.setLang` (`app.js:68`, called at `LocalizationService.cs:108`) — but the splash + any pre-boot content announces the wrong language, and `en` mismatches the pt-BR default locale.
*Fix direction:* ship `lang="pt-BR"` (the default) or inject per-deployment.

### D. Motion & media

**D1. No reduced-motion support — `medium`**
Zero `prefers-reduced-motion`/`motion-reduce` in source while using: `animate-spin` (status spinners, e.g. `ChatView.razor:29,184,227`, `ChatWorkspacePanel.razor:53`), `animate-pulse` (`VoiceButton.razor:9`, `CallOverlay.razor:14-17`), `transition-all` (`Sidebar.razor:20`), `.typing` blink, and the animated `thinking.gif` (`MessageBubble.razor:10` — also the GIF-for-animation anti-pattern).
*Fix direction:* global `@media (prefers-reduced-motion: reduce)` kill-switch for animations/transitions; replace GIF with CSS/dot animation (or `<video muted loop>` if motion is essential).

### E. i18n & content

**E1. Locale coverage gap — `medium`**
`en-US`/`pt-BR` carry 656 keys; `de-DE`, `es-ES`, `fr-FR`, `it-IT`, `ja-JP`, `zh-CN` carry 598 — **58 keys missing** (mostly `admin.mcp_*`, `admin.perm_*`, `admin.config_*`), falling back to English (`LocalizationService` chain: active → en-US → pt-BR). Admin UI renders partially in English for those locales.
*Fix direction:* translate or mark intentionally-English; add a CI key-parity check.

**E2. Hardcoded locale strings — `medium`**
- `BannerBar.razor:21` `aria-label="dispensar"` (pt-BR for all locales).
- `Auth.razor:59` `placeholder="voce@exemplo.com"`.
- `FolderPage.razor:6` PageTitle `"Pasta"`.
- `boot.js:180-184` boot-failure copy pt-BR only.
- `WorkspaceItemEditor.razor:222` injected preset text pt-BR; `CallOverlay.razor:51`/`VoiceButton.razor:35` default `Lang = "pt-BR"` (should follow `L.Language`).
- Date formats hardcoded `dd/MM/yyyy HH:mm` (`Automations.razor:288`, `AutomationDetail.razor:117`, `Archived.razor:68`, `SharedChat.razor:78`) — wrong for en-US users.
*Fix direction:* route all through `L[...]`/`L.T` and culture-aware `ToShortDateString`/ICU format.

### F. Broken styling — undefined CSS classes `high`

Used but never defined in `tailwind.input.css` (nor generated `tailwind.css`) — render as unstyled natives:
| Class | Usages |
|---|---|
| `.button-sm` | `SettingsModal.razor:223,237,264`; `Admin.razor` ×8 |
| `.primary-btn` | `ShareDialog.razor:84`, `Calendar.razor:45` |
| `.card-meta` | `Admin.razor:181` |
| `.toggle` | `Admin.razor:1268,1288` (SAML/SCIM switches) |
| `.hint` | `SettingsModal.razor:197,203` (probably meant `.field-note`) |

*Fix direction:* define the classes (or replace with existing `.msg-action`/`.field-note`/Tailwind utilities) and regenerate `tailwind.css`.

### G. Performance (static + Lighthouse)

**G1. 805 KiB TTF webfont — `high`**
`assets/fonts/Inter-Variable.ttf` is 86 % of page weight (`tailwind.input.css:34-39` `@font-face`). WOFF2 variable Inter ≈ 100–300 KiB.
*Fix direction:* convert to WOFF2, add `<link rel="preload" as="font" crossorigin>`; `font-display: swap` already present.

**G2. Render-blocking head scripts — `high`**
`index.html:23-29` — six classic `<script>` (no `defer`) in `<head>`: `app.js`, `audio.js`, `codeexec.js`, `collab.js`, **`lib/xterm/xterm.js` (56 KiB, 85 % unused on boot)**, `xterm-addon-fit.js`, `terminal.js`; plus `xterm.css` `:13` render-blocking for a terminal-only feature. Lighthouse: ~4.6 s estimated savings (mobile).
*Fix direction:* `defer` all of them; lazy-load xterm (JS+CSS) on first `TerminalView` mount.

**G3. WASM boot chain cost — `high` (indicative local)**
`dotnet.native.*.js` evaluates 4.0 s on the main thread (mobile); 18 long tasks, worst 1,926 ms; TBT mobile ≈2.0–4.6 s / desktop ≈0.7–0.8 s. Serial chain document → `boot.js` → `dotnet.js` → `dotnet.native` has no preload hints; the b64 mirror fallback (`boot.js:138-155`) adds decode + `crypto.subtle` verify on the main thread when exercised.
*Fix direction:* `<link rel="preload">` for `dotnet.js`/`dotnet.native.*.js` + `.wasm`; investigate `startupMemoryCache`/`loadBootResource` parallelization; AOT where viable.

**G4. Meta description absent — `medium`**
`index.html` has none → sole SEO failure (91).
*Fix direction:* add `<meta name="description">`.

**G5. Image delivery — `low`**
`splash-dark.png` served 500×500 for 224 px display; `<img>`s lack `width`/`height` (`index.html:40`, `ChatView.razor:84`, `Auth.razor:27`, `MessageBubble.razor:10`, `ToolCallCard.razor:85`); `fetchpriority=high` not applied to LCP.
*Fix direction:* right-size the PNG, add dimensions/`fetchpriority` on the splash.

**G6. Unvirtualized lists — `low`**
All `@foreach` lists render fully (Sidebar chats/folders, `_messages`, Admin lists, knowledge). Fine at current sizes; flag if chat lists grow past ~200 items (`Virtualize` is already imported via `_Imports.razor:7`).
*Fix direction:* adopt `<Virtualize>` for sidebar/message lists at scale.

### H. UX consistency

**H1. Native `window.prompt`/`confirm` for destructive actions — `low`**
`app.js:35-40` exposes them; used by `Workspace.razor:337,373,409,431,440`, `Automations.razor:266`, `Archived.razor:56`, `Notes.razor:52`, `Admin` — blocking, unstyled, inconsistent with the in-app modal system (though keyboard/SR accessible).
*Fix direction:* shared `ConfirmDialog` component matching SettingsModal pattern.

**H2. `transition-all` — `low`**
Single usage: `Sidebar.razor:20` rail.
*Fix direction:* enumerate transitioned properties.

**H3. Modal-tab pattern lacks tab semantics — `low`**
`SettingsModal.razor:19-30`, `Workspace.razor:18-31`, `ChatWorkspacePanel.razor:17-28` tab rows are plain buttons without `role="tablist"/tab`/`aria-selected`/arrow-key nav.
*Fix direction:* tablist semantics or keep simple buttons consistently (document the choice).

---

## What works well (keep as reference)

- `SettingsModal` — `role="dialog"`, `aria-modal`, labelled, focus-trapped (`app.js:73-102`), Escape close, backdrop click.
- `ChatView` message list — `role="log" aria-live="polite" aria-relevant="additions"`; status pill `role="status"`.
- `ToastHost` — `role="status" aria-live`, auto-dismiss + manual dismiss, navigation-aware.
- `ShareDialog` — `aria-expanded` + `aria-hidden` chevron; `PermissionPromptCard`/`QuestionPromptCard` — `role="alertdialog"`.
- `BarChart` — `role="img"` + `aria-label`; invariant-culture SVG formatting.
- `MainLayout` — skip link → `#main-content` landmark; `Admin` — `<nav aria-label>`.
- Most icon buttons in ChatView/Sidebar carry both `title` and localized `aria-label`.
- Dark-mode `dark:` variants applied consistently; `dark` class set pre-paint (`index.html:14-22`); `theme-color` synced (`app.js:9`).
- No `user-scalable=no`, no `maximum-scale`, no `onpaste` blocking, no `autofocus`, no marquee/carousel — clean scan.
- Destructive deletes behind confirmation in all list pages.
- HTML escaping via `@()` + sanitized `MarkupString` markdown only.

## Coverage

**Reviewed (all):** `App.razor`, `_Imports.razor`, `Layout/{MainLayout,EmptyLayout}.razor`, `Components/{BannerBar,BarChart,CallOverlay,ChatView,ChatWorkspacePanel,MessageBubble,ModelSelector,PermissionPromptCard,QuestionPromptCard,SettingsModal,ShareDialog,Sidebar,TerminalView,ToastHost,ToolCallCard,VoiceButton}.razor`, `Pages/{Admin,Archived,Auth,AutomationDetail,Automations,Calendar,ChannelPage,ChatPage,FolderPage,NotFound,NoteEditor,Notes,Playground,SharedChat,TerminalPage,Watch,Workspace,WorkspaceItemEditor}.razor`, `wwwroot/index.html`, `tailwind.input.css`, `wwwroot/js/{app,audio,boot,codeexec,collab,py-worker,terminal}.js`, `wwwroot/i18n/*` (9 files), `Services/LocalizationService.cs` (fallback/lang wiring).

**Skipped:** `wwwroot/css/tailwind.css` (generated artifact — audited via source instead), `lib/xterm/*` (vendored), `manifest.webmanifest`, `service-worker.js` (PWA plumbing, out of design scope).

**Not applicable:** RTL locales (none shipped), drag-and-drop reorder (not implemented), data tables (`<table>` unused — card lists instead), video autoplay (none), cookie/consent banners (none), print styles (not a target), native date-localization components.

## Proposed finding groups (for SPECs)

| # | Group | Findings | Severity |
|---|-------|----------|----------|
| G1 | Keyboard & focus foundation | A1, A2, A3, B4 (Escape/focus parts), B5 | critical |
| G2 | Form semantics & accessible names | B1, B2, B3 | high |
| G3 | Navigation semantics & landmarks | C1, C2, C3 | medium |
| G4 | Broken/undefined CSS classes | F (all) | high |
| G5 | Boot performance | G1, G2, G3, G5 | high |
| G6 | i18n parity | E1, E2 | medium |
| G7 | Motion, status & announcements | D1, A5, A6, A7 | medium |
| G8 | Polish & consistency | A4, A8, G4, G6, H1, H2, H3 | low |
