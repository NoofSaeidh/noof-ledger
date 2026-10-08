---
title: A bug report by voice note
status: deferred
area: telegram
since: 2026-10-02 (Phase 8a)
---
**What.** `/bug` is text only. A voice note that starts "bug …" is transcribed and captured as spending
like any other voice note.

**What it would take.** Recognising the command in a transcription before capture — which means the
transcription result, not the Telegram message, decides the route, unlike every other command.
