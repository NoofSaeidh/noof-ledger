# Phase 1D — MudBlazor Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Adopt MudBlazor as this app's component library and rebuild all four screens on it, so that the dashboards Phase 2 onward will need are assembled rather than invented, and so the app looks like a product instead of a scaffold.

**Architecture:** MudBlazor is a UI-only dependency and belongs to `Noof.Ledger.Web` alone. Its services are registered from inside that assembly through a new `AddNoofWeb()`, matching the Phase 1C rule that the Host names no implementation type. Per-page interactivity stays exactly as it is — `Home` and `Login` are static SSR, `Counter` and `Secrets` are `InteractiveServer` — and the component choices on each page are constrained by that page's render mode rather than the render modes being changed to suit the components.

**Tech Stack:** MudBlazor 9.10.0 (MIT, explicit `net10.0` target — verified by inspecting the package, not by reading a compatibility table), .NET 10 Blazor Web App, Playwright for the browser gate.

**Spec:** `docs/superpowers/specs/2026-09-19-noof-finance-design.md` (this phase adds no behaviour the spec describes; it changes how the existing behaviour is presented)

## Global Constraints

- `Noof.Ledger.Web` is UI only: no `DbContext`, no EF types, no `HttpClient`, no `Program.cs`. `ProjectReferenceTests` asserts its exact `PackageReference` set — adding MudBlazor is an edit to that allowlist, deliberately.
- Each assembly registers its own services behind one `AddNoofXxx`. `Program.cs` names no implementation type.
- `TreatWarningsAsErrors` is on repository-wide. The build must stay at 0 warnings.
- Money renders through `CultureInfo.InvariantCulture`; `DashboardPageSourceTests` asserts it and forbids `.ToString("C")`.
- `Secrets.razor` must keep `prerender: false`, `autocomplete="off"`, the `finally` that clears the in-flight flag, its component-owned `CancellationTokenSource`, and must never name `GetAsync(`.
- Every routable page declares `[Authorize]`; only `Login.razor` may be `[AllowAnonymous]`.
- The 14 Playwright tests are the gate on "still works". Their selectors are listed per task and are not to be broken casually.

---

## Design decisions

These are the choices that would otherwise be made silently, with the reasoning that produced them.

### D1 — Render modes do not change

The tempting move is global interactivity (`<Routes @rendermode="InteractiveServer" />`), because then every MudBlazor feature works everywhere. It is the wrong move here for one concrete reason: `Login.razor` is a real HTML `<form method="post">` whose POST is what calls `SignInAsync`, and `SignInAsync` needs an `HttpContext` that an interactive circuit does not have. Making the app globally interactive means opting that page back out again, which is the same complexity arriving by a longer road.

So: render modes stay as they are, and each page uses the MudBlazor components that work under its own mode. The practical rule is that anything rendering to plain markup and CSS — `MudPaper`, `MudCard`, `MudTable`, `MudChart`, `MudChip`, `MudAlert`, `MudText`, the grid — works under static SSR, and anything that needs JavaScript — popovers, dialogs, snackbars, tooltips, menus, the drawer toggle — does not.

### D2 — A top app bar with plain links, not a drawer

`MudDrawer`'s open/close toggle needs interactivity, and `MainLayout` is static under per-page interactivity. A drawer that cannot be toggled is worse than no drawer. The app has four screens now and eight or nine over its whole roadmap, so a permanent top navigation is the right shape anyway.

### D3 — Feedback is inline, not a snackbar

Same constraint, and MudBlazor documents it in so many words on its own installation page: *"Static rendering is not supported: these providers must render in the same interactive render mode as the components that use them. With per-page interactivity, `MainLayout.razor` renders statically, so add the providers to each interactive page instead."*

So `MudPopoverProvider`, `MudDialogProvider` and `MudSnackbarProvider` are **not** added to the layout, and nothing in this phase uses a popover, dialog, snackbar, tooltip or menu. Every piece of feedback — a save result, a probe result, a bad password, a dead database — renders as a `MudAlert` in the page itself, which is plain markup under either render mode. A provider that is present but inert is worse than an absent one: it is a trap for whoever next reaches for a dialog and finds it silently doing nothing.

`MudThemeProvider` is the exception and stays in the layout, because a fixed theme is only a block of CSS variables and static rendering emits those perfectly well. That claim is load-bearing enough to be **checked in a browser** rather than assumed — if it turns out a static theme provider emits nothing, the fallback is to move it onto each page.

### D4 — Dark theme, defined in C#, no toggle

A toggle needs interactivity plus somewhere to persist the choice, and the layout is static. A theme object is just CSS variables, so a fixed theme costs nothing. Dark is the default because this is a local dashboard read in the evening, and because the palette is where "looks like a product" is actually won. Switching to light later is one parameter; a toggle is a backlog item.

### D5 — The dashboard is rebuilt, not restyled

This is the reason the phase exists. `Home.razor` currently prints an unstyled article per transaction and a bare `<table>` per currency. It becomes:

- a row of summary cards, one per currency, each showing this month's total and how many transactions produced it;
- a donut chart per currency of spending by category, which is the thing MudBlazor makes cheap and which Phase 2 will want more of;
- a category table per currency, right-aligned amounts;
- a recent-transactions list where status is a coloured `MudChip` rather than a CSS class nobody can see.

`#month-totals-{currency}` and `#txn-{id}` survive, because `DashboardTests` selects on them and those tests are how we know the read model still works.

### D6 — Login learns to report a failure

`AccountEndpoints` already redirects to `/account/login?failed=1` on bad credentials, and the page has never read that parameter: today a wrong password returns you to an identical blank form with nothing said. While the page is being rewritten anyway, it gains a `MudAlert` for that case. This is a defect fix that the rewrite makes free, not scope creep.

### D7 — Sign-out

Authentication is always on and there is no way to sign out. An app bar is exactly where that belongs, and the omission is only invisible today because there is no app bar. `/account/logout` is a POST (a GET sign-out is CSRF-able), so the app bar carries a small form. This is the one genuinely new endpoint in the phase.

### D8 — Native inputs stay where a form POST depends on them

`Login.razor` keeps `<input name="username">` and `<input name="password">` as native elements inside MudBlazor chrome. A component that may or may not emit a `name` attribute is not worth the risk on the one page whose whole job is a form POST, and `LoginTests` selects `input[name='username']`.

---

### D9 — System fonts, not Google's

MudBlazor's own setup instructions add a `<link>` to `fonts.googleapis.com`. This app binds to loopback, holds financial data, and is meant to work with the network down; a stylesheet fetched from Google on every page load contradicts all three, and buys a typeface nobody asked for. The theme sets a system font stack instead, which renders natively on this machine and makes no external request.

---

## File structure

**Created**

| File | Responsibility |
|---|---|
| `src/Noof.Ledger.Web/WebRegistration.cs` | The assembly's one public seam: `AddNoofWeb()` registers Razor components, interactive server components and MudBlazor's services. |
| `src/Noof.Ledger.Web/NoofTheme.cs` | The palette, typography and shape of the app in one place. No markup. |
| `src/Noof.Ledger.Web/Components/Layout/NavBar.razor` | The app bar: brand, navigation links, sign-out form. Split out of `MainLayout` because it is the only part of the layout with content worth reading. |
| `src/Noof.Ledger.Host/Endpoints/` (edit) | `/account/logout` — POST, `SignOutAsync`, redirect to the sign-in page. |
| `tests/Noof.Ledger.Architecture.Tests/ShellSourceTests.cs` | Asserts the shell actually loads MudBlazor's stylesheet and script and hosts a theme provider — the "silently unstyled" failure has no other detector. |

**Modified**

| File | Change |
|---|---|
| `Directory.Packages.props` | `MudBlazor` 9.10.0. |
| `src/Noof.Ledger.Web/Noof.Ledger.Web.csproj` | `PackageReference Include="MudBlazor"`. |
| `src/Noof.Ledger.Web/_Imports.razor` | `@using MudBlazor`. |
| `src/Noof.Ledger.Web/Components/App.razor` | MudBlazor CSS and JS; drop the scaffold stylesheet link's role as the only styling. |
| `src/Noof.Ledger.Web/Components/Layout/MainLayout.razor` | `MudThemeProvider` + `MudLayout` + `NavBar` + `MudMainContent`. |
| `src/Noof.Ledger.Web/Components/Pages/Home.razor` | Rebuilt per D5. |
| `src/Noof.Ledger.Web/Components/Pages/Settings/Secrets.razor` | Rebuilt on MudBlazor, every `id` preserved. |
| `src/Noof.Ledger.Web/Components/Account/Login.razor` | MudBlazor chrome, native inputs, failure alert. |
| `src/Noof.Ledger.Web/Components/Pages/Counter.razor` | `MudButton`, keeping `role="status"` — it becomes the proof that MudBlazor's *interactive* path works, not just its CSS. |
| `src/Noof.Ledger.Web/wwwroot/app.css` | Reduced to what MudBlazor does not cover. |
| `src/Noof.Ledger.Host/Program.cs` | Calls `AddNoofWeb()`; stops calling `AddRazorComponents` itself. |
| `tests/Noof.Ledger.Architecture.Tests/ProjectReferenceTests.cs` | Web's allowlist gains `MudBlazor`. |
| `tests/Noof.Ledger.E2E.Tests/CounterTests.cs` | Selector `button.btn` → a stable id, because `MudButton` emits its own classes. |

---

## Task 1: MudBlazor in the build, and the shell it needs

**Files:**
- Modify: `Directory.Packages.props`, `src/Noof.Ledger.Web/Noof.Ledger.Web.csproj`, `src/Noof.Ledger.Web/_Imports.razor`, `src/Noof.Ledger.Web/Components/App.razor`, `src/Noof.Ledger.Web/Components/Layout/MainLayout.razor`, `src/Noof.Ledger.Host/Program.cs`, `tests/Noof.Ledger.Architecture.Tests/ProjectReferenceTests.cs`
- Create: `src/Noof.Ledger.Web/WebRegistration.cs`, `src/Noof.Ledger.Web/NoofTheme.cs`, `src/Noof.Ledger.Web/Components/Layout/NavBar.razor`, `tests/Noof.Ledger.Architecture.Tests/ShellSourceTests.cs`

**Interfaces:**
- Produces: `Noof.Ledger.Web.WebRegistration.AddNoofWeb(this IServiceCollection) : IServiceCollection` and `Noof.Ledger.Web.NoofTheme.Instance : MudTheme`. Task 2 onward consume the theme implicitly through `MudThemeProvider`.

**Deliverable:** the app builds with zero warnings, every existing test still passes, and all four pages render inside a dark MudBlazor shell with working navigation.

- [ ] **Step 1: Widen the package allowlist test and watch it fail**

`ProjectReferenceTests.Web_package_references_are_exactly_its_allowed_set` is an exact-set assertion. Add `"MudBlazor"` to it *before* touching the csproj and run it: it must fail with MudBlazor expected but not present. That is the test proving it is the real gate on this assembly's dependencies rather than a list that happens to match.

- [ ] **Step 2: Add the package**

`Directory.Packages.props` gains `<PackageVersion Include="MudBlazor" Version="9.10.0" />`; `Noof.Ledger.Web.csproj` gains `<PackageReference Include="MudBlazor" />`. Re-run the test: green.

- [ ] **Step 3: `AddNoofWeb`**

`Program.cs` currently calls `AddRazorComponents().AddInteractiveServerComponents()` directly, which is the Web assembly's business. Move it, with `AddMudServices()`, into `WebRegistration`. `MapRazorComponents<App>()` stays in the Host — that is endpoint mapping, not service registration.

- [ ] **Step 4: Shell markup**

`App.razor` loads `_content/MudBlazor/MudBlazor.min.css` and `_content/MudBlazor/MudBlazor.min.js`. `MainLayout.razor` becomes `MudThemeProvider` + `MudLayout` + `NavBar` + `MudMainContent`. No popover, dialog or snackbar provider — see D3.

- [ ] **Step 5: The theme**

`NoofTheme` — palette, a system font stack per D9, border radius. Dark by default.

- [ ] **Step 6: Shell source tests**

Three assertions, each naming a failure that has no other detector: the stylesheet is linked, the script is loaded, a theme provider is present. Break each one deliberately and watch it fail before moving on.

- [ ] **Step 7: Full suite, then look at it**

`dotnet test --solution NoofLedger.slnx` must be green, and then the app must actually be opened in a browser. The blank-page defect got through a green suite; "the tests pass" is not the same claim as "it renders".
