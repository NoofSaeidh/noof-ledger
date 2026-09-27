---
title: Loose ends from the Phase 3 closing review
status: deferred
area: web
since: 2026-09-24
---
Recorded 2026-09-24 by the Phase 3 closing review (Fable 5.1, a different model family from the one
that wrote the code). Both are real, cheap-to-verify UX gaps rather than proven defects; neither blocks
the phase.

**The dashboard names the wrong stage for a voice record awaiting or missing its transcript.**
`RecentTransaction` (`src/Noof.Ledger.Application/Reporting/ISpendingReadModel.cs`) carries no capture
kind, so `Home.razor` cannot tell a still-transcribing voice note from a captured text message: a voice
note with no transcript yet shows an empty description under "Awaiting categorisation - this message
has not been read by the categoriser yet" (`src/Noof.Ledger.Web/Components/Pages/Home.razor:111-113`),
and a "Heard nothing" record (`RecordEcho.HeardNothing`,
`src/Noof.Ledger.Application/Chat/RecordEcho.cs:19`) shows "Categorisation failed. Nothing was recorded
for this message." (`Home.razor:115-119`) — a `Failed` transaction status, which this is not; the audio
was heard, it just held no speech. Fix later by carrying `CaptureKind` (and, for the second case, the
distinction between "never categorised" and "heard nothing") into `RecentTransaction`, and phrasing the
two states for what actually happened.

**A spoken correction's own transcript is never shown.** `RecordEcho.WithWhatWasHeard`
(`src/Noof.Ledger.Application/Chat/RecordEcho.cs:54-57`) prepends `🎤 "{record.RawText}"` from the
transaction's original transcript only — `RawText` is written once, by `CompleteCaptureAsync`'s
`IS NULL` guard, and never again. A correction spoken as a reply stores its own transcript in
`CategorizationJob.Instruction`, which the final echo never surfaces: after a spoken correction, the
echo shows the *original* 🎤 line and the corrected body, but not what the correction itself was heard
as — exactly the ambiguity V5 was built to remove, just not for this path. A fix would prepend
🎤 "<instruction>" when the latest revision is a spoken correction; deferred because it needs a small
spec decision from the operator (which revision's transcript to show, and how it composes with the
original 🎤 line already shown above the body).
