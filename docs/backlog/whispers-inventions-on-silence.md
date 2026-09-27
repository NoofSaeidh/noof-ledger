---
title: Whisper's inventions on silence
status: deferred
area: ai
---
**Wanted.** Fewer fabricated transcripts from near-silent or noisy voice notes.

**Why it is not scheduled.** A near-silent note can come back as *«Продолжение следует…»* or
*«Субтитры сделал…»* — Whisper's own hallucination on low-signal audio. It is recorded as whatever the
model makes of it, and the `🎤 "…"` line shows it as-is. P2-1 forbids a filter on what the transcript
says. If it happens often in practice, the answer is a decision (for example, Groq's `verbose_json`
`no_speech_prob`), not a quiet guard added later.
