---
title: Revalidating authentication state for long-lived circuits
status: deferred
area: security
---
**Wanted.** A circuit that stops serving a user whose cookie was revoked or expired.

**Why it matters.** Blazor Server holds an authenticated `ClaimsPrincipal` for the life of the SignalR
circuit. `AddCascadingAuthenticationState` alone does not revalidate it, and there is no custom
`AuthenticationStateProvider` anywhere in this repository. The combination that makes this concrete is
`[Authorize]` plus `@rendermode InteractiveServer` — which is exactly the settings page that handles
secrets.

**Status.** Flagged by the Phase 1 readiness audit and never investigated. Belongs with the hardening
phase recorded in `OPEN-QUESTIONS.md`. Cookie authentication is the only mode now
(`docs/OPEN-QUESTIONS.md`, A1/A2 SUPERSEDED), so this is no longer conditional on a mode switch — it
applies today.
