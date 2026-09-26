---
paths:
  - "tests/**"
---

## CLAUDE.md §4 — Testing details

- **Test hosts never write into the operator's real log directory** (Phase 5).
  Every `WebApplicationFactory<Program>` and E2E host fixture must point `Logging:File:Directory` at a
  per-fixture temp directory (the `TestHostLogging` helpers, guarded by `TestHostLogDirectoryTests`) —
  before this, test runs wrote files straight into `%LOCALAPPDATA%\NoofLedger\logs` and could evict the
  operator's own logs under the 14-file retention cap. `Host.Tests` runs with
  `parallelizeTestCollections=false` because Serilog's logger is a shared static.
- **No production configuration key exists solely so a test can flip a code path** *(settled
  2026-09-25, Phase 5)*. A `Diagnostics:ForceLogSinkFailureForTests` hook was added, then removed in
  review, in favour of driving the real failure (an unreachable sink) from the test itself.
- **Never seed a test with `DateTimeOffset.UtcNow` and then assert exact equality against a value
  read back from PostgreSQL.** `timestamptz` keeps microseconds; a .NET tick is 100ns. A timestamp
  whose final tick digit is non-zero is truncated on the round trip, so the assertion fails most
  runs but not all — the worst kind of flake. Seed from a fixed literal, or compare with
  `BeCloseTo`. This shipped twice before it was caught.
