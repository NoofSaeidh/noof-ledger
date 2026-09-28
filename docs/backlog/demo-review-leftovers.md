---
title: "Demo: minor review findings left open"
status: deferred
area: tests
since: 2026-09-28
related: [demo-cut-from-first-design]
---
Found by the demo branch's closing review (Fable 5.1) and by Copilot's low-vote notes on PR #4, and
triaged as non-critical: none of them is wrong money, a leak or a broken build, and the pictures are
correct as they stand. Written down so nobody finds them again as new.

- **The opening-balance stagger bypasses `RevisionLog`, and its comment names the wrong cause.**
  `MockDataWriter` moves each opening's `occurred_at` with `ExecuteUpdateAsync` after
  `IWalletAdmin` has written the `Initial` revision. The lists do tie-break by `Id`; the real cause
  is that `EfWalletAdmin` gives wallets and openings a new `Guid` on every refresh. Fix the comment,
  or pass distinct times in instead of patching rows.
- **The `/health` picture is a hand-picked list.** `TelegramScenes` shows four checks; the app has
  seven (`Migrations`, `Disk` and `Log sink` are missing), and it says Telegram is "polling" while
  the dashboard picture shows Telegram failing. Tie the scene to the real `ISystemHealthCheck` names
  with a test, or build it from that list.
- **Two Telegram scenes don't match the mock ledger.** `income` shows Wise 5757.20 (the ledger has
  5762.30 after the salary), and `cancel-restore` shows Raiffeisen 175350 (175700 after the taxi).
  Take the numbers from `MockLedgerTests`' arithmetic.
- **`correction.png` shows the corrected echo above "no, 42".** Telegram edits the echo in place, so
  the order is faithful, but it reads backwards top-down. Either add a caption or reorder it.
- **Foreseeable failures print stack traces.** `DemoEntryPoint` catches only
  `InvalidOperationException`, so PostgreSQL being down (`NpgsqlException`), a host that never gets
  ready (`TimeoutException`) and other Playwright failures crash with a trace. It should say
  PostgreSQL is needed and point to `.\run.ps1 pg start`.
- **The advice after a hard kill is wrong.** If the tool is killed, the host keeps port 5264, and
  `run.ps1` then says "Ctrl+C in its window", but that window no longer exists. It should name the
  process to end.
- **The cost of anchoring to the current month is under-stated.** OPEN-QUESTIONS S-1 (e) and the
  RUNBOOK say only that pictures change once a month. On days 1–19, though, the demo shows activity
  dated in the future. And about 14 of the 20 app pictures rewrite when the month rolls over.
- **`RefreshTests.Refreshing_twice_leaves_exactly_one_demo_user`** asserts `NotBeNull`, not a count
  of one.
- **Dates come from the machine's calendar.** `MockData.MonthStart` uses `DateTime.Today`, but the
  app's "This month" uses the capture time zone. This only matters on the day the month changes.
- **Small things:**
  - `TelegramScenes.CreateEcho` returns a service from a provider that has already been disposed.
    It works only because `RecordEcho` holds no state.
  - `System.Drawing.Common` is on 9.0.16 although 10.0.x exists.
  - Two parts of the spec are out of date: the status line ("awaits the operator's review") and the
    "byte-identical" wording (it is now ±2 levels per colour channel, as the RUNBOOK says).
  - `/counter` (the template's page) has no picture.
  - The dashboard picture at the top of the README shows a red "No Telegram bot token configured"
    alert. The spec's no-token decision causes it, but the operator may not want it on the README.
