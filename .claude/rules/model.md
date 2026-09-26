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
- Forced tool use is unsupported on Claude Opus 5.5, Fable 5.1 and Mythos 5.1 — `docs/OPEN-QUESTIONS.md` P3-2.
- **Amounts are JSON numbers read straight into `decimal`** — from the argument's `JsonElement`,
  never via `double`.
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
