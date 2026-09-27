---
title: "`Auth:Mode=Off` is deleted; cookie authentication is the only mode"
status: decided
date: 2026-09-22
supersedes: [a1-other-windows-account, a2-remote-dashboard-access]
---

Found while auditing the two-mode setup: `/settings/secrets` disables prerendering and declares
`[Authorize]`, and `Routes.razor`'s `AuthorizeRouteView` has no `<NotAuthorized>` content, so under
Off mode — where every request authenticates automatically with no credential check — the page
rendered **blank** rather than showing anything, authorized or not. Nobody had caught it because
every E2E page test ran in Cookie mode and the only Off-mode fixture pointed at a deliberately dead
database.

The operator's instruction was not to fix Off mode but to delete it: *"Проще - лучше."* Cookie
authentication is now the sole scheme `Program.cs` registers, so A1 and A2's defaults no longer
select between two real code paths — there is only one. The loopback interlock (`LoopbackGuard`,
A2's enforcement mechanism) is unaffected in spirit and strengthened in fact: it no longer reads an
auth mode at all and refuses any non-loopback binding unconditionally, which is what "the config key
and the Kestrel binding are physically inseparable" now means literally rather than only while a
particular mode is selected. The rows above and the note below are kept for the record; they were
correct descriptions of the two-mode design while it existed.

## Note (Phase 0b, raised by the auth/data research)

This paragraph described `Auth:Mode` defaulting to `Off` so the login screen stayed out of the way.
That key no longer exists: cookie authentication is the only mode, and the loopback interlock now
refuses a non-loopback bind unconditionally rather than only while auth was off. Kept for the record
of why the toggle seemed worth having.
