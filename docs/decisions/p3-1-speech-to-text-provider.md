---
id: P3-1
title: "Speech-to-text provider (2026-09-23)"
status: decided
date: 2026-09-23
---

**Decision: Groq's hosted `whisper-large-v3`** — the operator's call, *"Давай грок"*, after a comparison of four options researched and adversarially fact-checked against vendor pages on 2026-09-23.

| Option | Why not (or why) |
|---|---|
| **Groq `whisper-large-v3`** (chosen) | Accepts Telegram's OGG/Opus as uploaded (name it `.ogg`; `.oga` is refused). Free tier without a card: 20 requests/min, 28,800 audio-seconds/day — ~60× this volume; paid ≈ $0.42/month. No training on customer data; not retained by default; zero data retention is a self-serve console switch. US-hosted; no SLA. |
| OpenAI `gpt-transcribe` | ≈ $1/month, no retention on the transcription endpoint, no training. Does not accept OGG — needs an Opus decoder in C#. Its older transcription models were deprecated 2026-08-26 (removal 2027-02-26). The operator's first preference, set aside because Groq needs neither the decoder nor a paid balance. |
| AWS (Transcribe + Claude on Bedrock) | Batch transcription requires S3 and polling; Transcribe may keep audio to improve its models unless an Organizations opt-out policy is set; strict tool use on Bedrock is undocumented and reported broken for Sonnet 5. |
| Azure (AI Speech / OpenAI + Claude in Foundry) | Fast transcription has no `ru-RU`; Claude in Foundry has no EU data zone; billing through Azure Marketplace. |
| Local Whisper on the operator's GPU | Private and free, but Vulkan on the RX 7900 XT under Windows was unverified, and a 1.6 GB model and native runtime to ship. Not chosen; the seam keeps it possible. |

**What keeps this cheap to reverse:** transcription goes through `ISpeechToTextClient` from a factory that only `src/Noof.Ledger.Ai/Groq/` implements (V2). OpenAI speaks the same wire protocol; moving there is a new folder, a key and a decoder. Real-voice comparison belongs to Phase 11 calibration.
