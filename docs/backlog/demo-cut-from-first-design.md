---
title: "Demo: what was cut from the first design"
status: deferred
area: tests
since: 2026-09-26
---
The operator cut the demo back to a database with mock data and screenshots
(`docs/superpowers/specs/2026-09-26-demo-database-and-screenshots-design.md`). Not built, so nobody
re-proposes it as new: a live fake Telegram chat driving the real bot through a fake Bot API; a
rule-based fake model so the demo could categorise messages; seeding by replaying a conversation
through the app instead of writing mock rows; labelled before/after screenshot runs (git history does
that now); pixel-diff regression tests (`Verify.Playwright`). Voice notes have no pictures yet;
receipts got theirs when Phase 6 merged (a drawn photo, never a real one).
