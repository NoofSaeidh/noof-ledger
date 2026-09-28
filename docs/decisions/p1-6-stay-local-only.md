---
title: "Nothing is built. The application stays local"
status: decided
date: 2026-09-21
supersedes: [p1-6-capture-relay-decision]
---

The relay described in `p1-6-capture-relay-decision.md` is **not being built**. Neither is hosting, and
neither is the userbot. The operator's decision after the price re-check: *"оставляем пока только
локально."*

The reasoning there is kept because it is correct and was expensive to establish — but its conclusion
was overturned by one fact the first survey missed, found only when the prices were challenged:

> **A tunnel (Cloudflare Tunnel or Tailscale) gives phone access to the dashboard with the application
> staying at home, for nothing, with no inbound port open.**

That collapses the case for hosting. Hosting was worth considering because it bought three things at
once — always-on capture, remote dashboard access, and no third-party queue. Remote access is now free
and separable. Always-on capture is addressed more cheaply by waking the machine on a schedule. And the
third-party queue is a cost of the relay, not a benefit of hosting.

So none of the three remaining options is worth its price today:

| Option | Why not now |
|---|---|
| Relay (Lambda + DynamoDB, $0) | 2–3 days, the project's first external dependency, raw expense text in AWS — to solve a problem a free OS setting may already solve |
| Whole-app hosting (€5–6/mo) | Every stored secret re-entered (DPAPI does not travel), a 2–4 day Linux port, and a public-repo finance app on the open internet |
| MTProto userbot | Rejected on its own terms — see the decision above |

**What remains true and unchanged:** the 24-hour limitation recorded in **P1-5** is still live. A machine
off for longer than a day still loses the messages sent in that window. That is now an accepted,
documented limitation rather than a problem being solved — and the mitigations are in `docs/backlog/` (four options, starting with
`docs/backlog/wake-the-machine-on-a-schedule.md`),
ready to pick up when the product is actually in daily use and the gap is felt rather than imagined.

**Order of work is unaffected:** Phase 1B was always first, and remains so.
