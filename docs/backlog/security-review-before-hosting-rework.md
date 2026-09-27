---
title: Security review before the hosting rework
status: deferred
area: security
since: 2026-09-28
---
**Wanted.** Before the hosting rework exposes the app beyond localhost, run Claude Code's
`/security-review` and a Codex adversarial review (`/codex:adversarial-review`) on that PR.

**Why it is not scheduled now.** Nothing is reachable from outside localhost yet
(`LoopbackGuard` refuses a non-loopback bind unconditionally), so there is nothing to review for
that specifically today. The review belongs on the PR that actually changes that — reviewing code
that cannot yet be reached from outside the machine tests nothing a later, real review would not
have to repeat anyway.

**Why the general-purpose `security-guidance` plugin is not the answer.** It was uninstalled on
2026-09-28: its pattern rules cover JS/Python/Go only — none for C# — so it produced 0 warnings and
88 "no vulnerabilities" LLM reviews in two days, hit the account's rate limit 18 times, and its
per-tool-call hook slowed every Bash/Edit call. It was pure overhead for this codebase.

**What to decide then.** Whether a C#-aware scanner is worth adding, once there is an actual
network-facing surface to point one at.
