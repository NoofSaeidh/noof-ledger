---
paths:
  - "src/Noof.Ledger.Web/**"
  - "**/*.razor"
  - "tests/Noof.Ledger.E2E.Tests/**"
  - "tests/Noof.Ledger.Architecture.Tests/ShellSourceTests.cs"
  - "tests/Noof.Ledger.Host.Tests/ThemeTests.cs"
---

## CLAUDE.md §4 — Architecture: render modes

- **Render modes are per-page and stay that way.** The sign-in page is a real form POST, because
  `SignInAsync` needs an `HttpContext`. So `MainLayout` renders statically, MudBlazor's popover,
  dialog and snackbar providers cannot work from there, and nothing may use a popover, dialog,
  snackbar, tooltip or menu. Feedback is an inline `MudAlert`. Ways out are costed in
  `docs/BACKLOG.md`; taking one is a decision, not a convenience.

## CLAUDE.md §4 — Testing: statically rendered pages and silent look failures

- **A statically rendered page's state is read once, at render** — an E2E fixture waiting for
  something to change (the database gate, a background job) must wait for the actual rendered marker
  (the sign-in page rendering *without* `id="database-waiting"`), never for a bare HTTP 200; a page can
  200 while still showing what it rendered before the change.
- **A look that fails silently needs a test that reads what the app serves, not the source.** The
  theme once emitted `font-family: 'system-ui, -apple-system, ...'` — one quoted name no machine
  has — and every page rendered in Times New Roman while all 482 tests passed. `ShellSourceTests`
  and `ThemeTests` are that detector; a source-text assertion could not have been.
