---
title: The read model's "current zone" is supplied by a registered singleton now — a test gap remains
status: deferred
area: tests
since: 2026-09-22
---
**Corrected 2026-09-22 by Phase 1C Task 2.** This entry originally said the zone is "never given a
zone other than UTC". That was true of every test but overstated as a claim about production, which
is worth restating precisely now the wiring under it has changed.

`EfSpendingReadModel` takes the operator's current zone as a constructor argument and compares it
against each row's own stored zone — that pairing is the whole point of decision P1-3, and it is what
keeps a spend made in Belgrade on its Belgrade day after the operator moves. Before Phase 1C that
argument was threaded through a hand-written three-argument lambda registration in `Program.cs`.
Phase 1C Task 2 registers `TimeZoneInfo` itself as a singleton (`CaptureTimeZoneGuard.Resolve(...)`
against `Capture:TimeZone`, alongside the existing `AddSingleton(TimeProvider.System)`), and
`AddNoofPersistence` registers `EfSpendingReadModel` by type, resolving `currentZone` from the
container — so in production the read model now gets the operator's real configured zone, not UTC.

What has not changed: `EfSpendingReadModelTests` still constructs `EfSpendingReadModel` directly in
every test (`new EfSpendingReadModel(db, new FakeTimeProvider(now), TimeZoneInfo.Utc)`), bypassing DI,
so the original gap stands unchanged — the seam between "the zone the month boundaries are computed
in" and "the zone each row is bucketed by" is still never exercised with two different values. The
row-zone side is covered well (there is a test built so that a naive `date_trunc` would merge two
months the correct query separates). No defect is known; it is untested, not known-wrong.

**To settle it:** one test with `currentZone` set to something well away from UTC and rows carrying a
third zone, asserting which month each lands in.
