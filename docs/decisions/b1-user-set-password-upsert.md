---
id: B1
title: "Does `user set-password` CREATE the `app_user` row, or require it to exist? There is no `/register` and no `/setup` page, so nothing else can create it"
status: decided
phase: Phase 0b, task 13
---

**Default taken:** Upsert — it creates the row if absent. Otherwise the app has no path to a first
user at all.

**Decide by:** Phase 0b, task 13.
