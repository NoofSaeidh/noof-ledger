---
title: "`Microsoft.CodeAnalysis.PublicApiAnalyzers` was considered and deferred"
status: deferred
area: tests
since: 2026-09-22
---
It would lock public surface at build time via a hand-maintained `PublicAPI.Unshipped.txt` per
project — several hundred lines for `Domain` plus `Application` alone, whose public surface is
deliberately large because it *is* the cross-assembly contract. At type level it duplicates what
`PublicSurfaceTests` (added this phase) already does with no package at all; at member level it would
close a real gap that `jb inspectcode` (Tasks 7–8) now covers instead, as a periodic sweep rather than
a build gate. The maintenance cost of the unshipped files was judged not worth paying twice.
