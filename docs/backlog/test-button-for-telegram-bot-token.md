---
title: A Test button for the Telegram bot token
status: deferred
area: telegram
---
**Wanted.** The settings page's Anthropic key gets a Test button this phase (`ISecretProbe` /
`AnthropicKeyProbe`, `GET /v1/models`, costs no tokens). The Telegram bot token has no equivalent -
the spec (§9) names `getMe` for exactly this, and today the only way to learn a pasted Telegram
token is bad is to watch the poller silently fail to start.

**Why it is not scheduled.** Out of this phase's stated scope (Task 8 covers only the Anthropic
key's Test button). `ISecretProbe` already exists as a port after this phase; a `TelegramKeyProbe`
implementing it against `getMe` is a small, isolated addition with no schema or contract cost -
ordinary work for any later phase.
