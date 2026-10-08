---
title: A bot claimed after start-up gets its command menu only at the next restart
status: deferred
area: telegram
since: 2026-10-02 (Phase 8a, P-16)
---
**What.** `TelegramPollingService` registers the bot's commands (`/health`, `/bug`) for the owner's chat
with `setMyCommands` only when the token is new and an owner already exists. A bot whose owner claims it
later shows no command menu in Telegram until the host restarts or the token changes. The commands still
work when typed.

**What fixing it would take.** Register the commands when the owner gate first records an owner, as well
as on a new token.
