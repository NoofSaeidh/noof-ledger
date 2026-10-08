---
paths:
  - "src/Noof.Ledger.Ai/**"
  - "tests/Noof.Ledger.Ai.Tests/**"
  - "src/Noof.Ledger.Application/Categorization/**"
  - "src/Noof.Ledger.Persistence/Categorization/**"
  - "src/Noof.Ledger.Host/Workers/CategorizationWorker*.cs"
---

## CLAUDE.md §4 — The model *(settled)*

- Reached through **`Microsoft.Extensions.AI`'s `IChatClient`** — the Anthropic factory calls
  `AsIChatClient(options.Model, options.MaxTokens)` on the SDK client — not the SDK's native
  `Messages.Create`. Operator's decision.
- **The answer is a forced tool call with `strict: true`, not structured outputs** *(settled
  2026-09-23, operator's preference)*. `record_transaction`'s arguments are the answer (renamed from
  `record_spending` in Phase 4: it now records income and balance statements too, and names a `kind`,
  an optional `wallet_id`, and — for `kind = "balance"` — the stated `balance_amount`); strictness is a
  provider-neutral marker (`StrictTool.Marker()`), translated to the wire's own `"Strict"` key inside
  `Noof.Ledger.Ai/Anthropic/`, and `ChatToolMode.RequireAny`/`RequireSpecific` becomes `tool_choice`.
  Assert both on the captured HTTP body, not from documentation. (Phase 1B had used
  `output_config.format`; that was an agent's choice, not the operator's.)
- Forced tool use is unsupported on Claude Opus 5.5, Fable 5.1 and Mythos 5.1 — `docs/decisions/p3-2-forced-tool-use-model-restrictions.md`.
- **Amounts are JSON numbers read straight into `decimal`** — from the argument's `JsonElement`,
  never via `double`.
- **The model never does arithmetic on money** *(Phase 7, T-12)*. It copies amounts, rates and fees
  as the operator said them — `record_transaction`'s `transfer` and `charged` — and C# converts,
  adds a fee to a leg or takes it out, and prices a charge (`TransferRequest.TrySettle`,
  `ChargeTerms` in Domain). A new money figure derived from what the model read is computed in C#,
  never asked of the model.
- **Never set temperature.** It is `[Obsolete]` in the SDK and therefore a compile error here.
  Determinism comes from the schema's enums.
- **Nothing depends on an AI provider except its factory**: `IChatClientFactory` for the model and
  `ISpeechToTextClientFactory` for speech, each implemented in its own folder under
  `src/Noof.Ledger.Ai/<Provider>/` *(D-A, 2026-09-23; speech P3-1)*. Asserted by `AiBoundaryTests`.
  `ISpeechToTextClient` is experimental (`MEAI001`), and the warning is suppressed in
  `Noof.Ledger.Ai` and its tests only.
- **The tool loop runs through `FunctionInvokingChatClient`** *(D-B)*, with a guard
  `DelegatingChatClient` below it that re-forces `record_transaction` on the follow-up request: FICC
  resets a required `ToolMode` after the first round and strips every tool declaration on its own
  last iteration — verified by decompiling, not by its docs.
- **`record_transaction` is not the only forced answer tool** *(Phase 6)*. `ChatReceiptVision`'s vision
  fallback forces `read_receipt`, `ChatReceiptCategorizer` forces `categorize_receipt`, and
  `ChatFindingExplainer` forces `write_explanation` (Phase 8a); all three use
  `ChatToolMode.RequireSpecific` for a single round with no `FunctionInvokingChatClient` loop, unlike
  `record_transaction`'s multi-round `ChatCategorizer`/`DelegatingChatClient` path above.
- **The model explains, never decides** *(Phase 8a, IR-6)*. `write_explanation`'s `looks_like_bug` only
  shows or hides an offer — *Create bug report* on the integrity page, *Close report* in the bot. Nothing the
  model says changes a finding, a report's status or a health level, and every figure it is given is computed
  and formatted by C#. An integrity or health check never reaches the model (`HealthCheckBoundaryTests`).
