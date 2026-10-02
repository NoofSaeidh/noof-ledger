---
title: A reply racing a slip save by milliseconds can still lose to the caption
status: deferred
area: persistence
since: Phase 7
---
**Wanted.** A reply to the bot's message always wins over an exchange slip photo's caption, even when
the two land at the same moment.

**What holds today.** A reply sent while the photo is still being read, or before "Record anyway" is
pressed on a held slip, wins. `EfReceiptStore.SaveExchangeSlipAsync` skips the caption's job, and
`EnqueueCategorizationAsync` refuses "Record anyway", while a reply's job is pending or claimed for
the transaction. Both check under the transaction's row lock.

**The window that remains.** Replies are queued without that row lock: `EfRecordEditor.TryQueueAsync`
(typed and voice replies) and `EfTranscriptionStore.CompleteCorrectionAsync` (a voice reply once
transcribed) only insert. The race takes three steps:

1. The reply's job takes its `created_at`.
2. The slip save or "Record anyway" reads the queue before the reply commits, so it does not see the
   reply.
3. The reply then commits with the earlier `created_at`.

The queue claims a transaction's jobs in `created_at` order. The reply applies first, and the caption
queued after it re-applies the older figures over the reply's. The window is the milliseconds
between the reply handler stamping its job and committing it. The echo shows the result, and another
reply corrects it (P2-1).

**To close it.** Take the transaction's row lock (`SELECT … FOR UPDATE`, as `EfRecordEditor.LockAsync`
does) in the reply paths of `EfRecordEditor` and `EfTranscriptionStore`, and stamp `created_at` under
it. A reply is then ordered strictly before or after a slip save or a "Record anyway" press, never
interleaved with one.
