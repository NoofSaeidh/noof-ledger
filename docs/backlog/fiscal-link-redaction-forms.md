---
title: Fiscal-link redaction catches only the canonical URL form
status: deferred
area: receipts
since: Phase 8a (2026-10-03, Codex review of the explanations and report-storage PR)
---
**What.** `FiscalVerificationUrl.StripUrl` is the one place that removes a fiscal receipt's verification URL
from free text before it reaches a model prompt, a bug report or its Markdown export (CLAUDE.md §4). It
matches only the canonical form: the configured `https` origin followed by a lower-case `/v/?vl=`. A link
written as `http://…`, with an upper-case `/V/`, with `vl=` later in the query (`?x=1&vl=…`), or a bare
`vl=…` payload with no URL around it passes through unchanged. Hardening it would mean a matcher that
ignores scheme and case, finds `vl=` anywhere in the query, and decides whether a bare payload is worth
the risk of cutting ordinary text — with tests for each form in every sink (the capture prompts, the
finding explainer's HTTP body, the stored report and the export).

**Why it is not done.** The receipt QR the app reads is always the canonical form, so the gap needs a
link typed or pasted by hand in another shape. The stripper is Phase 6 code shared with the capture path,
so the change belongs in its own PR rather than inside Phase 8a. The operator chose the backlog
(2026-10-03).
