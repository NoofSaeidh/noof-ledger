---
title: Choosing the default currency from Telegram
status: deferred
area: telegram
since: 2026-09-22
---
**Status:** deferred by the operator, 2026-09-22. A single hard default (`RSD`) ships instead.

A message that states no currency — `кофе 250` — has to become money in some currency. Until this
lands, that is always `CategorizationWorkerOptions.DefaultCurrency`, which is `RSD`.

**What was actually wrong before the default existed**, and why this is not a nice-to-have: `currency`
was a `required` property in the response schema, so constrained decoding *forced* the model to emit
one of the five codes whether or not the message said anything. The model had no way to say "not
stated" and no way to be right except by luck. The amount was verified against the raw text and the
currency beside it was a guess — with a hundredfold consequence between RSD and EUR. Making the
property optional and substituting a known default is what removed the guess; the Telegram command
below only makes the default the operator's to choose.

**What to build.** A bot command — `/currency eur`, or a one-tap keyboard — that sets the default for
every later message that omits one. It should:

- store the choice, not hold it in configuration, so it survives a restart and is visible on the
  settings page next to the other operator-owned values;
- apply only to messages captured *after* the change, never retroactively — a transaction already
  recorded in RSD was recorded in RSD, and re-interpreting history on a setting change is the same
  class of mistake as bucketing a day by the current time zone rather than the row's (decision P1-3);
- confirm the change in the chat, so the operator can see it took effect without opening the dashboard.

**Where the default lives today, and where it should move.** `CategorizationWorkerOptions.DefaultCurrency`,
bound from the `Categorization` configuration section. When this item is built it becomes a stored
value the bot command writes and the worker reads per job, and the configuration key should be removed
rather than left as a second source of truth that silently disagrees.

**The alternative not taken, recorded so it is not re-proposed as new.** The default could have been
derived from the wallet the capture lands in — wallets already carry a currency, and Phase 2 gives
every account one wallet per currency. It was not taken because the operator asked for a chosen
default rather than an inferred one, and because a wallet-derived default cannot express "I am
travelling, price things in EUR for now" without moving the whole capture to a different wallet.
Worth revisiting when Phase 2 makes multi-wallet capture real.
