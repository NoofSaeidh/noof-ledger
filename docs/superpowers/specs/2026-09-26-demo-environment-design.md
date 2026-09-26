# Demo environment, fake Telegram chat and screenshots (design)

**Status:** approved in conversation 2026-09-26 (sections 1–5), then revised after two independent
reviews (a codebase-feasibility pass and a cross-family design critique); this written spec awaits the
operator's review before an implementation plan is written. Branch `demo-environment`, cut from
`master` (67225ea), built alongside Phase 6 (receipts), which is still in progress in another session.

## Goal

Every change to something the operator sees — a page, or a message the bot sends — reaches the
operator's phone as screenshots, taken from a long-lived demo that runs the real app on made-up
data. The same demo can be opened by hand: `.\run.ps1 demo` starts the site and a Telegram-style
chat page that talks to the real bot code, with no configuration, no real secrets and no internet
access. Its data is produced by playing a readable script through the real app, so it cannot drift
from what the app actually writes.

## Decisions (operator's, 2026-09-26)

| # | Decision |
|---|---|
| D-1 | **Live fake chat**, not pictures. The chat page drives the real bot code (polling, routing, owner gate, echo edits, buttons); nothing is drawn from the echo text directly. |
| D-2 | **Screenshots on every visible change**, with the "done" report of that task: web pages at phone and desktop size, the chat at phone size, before and after for a screen that already existed. A phase close sends the full tour. |
| D-3 | **New branch off `master`.** The fake chat handles text, `/health` and Cancel/Edit/Restore now; receipt photos follow once Phase 6 lands. |
| D-4 | **Approach A: a separate demo tool runs the real app in-process** and swaps the Telegram transport and the model from outside. No production configuration key is added — the settled rule in `.claude/rules/tests-detail.md` stands untouched. |
| D-5 | **One demo database, `noof_ledger_demo`, one demo per machine**, long-lived: data persists between runs; `demo reset` rebuilds it. Never used by any test. |
| D-6 | **Demo data comes from a script** (`wallet`/`say`/`tap`/`reply` lines) played through the real app — no raw inserts. A feature that adds something visible adds lines to it. |

Rejected, with reasons, so nobody re-proposes them:

- **Telegram Web / Telegram Desktop / Telegram's test servers.** Telegram Web K/A are GPL-3.0 (nothing
  may be copied into this repo) and need a real account; test-server accounts can only be made by hand
  through the iOS app now (the `99966XYYYY` numbers are reported dead); none of it can be scripted
  reliably.
- **Existing fake Bot API servers** (`jehy/telegram-test-api`, Node; `werdnum/telegram-bot-api-mock`,
  Python). No UI, a foreign runtime, thin maintenance. Our bot uses six Bot API methods — a fake in C#
  is smaller than the integration.
- **Approach B: production `Telegram:BotApiBaseUrl` / model base-URL settings plus an out-of-process
  fake.** Settings whose real reason is the demo bend the settled rule; faking Anthropic's wire format
  (tool use, strict schemas) is a second thing to keep faithful.
- **A configuration-selected fake model inside `Noof.Ledger.Ai`.** Exactly what the settled rule forbids.
- **Screenshots from an already-running demo.** It shows the code that process was built from, not the
  worktree's current sources, and rebuilding while it runs fails on locked DLLs (review finding).
- **Visual regression (pixel-diff) testing.** `toHaveScreenshot` is JavaScript-only; `Verify.Playwright`
  would do it in .NET. The ask is human review, not an automated gate — `docs/BACKLOG.md`.

## 1. The demo environment

**Commands** (`run.ps1`, a new `demo` entry with sub-actions, parsed like `pg start|stop|status`):

| Command | Does |
|---|---|
| `.\run.ps1 demo` | Starts the demo in the foreground until Ctrl+C. Creates and seeds the database on first run. Prints both URLs, the sign-in and the age of the data. |
| `.\run.ps1 demo reset` | Removes the database, `chat.json` and `dp-keys`; recreates the database, seeds it, exits. Logs, backups and screenshots are kept. |
| `.\run.ps1 demo shots [-Screens a,b] [-Label before\|after]` | Starts its own demo in-process from the current sources, takes the screenshots (section 4), stops. |

`run.ps1` forwards to `dotnet run --project tools\Noof.Ledger.Demo -c Release -- <start|reset|shots>`,
turning `-Screens a,b` (which PowerShell hands over as an array) into `--screens a,b` and `-Label x`
into `--label x`. `demo` joins `RunScriptTests.CommandNames`.

**One demo at a time, machine-wide.** Every `demo` command holds
`%LOCALAPPDATA%\NoofLedger\demo\demo.lock` open exclusively for its whole life (released by the OS if
the process dies) and writes `demo.owner` beside it: worktree path, process id, command, start time.
`run.ps1` checks the lock **before building**, so a second command never rebuilds DLLs a running demo
has loaded; it refuses with the owner's details ("a demo started from <path> is running — stop it
first"). The ports and database are therefore never contended between worktrees or sessions. The
PostgreSQL suite lock is not taken: the demo neither clones the template nor runs tests.

**Addresses.** Site `http://127.0.0.1:5264`, chat `http://127.0.0.1:5265`. The real host keeps
`5263`, so the demo and the real app run at the same time. A port in use by something else fails
fast with a message naming it. Both bind to `127.0.0.1` explicitly, and the tool itself checks the
bound addresses with `LoopbackGuard.AssertSafe` after start — `Program.cs` registers that check on
`ApplicationStarted` after `builder.Build()`, which `WebApplicationFactory` may never reach, so the
demo's loopback guarantee must not rest on it.

**Database.** `noof_ledger_demo` on the same local PostgreSQL server.

- The tool builds its connection string itself: it reads the admin connection from
  `%LOCALAPPDATA%\NoofLedger\db.connection` (`Database=postgres`), rewrites the database to
  `noof_ledger_demo`, and refuses to continue unless the result names exactly `noof_ledger_demo`
  (checked with `NpgsqlConnectionStringBuilder`, not a substring) — the `update-test-template` pattern.
  It never calls `LedgerConnectionString.Resolve`'s fallback, which defaults to `noof_ledger`.
- The connection string reaches the host through in-process configuration (`UseSetting`), never a
  process argument.
- First run: `CREATE DATABASE noof_ledger_demo`, then `CREATE EXTENSION IF NOT EXISTS pg_trgm` and
  `unaccent` (migrations do not create them — `ops/reset-database-auth.ps1` does, for the other two
  databases). The host then migrates it with `Database:MigrateOnStartup=true`, as on every start: an
  older database simply moves forward.
- **Newer-schema guard.** Before starting the host, the tool compares `__EFMigrationsHistory` with the
  migrations it was compiled with. Migrations in the database that this build does not know mean
  another branch moved it ahead: the tool refuses, names those migration ids, and says `demo reset`
  rebuilds it at this branch's schema — replaying this branch's script, and discarding what the other
  branch seeded. With two branches carrying different migrations (today: this one and Phase 6), the
  demo belongs to whichever reset it last.
- `ops/clean-test-databases.ps1` adds `noof_ledger_demo` to `$Protected` (its `LIKE` patterns do not
  match it today; the exact-name refusal is the guard, per the script's own convention).

**Isolation.** Every path the demo writes is derived from one root, `DemoPaths`, which the
command-line boundary sets to `%LOCALAPPDATA%\NoofLedger\demo\` and a test sets to a temp directory:

| Setting | Value |
|---|---|
| `Logging:File:Directory` | `<root>\logs` |
| `DataProtection:KeyRingDirectory` | `<root>\dp-keys` — its own encryption keys; the real key ring is never read or written |
| `Backup:BackupDirectory` | `<root>\backups`, keeping the newest 2 — backups stay on, so the Backups health check and tile show the real feature, not a permanent Warning. `pg_dump` is local, and its password travels only through `PGPASSWORD`, as in production |
| chat transcript and counters | `<root>\chat.json` |
| screenshots | `<root>\shots\` |
| lock | `<root>\demo.lock`, `<root>\demo.owner` |

The host runs as `Production` (as `run.ps1 start` does), not `WebApplicationFactory`'s default
`Development`.

**No internet.** `ConfigureHttpClientDefaults` gives every named `HttpClient` a primary handler that
refuses every request; `telegram` is then pointed at the fake Bot API (section 2). Today that covers
`anthropic`, `groq` and `telegram`, and it covers any client added later without anyone remembering to.
(The plan verifies that the named `telegram` registration's handler wins over the default.)

**Sign-in and secrets**, set **unconditionally on every start** through the app's own services
(`IUserStore`/`IPasswordHasher`, `ISecretStore.SetAsync` — never set-if-missing, so a lost key ring
or a value edited on the Secrets page cannot leave the bot dead with a green-looking site):

- User `demo`, password `demo` — documented in `ops/RUNBOOK.md`. It guards made-up data on a
  loopback-only site; it is not a secret. (`UserCommand` enforces no password policy beyond non-empty.)
- `telegram-bot-token` = `123456789:demo-not-a-real-token`; `telegram-owner-chat-id` = the fake
  chat's id; `anthropic-api-key` and `groq-api-key` = `demo-not-a-real-key`. The Telegram and AI key
  health checks test presence only, so `/diagnostics` is green. A probe of the AI keys from the Secrets
  page fails — the offline handler refuses it — which is the truth.

**Start sequence** (`start`, and the first half of `shots` and `reset`): take the lock → resolve and
guard the connection string → create the database if missing → newer-schema guard → start the chat
server (5265) → start the app in-process (5264) → assert loopback → wait for `IDatabaseGate` to report
`Ready` → set the user and secrets → if the database was just created, play the demo script → print
URLs, sign-in, data age.

## 2. The fake Telegram chat and the fake model

**Fake Bot API** (`FakeBotApi`, in the tool). The `telegram` client's primary handler is an in-memory
`HttpMessageHandler` that parses `/bot<token>/<method>` plus the JSON body and dispatches to it — no
socket. It answers exactly the six methods the bot calls (`TelegramPollingService`,
`TelegramChatNotifier`):

| Method | Behaviour |
|---|---|
| `getUpdates` | Long-poll: returns pending updates with `update_id >= offset`, otherwise waits up to `timeout` seconds (the bot sends 30) before returning `[]`. Returning `[]` at once would spin the polling loop. |
| `sendMessage` | Adds a bot message (honouring `reply_parameters` and `ForceReply`'s placeholder); returns `message_id`, `date`, `chat`. |
| `editMessageText` | Replaces text and inline keyboard, marks the message edited; returns the message. |
| `answerCallbackQuery`, `deleteWebhook`, `setMyCommands` | Returns `true`. |
| anything else | `{"ok":false,"error_code":400,"description":"not supported by the demo"}` — visible, not silent. |

Responses use Telegram's envelope `{"ok":true,"result":...}`. One private chat, fixed chat and user
ids. Voice (`getFile`) and photos are out of scope (D-3); the chat page offers no way to send them.

**Persistence and the update offset.** `chat.json` holds the transcript **and** the next `update_id`
and `message_id`, written after every change and loaded on start. The bot keeps its own offset in the
database (`telegram-update-offset`) and ignores any update below it, so on load the fake also starts
its `update_id` counter at no less than that stored offset — otherwise a restarted demo, or one whose
`chat.json` was deleted, would have a chat that silently never answers. `demo reset` deletes
`chat.json` together with the database, since the offset lives there.

**Chat page** (`http://127.0.0.1:5265`, static HTML/CSS/JS served by the tool's own Kestrel; styling
written from scratch — nothing copied from Telegram's GPL clients; laid out for phone width):

- Bubbles: the operator's on the right, the bot's on the left, times, an "edited" mark.
- Inline keyboards under a bot message as a row of buttons; tapping one queues a `callback_query`.
- Tapping a bubble enters reply mode (a quoted strip above the input); a bot message with `ForceReply`
  puts the input into reply mode itself, with its placeholder (`no, 1500`).
- `/health` works: the bot's owner check passes because the owner secret is the fake chat's id.
- JSON behind it: `GET /api/transcript`, `POST /api/send {text, replyTo?}`, `POST /api/tap
  {messageId, data}`. The page polls the transcript every 500 ms.

**Fake model** (`DemoChatClient : IChatClient`, returned by a replacement `IChatClientFactory`
registered by the tool). The categoriser's own chain is untouched: `FunctionInvokingChatClient` →
`AnswerToolGuard` → this client. It answers the forced tool it is offered, on the first call:

- `record_transaction` — never `list_merchants` first — with arguments in exactly the tool's schema
  (`CategorizationSchema`): `items` (`description`, `amount`, `currency`, `category_slug`,
  `merchant_name`, plus `known_merchant_id` when the schema declares it), `occurred_on`, `kind`,
  `wallet_id`, `balance_amount`, `balance_currency`.
- `canonicalize_merchant` — the merchant name it was given, title-cased.

It reads the request, not the database:

- The message and today's date from the user turn (`Message:` and `Today:` lines of
  `CategorizationPrompt.BuildUserTurn`); on a correction, the `Current record` lines and the
  `Correction from the person:` text.
- Wallet ids, names and aliases from the user turn's `Wallets:` lines; known merchants from its
  `Known merchants:` lines; category slugs from the schema's `category_slug` enum (a slug the enum
  does not offer is never used).

Rules, deterministic — the same message always gives the same answer:

| Input | Answer |
|---|---|
| `coffee 350 rsd` | expense, one item, `coffee` category, currency RSD |
| `groceries 2400, milk 180 rsd` | expense, two items (split on commas; a trailing currency applies to all) |
| `Maxi groceries 2400`, `dinner at Walter 4200` | a leading capitalised word, or `at <Name>`, is `merchant_name`; if `Known merchants:` lists it, `known_merchant_id` from the schema's enum instead |
| `salary 2800 eur`, `+450 usd freelance` | income (`+` prefix or an income word: salary, income, freelance, refund) |
| `wise balance 3050` | balance statement for that wallet, `balance_amount` 3050, its currency |
| `yesterday`, `3 days ago`, `14.09` | `occurred_on`; otherwise `null` (the app uses the message's day) |
| `@kaspi`, or a wallet's name/alias as a word | `wallet_id`; otherwise `null` (the app's default-wallet rule applies) |
| a correction with a date phrase or a category word | the complete record with that one thing changed |
| a correction with a number, single-line record | the complete record with that line's amount changed |
| a correction with a number, multi-line record | throws `ModelCallException(ModelFailureKind.Terminal, ...)` → the app's real "Could not apply that correction — the record is unchanged" echo |
| no number at all (`#@%&`) | throws `ModelCallException(ModelFailureKind.Terminal, ...)` → the app's real failure echo at once, instead of eight transient retries over an hour |

Category keywords map to the seeded slugs (`coffee`, `restaurants`, `groceries`, `transport`, `fuel`,
`public-transport`, `housing`, `utilities`, `health`, `subscriptions`, `entertainment`, `travel`,
`education`, `gifts-donations`, `personal-care`, `clothing`, `salary`, `refund`); anything unknown is
`other` (`other-income` for income).

The real model's behaviour stays covered by the live suites; the demo shows what the app does with an
answer, not how good the answer is.

**Visibility.** `Program`, `IChatClientFactory`, `CategorizationPrompt`, `CategorizationSchema` and
`LoopbackGuard` are `internal`. New `InternalsVisibleTo` entries — the only production-side change,
and not a behavioural one:

| Declaring assembly | Gains |
|---|---|
| `Noof.Ledger.Ai` | `Noof.Ledger.Demo`, `Noof.Ledger.Demo.Tests` |
| `Noof.Ledger.Host` | `Noof.Ledger.Demo`, `Noof.Ledger.Demo.Tests` |
| `Noof.Ledger.Demo` | `Noof.Ledger.Demo.Tests` |

(`DynamicProxyGenAssembly2` only if a test substitutes an internal interface — the fakes are
hand-written, so none is planned.) An architecture test asserts that `Noof.Ledger.Demo` is the only
non-test assembly any `src` project grants `InternalsVisibleTo`.

## 3. Demo data: the script

`tools/Noof.Ledger.Demo/demo-script.txt`, played by `demo reset` and by a first start:

```
# wallets — through IWalletAdmin, the same service /wallets uses
wallet Wise, EUR, opening 3000, 20 days ago, default
wallet Raiffeisen, RSD, opening 45000, 20 days ago, default, aliases raif
wallet Cash, USD, opening 400, 20 days ago, default
wallet Tinkoff, RUB, opening 52000, 20 days ago, default
wallet Kaspi, KZT, opening 180000, 20 days ago, default
wallet Old Revolut, EUR, opening 0, 60 days ago
archive Old Revolut

# messages — typed into the fake chat, each waits for the bot's final answer
say coffee 350 rsd 3 days ago
say Maxi groceries 2400, milk 180 rsd yesterday
say salary 2800 eur @wise 10 days ago
say taxi 900 rsd
tap Cancel
tap Restore
say dinner at Walter 4200 rsd 2 days ago
tap Edit
reply no, 3900
say wise balance 3050
say #@%&
say /health
```

(The committed script is longer: a few weeks of expenses across all five currencies. This is its
shape.)

"Main Wallet" (RSD) is created by migration `AddCaptureModel` in every database, as the RSD default.
Raiffeisen takes the RSD default over (through `NewWallet.IsDefaultForCurrency`, or
`MakeDefaultForCurrencyAsync` if creation does not move an existing default — the plan pins which), so
RSD spending lands there; Main Wallet stays at zero, "Not yet reconciled" — what a fresh install shows.

| Line | Runs | Finished when |
|---|---|---|
| `wallet <name>, <CUR>, opening <amount>, <when>[, default][, aliases a\|b]` | `IWalletAdmin.CreateAsync(NewWallet)` | the call returns |
| `archive <name>` | `IWalletAdmin.ArchiveAsync` | the call returns |
| `say <text>` | a new message from the fake chat | its echo's text is no longer `Recording…` and it carries at least one button; for a `/command`, the bot's plain reply has arrived |
| `reply <text>` | a message replying to the most recent bot message | the corrected echo's text is no longer `Correcting…` and it carries at least one button |
| `tap Cancel` / `tap Restore` | taps that button on the most recent bot message that has it | that echo is edited to carry the opposite button (`Restore` / `Cancel`) |
| `tap Edit` | taps Edit on the most recent bot message that has it | a new bot message with `force_reply` has arrived |
| `# ...`, blank | ignored | — |

`<when>` is `today`, `yesterday` or `N days ago`, relative to the reset day. Dates inside `say` text
are read by the fake model the same way. Each chat step has a 30-second limit; a step that fails or
times out stops the reset with the line number and text, and the bot's last message. Expect roughly
3–6 seconds per chat step (the categorisation worker polls every 5 seconds when idle), so a full reset
takes a few minutes.

Because the app writes everything — transactions, entries, balance checks, merchants, revisions, the
trace stages and the log rows — the data stays exactly what the app would write today.

**Data age.** The reset time is stored in `chat.json`. `start` and `shots` print how old the data is;
older than 7 days, `shots` warns that the dashboard's recent activity will look empty and suggests
`demo reset`.

**Standing rule:** a feature that adds something visible also adds lines here (and a screen, section
4), so a reset shows it.

## 4. Screenshots and the requirement

**`demo shots`** (Playwright for .NET, `Microsoft.Playwright` 1.62.0 to match the E2E suite's
`Microsoft.Playwright.Xunit.v3`; Chromium from the same `%LOCALAPPDATA%\ms-playwright` install the E2E
suite needs — missing, the tool prints the install command and stops):

- **Always its own demo.** `shots` takes the lock, builds and starts the demo in-process from the
  worktree's current sources, shoots, and stops — so a screenshot can never show code other than what
  is on disk right now. With another demo running, `run.ps1` refuses before building.
- **Screens** are a list in code (`Screens.cs`): name, how to find the URL, and a ready marker.

  | Screen | URL | Ready marker |
  |---|---|---|
  | `login` | `/` signed out (redirects to sign-in) | `input[name='username']` |
  | `dashboard` | `/` | `#balances` |
  | `wallets` | `/wallets` | `#create-wallet` |
  | `transactions` | `/transactions` | `#transactions-grid` |
  | `trace-ok` | `/transactions/<id>/trace`, latest completed | `#trace-timeline` |
  | `trace-failed` | `/transactions/<id>/trace`, latest failed | `#trace-timeline` |
  | `diagnostics` | `/diagnostics` | `#diagnostics-checks` |
  | `logs` | `/diagnostics/logs` | `#logs-grid` |
  | `log-settings` | `/diagnostics/logs/settings` | `#retention-verbose` |
  | `secrets` | `/settings/secrets` | `#status-anthropic-api-key` |
  | `chat` | the chat page | the last bubble |

  Markers are the ids the E2E tests already wait on. Trace ids come from the app's own read
  services, not SQL.
- **Capture.** Web screens at phone 390×844 (device scale 2) and desktop 1440×900; the chat at phone
  only. Sign in once per browser context. Per screen: navigate → ready marker → network idle → 500 ms
  settle — the wait these `InteractiveServer`, `prerender:false` pages need (a bare load event shows
  the pre-circuit shell). Every screen, the chat included, is captured full page; animations
  disabled, caret hidden.
- **Output.** `<root>\shots\<yyyyMMdd-HHmmss>[-<label>]\`:
  - `<screen>-<phone|desktop>.png` — lossless originals.
  - `send\<screen>-<size>[-partN].jpg` — the phone-deliverable set, JPEG quality 88, cut by
    Playwright's own `Clip` into slices no taller than 4000 device pixels. (A 780×13528 PNG was refused
    by the file transfer; 4000-pixel JPEG parts went through.)
  - `manifest.json` — screens, label, `HEAD`, whether the working tree was dirty, data age, time.

  The tool prints the folder and each file to send, in order.
- **`-Label before` refuses on a dirty tree** (`git status --porcelain -- src tools` non-empty): a
  "before" must show the code before the change, and this makes that mechanical rather than a promise.

**The requirement.** One bullet in `CLAUDE.md` §5, where it loads in every session:

> **Show what changed** *(settled 2026-09-26)*. A change to anything the operator sees — a page, or a
> message the bot sends — ends with screenshots of the affected screens from the demo
> (`.\run.ps1 demo shots`), sent with the report that says it is done: "before" shots of screens that
> already exist, taken before the first change, and "after" shots. A phase close sends the full tour.
> Never from `noof_ledger`. How: `.claude/skills/demo-screenshots/SKILL.md`.

`docs/CLOSING-A-PHASE.md` gains a checklist item: `demo reset`, then `demo shots` (all screens), sent
in full.

**The skill** (`.claude/skills/demo-screenshots/SKILL.md`), the verified procedure:

1. Before the first change — as the first task of any plan that touches a screen — on a clean tree:
   `.\run.ps1 demo shots -Screens <every affected existing screen> -Label before`. A new screen has no
   "before".
2. Make the change. If it adds something visible: add lines to `demo-script.txt`, a screen to
   `Screens.cs` if it is a new page.
3. `.\run.ps1 demo reset` if the script changed or `shots` warned that the data is old.
4. `.\run.ps1 demo shots -Screens <affected> -Label after`.
5. Send the `send\` files through `SendUserFile`, before then after, each named in the message; cite
   both shot folders (their manifests show the "before" was pre-change).
6. Troubleshooting: another demo is running (stop it); "run demo reset" (a newer schema from another
   branch); a port in use by something else; Chromium missing.

A path-scoped rule, `.claude/rules/demo.md` (`paths: tools/Noof.Ledger.Demo/**`,
`tests/Noof.Ledger.Demo.Tests/**`), carries the tool's own invariants for whoever edits it: the exact
database-name guard, everything under `DemoPaths`, no internet, no production configuration key, the
script grows with features.

## 5. Code layout and testing

**Projects.**

- `tools/Noof.Ledger.Demo` — console app (`Microsoft.NET.Sdk.Web`, for its own Kestrel and static
  files). References `Noof.Ledger.Host` (and through it every assembly), `Microsoft.AspNetCore.Mvc.Testing`
  (`WebApplicationFactory<Program>` with `UseKestrel(...)`, available in 10.0.8) and
  `Microsoft.Playwright`. Listed in `NoofLedger.slnx` under a new `/tools/` folder, so `dotnet build`
  fails the moment a seam it swaps changes. Not a test project: `dotnet test --solution` never starts
  it.
- `tests/Noof.Ledger.Demo.Tests` — its tests, with its own `xunit.runner.json` setting
  `parallelizeTestCollections: false` (Serilog's logger is a process-wide static, as in `Host.Tests`
  and `E2E.Tests`).

The runner that starts the host takes the connection string and a `DemoPaths` root as parameters.
The command-line boundary is the only place `noof_ledger_demo` and `%LOCALAPPDATA%\NoofLedger\demo`
come from; a test passes a template clone and a temp directory it deletes, so no test ever touches
the operator's demo, its transcript or its keys.

**Tests (TDD, as everywhere; none touches `noof_ledger_demo`).**

| Subject | Kind |
|---|---|
| Fake model rules: every row of the table in section 2, requests built with the real `CategorizationPrompt.BuildUserTurn` and `CategorizationSchema` so a prompt-format change breaks a test, not the demo | unit |
| Demo script reader: each line kind, `<when>` forms, errors carrying line numbers | unit |
| `FakeBotApi`: each method, long-poll wait and `offset`, edit replacing keyboard, unknown method refused, transcript save and load, and after load new update and message ids continue past both the saved ones and a stored offset | unit |
| Connection guard: only exactly `noof_ledger_demo`; a failed rewrite refuses | unit |
| Runner settings: `Logging:File:Directory`, `DataProtection:KeyRingDirectory`, `Backup:BackupDirectory`, chat and shots paths all lie under the given root; `ConnectionStrings:Ledger` equals the given string; environment `Production` | unit |
| Offline handler: a named client other than `telegram` is refused | unit |
| Demo lock: a second holder is refused with the owner's details | unit |
| End to end: the in-process demo host against a clone of `noof_ledger_test_template` and a temp root plays `wallet` + `say coffee 350 rsd` + `tap Cancel`; the transcript shows `Recorded —` then `Cancelled —`; the bound addresses are loopback only | database (runs filtered, like other DB classes) |
| No `src` project references anything under `tools/`; `Noof.Ledger.Demo` is the only non-test `InternalsVisibleTo` target in `src`; `TestHostLogDirectoryTests` extended to `tests/Noof.Ledger.Demo.Tests` and `tools/` | architecture |

**First task of the plan — prove the riskiest assumption.** `WebApplicationFactory<Program>` +
`UseKestrel` on a fixed loopback port has never been used in this repo. The plan starts with a spike
test: the in-process host, as `Production`, serves the sign-in page on 5264 with MudBlazor's styles
loaded (checked in the served CSS, per the `ThemeTests` lesson), and `IServerAddressesFeature`
reports exactly `http://127.0.0.1:5264`. If it cannot be made to work, stop and come back to the
operator — approach B is the fallback, and it needs a decision.

## 6. Documentation

- `ops/RUNBOOK.md` — "Demo environment": commands, addresses, sign-in, where its files live, one demo
  per machine, reset, troubleshooting.
- `README.md` "Running it" — one line for `.\run.ps1 demo`.
- `docs/STATUS.md` and the status line in `CLAUDE.md`.
- `docs/OPEN-QUESTIONS.md` — D-1..D-6 and the rejected options above.
- `docs/BACKLOG.md` — voice notes in the fake chat (needs a fake speech factory and `getFile`);
  receipt photos once Phase 6 lands; visual regression with `Verify.Playwright`; automatic
  "which screens changed" detection; a separate demo per worktree, if one shared demo proves too
  contended.

## Out of scope

Voice notes and receipt photos in the fake chat (BACKLOG); pixel-diff regression; running the demo
anywhere but the operator's machine; a real model or real bot in the demo; more than one demo at a
time.
