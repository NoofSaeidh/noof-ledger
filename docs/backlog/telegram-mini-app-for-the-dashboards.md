---
title: A Telegram Mini App for the dashboards
status: deferred
area: telegram
since: 2026-10-03
related: [reaching-dashboard-from-phone-away-from-home, hosting-the-whole-application, security-review-before-hosting-rework]
---
**Wanted.** Every dashboard viewable inside Telegram, as a Mini App opened from the bot — the same
place the operator already captures spending.

**What it depends on.** Telegram loads a Mini App from a public HTTPS URL, and the app is
loopback-only today (`LoopbackGuard` refuses any other bind). So this is one answer to
`reaching-dashboard-from-phone-away-from-home`, and needs what that one needs: the app, or the pages
the Mini App shows, reachable from outside — hosting or a tunnel — and the security review that goes
with exposing it. Sign-in would come from Telegram itself: the Mini App's `initData` is signed with
the bot token and names the Telegram user, which the bot already checks against the operator's id.
