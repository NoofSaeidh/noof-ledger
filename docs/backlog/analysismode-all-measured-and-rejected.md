---
title: "`AnalysisMode=All` was measured and rejected"
status: done
area: tests
since: 2026-09-22
---
# Phase 1C measurements — accessibility and composition analyzer choices

Recorded 2026-09-22 closing Phase 1C, so none of these gets re-proposed as new without the measurement
that already settled it.

A full rebuild with `-p:AnalysisMode=All` was run against the whole solution while Phase 1C looked for
an analyzer gate (requirement 4). It emitted **842 distinct warnings across 31 rules** — 376 `CA1707`
(underscores in names, this repository's deliberate test-naming convention, e.g.
`Every_routable_page_declares_its_authorization`), 258 `CA2007` (`ConfigureAwait`, meaningless in an
application with no synchronization context), 61 `CA2000`, and 28 `CA1062` (argument-null boilerplate
`CLAUDE.md` §3 forbids).

Adopting it would mean suppressing most of the rulebook and calling what survives "strictness" — the
opposite of a curated gate. Phase 1C Task 6 enabled a small named list instead (`CA1515`, `CA1852`,
`CA1862`, `CA1861`, `CA2263`, `IDE0005`), each chosen because the `AnalysisMode=All` run showed it cost
fewer than a handful of genuine fixes.
