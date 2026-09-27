---
title: ReSharper's other 42 WARNING-level findings were seen and not triaged
status: deferred
area: tests
since: 2026-09-22
---
A pre-settings baseline run of `jb inspectcode` found 42 issues at `WARNING` severity or above that are
not about accessibility (accessibility is handled separately, raised to ERROR): 18 `InconsistentNaming`
(ReSharper wants `_camelCase` private fields, which this repository does not use — a settings decision
somebody should make deliberately rather than by silence), 12 `AccessToDisposedClosure`, 4
`FormatStringProblem` in `EfCategorizationStore.cs` and `EfJobQueue.cs` (probably false positives about
raw SQL parameters, not checked), 3 `RedundantSuppressNullableWarningExpression`, 2
`ParameterHidesPrimaryConstructorParameter`, 2 `NotAccessedPositionalProperty.Global`, and 1
`UsingStatementResourceInitialization`. Out of Phase 1C's scope (accessibility and composition, not
general code health); none is gated at ERROR by `NoofLedger.sln.DotSettings`.
