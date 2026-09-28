---
id: A2
title: Will you reach the dashboard from your phone, and when?
status: superseded
superseded_by: cookie-auth-only-mode
---

**Default taken:** Not yet.

**Decide by:** Enforced automatically: the startup guard refuses to boot on a non-loopback binding
while `Auth:Mode=Off`.

**Why A2 needs no discipline from you:** the guard makes the config key and the Kestrel binding
physically inseparable. The day you widen the binding for phone access, the app will not start
until auth is on. That is what stops "optional now" from becoming "forgotten forever".

Superseded 2026-09-22 — see `cookie-auth-only-mode.md`: `Auth:Mode` is deleted, so this no longer
selects between two real code paths, but the loopback interlock itself is unaffected in spirit and
strengthened in fact — it no longer reads an auth mode at all and refuses any non-loopback binding
unconditionally.
