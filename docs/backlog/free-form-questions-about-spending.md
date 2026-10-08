---
title: Free-form questions about spending ("сколько я потратил на кафе в сентябре?")
status: deferred
area: ai
since: 2026-10-02 (Phase 8a, IR-1)
---
**What.** Asking the bot or the dashboard an arbitrary question about the ledger and getting an answer
computed from it.

**Why it is not done.** The operator split Phase 8 into 8a (finding what is wrong) and 8b (a ready-made
period summary). Free-form questions come after 8b. Whatever form they take, the figures in an answer are
computed by C# and the model only phrases them (`CLAUDE.md` §4 Money).
