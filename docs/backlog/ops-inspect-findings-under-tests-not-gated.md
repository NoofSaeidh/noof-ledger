---
title: "`ops/inspect.ps1` reports findings under `tests\` that it deliberately does not gate on"
status: done
area: ops
since: 2026-09-22
---
A run against the finished tree reported **84 findings under `tests\`**, 67 of them
`ClassCanBeSealed.Global` against xUnit fixtures. Phase 1C requirement 1 exempts tests
(«Исключение - тесты. Для них можно делать internal или private protected.»), and xUnit fixtures are
routinely left unsealed or subclassed for reasons the inspection cannot see — gating on a count that
can never reach zero guards nothing. `ops/inspect.ps1` prints the count and fails only on ERROR-severity
findings under `src\`. Worth a look occasionally; not worth a gate that can never pass.
