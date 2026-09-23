# Voice capture — design

**Status:** approved by the operator in conversation 2026-09-23; this document awaits their review.
Builds on `2026-09-22-natural-language-capture.md` (Phase 2). This is Phase 3 in that document's
phase table.

## Why this exists

Voice is the operator's main capture path. Phase 2 made the model read amounts and dates the way
people say them, so the only missing piece is turning a Telegram voice note into text. Everything
after that — interpretation, the echo, Cancel · Edit, corrections, the revision history — already
exists and is reused unchanged.

## Decisions

**V1 — Speech-to-text is Groq's hosted `whisper-large-v3`.** Operator's choice, 2026-09-23, after a
comparison of Groq, OpenAI, AWS and Azure (summarised in `docs/OPEN-QUESTIONS.md` P3-1). Why Groq:

- It accepts Telegram's OGG/Opus as uploaded, so there is no audio decoder in this codebase. The file
  must be named `.ogg`: Telegram's own `.oga` name is rejected with 400.
- The free tier (no card; 20 requests/min, 28,800 audio-seconds/day) covers ~30 notes/day about 60
  times over. Paid, it would be about $0.42/month.
- No training on customer data, and requests are not retained by default. Zero data retention is a
  self-serve switch in the Groq console (Data Controls), which the operator turns on — a runbook step.
- The trade-offs: audio is processed in the US; there is no published SLA; and Whisper can invent
  text on silence or noise. The last one is why V5 puts the transcript in the echo.

Not chosen: OpenAI `gpt-transcribe` (does not accept OGG, so it needs an Opus decoder; the rest of
its lineup was deprecated on 2026-08-26), AWS Transcribe (batch needs S3, and content may be used for
training unless an Organizations opt-out policy is set), and Azure AI Speech (fast transcription has
no `ru-RU`). Groq speaks OpenAI's wire protocol, so moving to OpenAI later is a new endpoint, a key and
a decoder — not a redesign.

**V2 — Provider-agnostic like the chat side (D-A).** The transcriber is written against
Microsoft.Extensions.AI's `ISpeechToTextClient`, obtained from an internal
`ISpeechToTextClientFactory`. Only `src/Noof.Ledger.Ai/Groq/` knows Groq: the factory, the stored
secret (`groq-api-key`), the model id, the endpoint, the `.ogg` file name and what Groq's HTTP errors
mean. `AiBoundaryTests` asserts it. `ISpeechToTextClient` is `[Experimental("MEAI001")]`; the warning
is suppressed in `Noof.Ledger.Ai` only, with that reason written next to the suppression.

The Groq `ISpeechToTextClient` is our own small `HttpClient` implementation of
`POST https://api.groq.com/openai/v1/audio/transcriptions` (multipart: `file`, `model`,
`language=ru`, `response_format=json`), not a third-party SDK. Groq ships no .NET SDK, and owning the
request lets tests assert the exact body that goes on the wire. No temperature and no prompt are sent
(prompt hints are Phase 11 calibration).

**V3 — Capture stays durable: store first, transcribe in a job.** When the owner sends a voice note,
the router records the transaction immediately — capture kind `Voice`, the voice's `file_id` and
duration, no text yet — and enqueues a `Transcribe` job in the same database transaction. It replies
`🎤 Transcribing…`. A Groq or network outage delays the record; it never loses it. "Today" is the
voice note's send day, as for text (D2).

**V4 — Transcription hands off to the existing pipeline.** The `Transcribe` job downloads the file
from Telegram by `file_id`, sends it to the transcriber, and in one database transaction writes the
transcript as the record's `raw_text` and enqueues the ordinary `Categorize` job. From there nothing
knows the words were spoken: merchant hints, categorisation, the echo, Cancel · Edit and revisions
run as they do for text. The audio itself is not stored; the `file_id` is enough to fetch it again.

**V5 — The echo shows what was heard.** For a voice capture the echo's first line is
`🎤 "<transcript>"`, so a misheard word is told apart from a misread one at a glance. The transcript is
the operator's own words, shown as-is; the rest of the echo stays English (P2-5).

**V6 — An empty transcript is not a record.** When the transcriber returns only whitespace — silence
or noise — the job ends, no `Categorize` job is created, the transaction is marked `Failed`, and the
acknowledgement becomes `Heard nothing in that voice note.` For a spoken correction (V7), the
record is left exactly as it was and the same line is shown. This is not validation of what the
operator said (P2-1 still holds); there is simply nothing to interpret.

**V7 — Corrections can be spoken too.** A voice note that replies to the echo, or answers the Edit
prompt, goes through a `Transcribe` job whose purpose is *correction*. On success it creates the
ordinary `Correct` job with the transcript as its instruction and the voice reply's own send day as
`instruction_day` (P2-2). Telegram redelivering the same voice reply must not transcribe or correct
twice. Today's unique index on (`transaction_id`, `source_message_id`) would make the follow-up
`Correct` job collide with its own `Transcribe` job, so the key gains `kind`. Editing a voice message in
Telegram can only change its caption, so voice edits are ignored.

**V8 — One job table, two workers.** `JobKind` gains `Transcribe`. A new `TranscriptionWorker` claims
only `Transcribe` jobs; `CategorizationWorker` claims only the others. `IJobQueue.ClaimAsync` takes the
kinds it may claim. The ordering rule is unchanged and spans kinds: no job is claimed while an earlier
job for the same transaction is pending or claimed. Each worker checks that its own provider is
configured before claiming, so a missing Groq key never burns attempts on text captures and a missing
Anthropic key never burns them on transcriptions. Each keeps its own account-level cooldown.

**V9 — Failures follow the chat side's classes.** 401/403 from Groq: account-level; the job is
retried and the worker pauses claiming for a cooldown. 429, 5xx, timeouts and network errors:
transient, retried with the existing backoff up to the attempt cap. 400/413/415: terminal. A
terminal or exhausted `Transcribe` job for a capture marks the transaction `Failed` and edits the
acknowledgement to `Couldn't transcribe that voice note.`; for a correction it leaves the record
untouched and says so, as a failed text correction does today. A failed download from Telegram is
transient.

**V10 — Schema, in one additive migration.**
- `transactions`: `capture_kind` (`Text` = 0, `Voice` = 1, default `Text`), `voice_file_id` (nullable),
  `voice_duration_seconds` (nullable). `raw_text` becomes nullable; a check constraint requires it for
  `Text` captures and requires `voice_file_id` for `Voice` captures.
- `categorization_jobs`: `voice_file_id` (nullable) and a check constraint that `Transcribe` jobs carry
  one. The unique index described in V7 gains `kind`.
- The migration file is converted to a file-scoped namespace, and `noof_ledger_test_template` is
  updated with the runbook command. `noof_ledger` itself is migrated only when the operator runs the
  app.

**V11 — The secret is entered like the others.** The settings page gets a `groq-api-key` field with a
Test button backed by an `ISecretProbe`. The probe calls Groq's `GET /openai/v1/models` only when the
operator presses Test. The key never appears in a log, an exception message or a prompt.

## Testing

- The Groq client is tested against a stub `HttpMessageHandler`. The tests assert the captured request
  (URL, `Authorization`, `model`, `language`, file name `voice.ogg`, and the audio bytes passed through
  unchanged) and the mapping of each status class in V9.
- Router, store, queue and workers are tested with fakes and against clones of the test template.
  Voice fixtures are synthetic byte arrays; nothing decodes audio, so no real voice ever enters the
  repo.
- An opt-in live test transcribes a file the operator points at with `NOOF_LEDGER_LIVE_VOICE_FILE`
  (outside the repo), using `NOOF_LEDGER_LIVE_GROQ_KEY`. It is skipped unless both are set, and it runs
  only with the operator's explicit permission.
- Every new guard is seen failing before it passes.

## Documentation that closes the phase

- `docs/OPEN-QUESTIONS.md`: P3-1, the provider decision, with the comparison and its sources. P3-2, a
  finding from the same research: per Bedrock's documentation (to be checked against Anthropic's own
  before recording), Claude Opus 5.5, Fable 5.1 and Mythos 5.1 reject a forced `tool_choice` with 400.
  The app runs `claude-haiku-4-5`, so nothing breaks today, but Phase 2's forced strict tool call rules
  those models out until the design changes.
- `ops/RUNBOOK.md`: getting a Groq key, entering it, turning on zero data retention.
- `CLAUDE.md` status block and `README.md`.
- `docs/BACKLOG.md`: prompt/keyword hints for the transcriber (Phase 11); storing audio for a
  re-transcription corpus, if the operator ever wants one.

## Out of scope

Voice captions, audio storage, vocabulary hints, a second speech provider, streaming transcription,
and receipt photos (Phase 6).
