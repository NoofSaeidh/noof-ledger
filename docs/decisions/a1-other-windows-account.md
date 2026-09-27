---
id: A1
title: Does anyone else have a Windows account on this PC?
status: superseded
superseded_by: cookie-auth-only-mode
---

**Default taken:** No.

**Decide by:** Any time — a yes means flipping `Auth:Mode=Cookie` immediately, since that is the
single scenario where auth is load-bearing today.

Superseded 2026-09-22 — see `cookie-auth-only-mode.md`: `Auth:Mode` is deleted and cookie
authentication is the only mode, so this question no longer selects between two real code paths.
