---
title: "`Noof.Ledger.E2E.Tests` was not in `NoofLedger.slnx` — fixed 2026-09-22"
status: done
area: tests
since: 2026-09-22
---
Discovered in Phase 1C when an unused-`using` error in
`tests/Noof.Ledger.E2E.Tests/CookieModeHostFixture.cs` survived a clean `dotnet build
NoofLedger.slnx`: the E2E project was not a member of the `.slnx`, so neither `dotnet build
NoofLedger.slnx` nor `dotnet test --solution NoofLedger.slnx` ever touched it, and `ops/publish.ps1`
— which only runs `dotnet test --solution` — did not cover it either. A test suite outside the
solution is a test suite that rots without anybody noticing.

Added to the `.slnx` at the operator's instruction, with both consequences accepted knowingly:
`dotnet test --solution` now runs the browser suite too, so a plain test run needs Playwright's
Chromium present and takes about 40 seconds longer, and the solution test count now includes those
13. Every number in this repository's documentation counts the whole solution from here on.
