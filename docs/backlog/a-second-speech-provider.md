---
title: A second speech provider
status: deferred
area: ai
---
**Wanted.** A fallback when Groq is unavailable or its terms change.

**Why it is not scheduled.** Groq publishes no SLA, which is a known trade-off from P3-1, accepted for
now. `ISpeechToTextClientFactory` (V2) is the seam a fallback would use, and nothing uses one yet — no
demonstrated need.
