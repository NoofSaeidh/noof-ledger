---
title: "`PublicSurfaceTests`'s regex does not match `public delegate`"
status: deferred
area: tests
---
The allowlist test's `TopLevelPublicType` regex
(`tests/Noof.Ledger.Architecture.Tests/PublicSurfaceTests.cs`) matches `class`, `record`, `interface`,
`enum` and `struct` declarations, but not `delegate`. There are no delegates in the tree today, so the
lock is complete in practice — but a `public delegate` added to a scanned project later would slip past
the allowlist unnoticed, silently defeating the point of the guard (widening the surface is supposed to
be a reviewed edit to `PublicSurfaceTests.Allowed`). A one-line regex fix, worth doing with a
watch-it-fail-first check the day the first delegate is actually added.
