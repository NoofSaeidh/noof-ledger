---
title: Does any dependency throw an OperationCanceledException that is not our stopping token?
status: deferred
area: host
since: 2026-09-22
---
# Two things the closing review flagged and could not settle

Recorded 2026-09-22 by the Phase 1B closing review, which ran on a different model family from the
one that wrote the code. Neither is a proven defect; both are cheap to check and expensive to
discover the hard way.

`CategorizationWorker` and `TelegramPollingService` both keep the host alive by catching everything
*except* `OperationCanceledException`, which they let through because it means "we are shutting down".
`BackgroundServiceExceptionBehavior` is not overridden, so its .NET 10 default of `StopHost` applies:
anything that escapes `ExecuteAsync` takes the whole application down, the other poller included.

That is safe only while no dependency raises an `OperationCanceledException` for a reason other than
our own token. `AnthropicCategorizer` converts its timeout deliberately. The reviewer believed
`Telegram.Bot` 22 wraps a request timeout in its own `RequestException` but **did not verify it
against the package**, and a `TaskCanceledException` from `IChatNotifier` would reach the outer catch
as an `OperationCanceledException` and stop the host.

**To settle it:** drive `IChatNotifier.EditAsync` into a real timeout and see what type comes out. If
it is an `OperationCanceledException`, the filter needs to distinguish our token from anyone else's —
`ex is OperationCanceledException && stoppingToken.IsCancellationRequested` rather than a bare type
test.

**Still open after Phase 5 (2026-09-25).** Phase 5 made every hosted loop catch non-cancellation
exceptions per tick and await `IDatabaseGate` first (`CLAUDE.md` §4), but the filter itself is
unchanged — still a bare `ex is not OperationCanceledException` — and
`HostOptions.BackgroundServiceExceptionBehavior` is still the unoverridden .NET default `StopHost`
(confirmed directly by Phase 5's C-1 finding, `docs/OPEN-QUESTIONS.md` P5-1). A dependency that raises
`OperationCanceledException` for a reason other than the loop's own token would still stop the host.
The verification step above is still not done.
