---
title: Keeping the audio
status: deferred
area: other
---
**Wanted.** A re-transcription corpus, to compare providers or Whisper versions on real voice notes
later.

**Why it is not scheduled.** Nothing stores the voice note itself. The `file_id` can fetch it again
while Telegram keeps it, which is enough for the current pipeline. A re-transcription corpus for Phase
11 would need the bytes in the database, which would then be in every backup. That is a privacy
decision for the operator, not a default to reach for.
