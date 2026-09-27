---
title: MudBlazor features that need a render-mode decision first — deferred 2026-09-22
status: deferred
area: web
since: 2026-09-22
---
Phase 1D adopted MudBlazor but deliberately uses none of its JavaScript-backed components. MudBlazor
documents that its providers "must render in the same interactive render mode as the components that
use them", and `MainLayout` renders statically because render modes are per-page — which is itself
forced by the sign-in page being a real form POST. So there is no `MudPopoverProvider`,
`MudDialogProvider` or `MudSnackbarProvider`, and therefore no popover, dialog, snackbar, tooltip,
menu, `MudSelect`, `MudDatePicker` or `MudAutocomplete` anywhere in the app.

Three ways out, when something actually needs one:

1. **Put the providers on each interactive page.** MudBlazor's own documented answer for per-page
   interactivity. Cheapest, and the duplication is two lines per page.
2. **Go globally interactive** (`<Routes @rendermode="InteractiveServer" />`) and mark the sign-in
   page `[ExcludeFromInteractiveRouting]`. Cleaner afterwards, but it makes every page hold a
   SignalR circuit, including the dashboard, which today needs none.
3. **Keep doing without.** A single-user local ledger with four screens has not yet wanted a dialog.

Nothing is blocked today. This is written down so the next person who reaches for `MudDialogService`
and finds it silently doing nothing knows why in one minute rather than one afternoon.
