---
id: P1-5
title: Telegram drops unfetched messages after 24 hours, and a bot cannot read history
status: open
date: 2026-09-21
---

**Not a decision. A verified constraint of the platform**, recorded because the design's own framing
("the PC was off") runs straight into it and nothing said so.

Checked against `https://core.telegram.org/bots/api` on 2026-09-21, not from memory:

> Incoming updates are stored on the server until the bot receives them either way, but they will not
> be kept longer than **24 hours**.

And the more consequential half: **the Bot API has no method for fetching past messages.** There is no
`getChatHistory`, no `getMessages`, nothing equivalent. A bot receives messages only going forward, via
`getUpdates` or a webhook. So "it is just a chat, the service reads the new messages each time it runs"
does not hold — not because of how this is implemented, but because bots cannot look backwards at all.

**What it costs, concretely.** The spec names "your PC was off" as the common case. Off for under a day:
nothing is lost, the poller drains the backlog on the next start. Off for longer — a weekend away, a
holiday — and every message sent outside the last 24 hours is **gone**, with no way to recover it. The
same window is what makes a permanently stalled poller a data-loss bug rather than an availability one,
which is why `TelegramPollingService` now gives up on a poisoned update after three attempts, tells the
operator in the chat, and moves on.

**The only thing that would change this** is a client on MTProto under the operator's own Telegram
account rather than a bot token: a user client *can* read history, so a service could reconcile whatever
it missed on startup. The price is that it holds full account credentials instead of a token that can
only write to one chat — a materially different secret to be storing, in a project whose repository is
public. **Undecided, and deliberately not decided here.** Revisit if the "off for a week" case turns out
to matter in practice; until then the honest statement is the one in the README, not "nothing is lost".
