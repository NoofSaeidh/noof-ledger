---
title: Vocabulary hints for the transcriber
status: deferred
area: ai
---
**Wanted.** Fewer misheard merchant names in a voice transcript.

**Why it is not scheduled.** Groq takes a `prompt` of up to 224 tokens. Merchant names from the
directory would help it spell *Maxi*, *Lidl* and *Wolt*. That sends a slice of the shopping profile to
Groq, the same trade as Q7 (`docs/OPEN-QUESTIONS.md`). It belongs to Phase 11 calibration, once there
is a real-voice corpus to judge it against.
