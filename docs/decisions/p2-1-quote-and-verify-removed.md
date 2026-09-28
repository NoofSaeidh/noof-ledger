---
id: P2-1
title: "Quote-and-verify is removed; the safety is undo, not rejection"
status: decided
date: 2026-09-22
phase: Phase 2
related: [p1-4-extraction-is-llms-job]
---

The operator's words: *"не надо делать валидацию. просто в чат тг должен присылаться распознанный
вариант. и его можно уже руками поправить. или написать как обработать. но главное его можно
отменить и изменить. это важнее guarda"*.

Quote-and-verify (P1-4 point 2) rejected every amount not written as digits — *"штуку евро"*,
*"полтос"*, and most speech-to-text output. Voice is the main capture path, so the guard blocked the
main use case. The model now interprets amounts and dates freely; the bot echoes the stored result
with **Отменить** / **Изменить**; a reply in free text corrects it; every state is kept in
`transaction_revisions`. The CLAUDE.md money rule keeps its force for reports, totals and balances,
and no longer applies to capture. Full design:
`docs/superpowers/specs/2026-09-22-natural-language-capture.md`.

**Do not re-propose a validation layer on capture** — evidence spans, verbatim checks or sanity bounds
— without the operator asking. It was considered and refused in favour of cheap undo.
