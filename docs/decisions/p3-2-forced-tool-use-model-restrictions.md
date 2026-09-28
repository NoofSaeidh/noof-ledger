---
id: P3-2
title: "Forced tool use rules out some Claude models (found 2026-09-23)"
status: decided
date: 2026-09-23
---

AWS's Bedrock documentation states, under "Forced tool use"
(`docs.aws.amazon.com/bedrock/latest/userguide/model-parameters-anthropic-claude-messages-tool-use.html`):
*"Claude Opus 5.5, Claude Fable 5.1, and Claude Mythos 5.1 do not support forced tool use. A request
that sets `tool_choice` to `{"type": "any"}` or `{"type": "tool", "name": "..."}` returns a `400
invalid_request_error`."* Anthropic's own documentation confirms the same restriction independently,
not merely by Bedrock's word for it:
`platform.claude.com/docs/en/agents-and-tools/tool-use/implement-tool-use` ("Forcing tool use"
section) carries a table naming the identical three models — *"Claude Opus 5.5, Claude Fable 5.1, and
Claude Mythos 5.1"* — with the restriction *"`any` and `tool` return a 400 error"* and the recommended
replacement *"`auto` with strict tool use ... or structured outputs."* The app runs
`claude-haiku-4-5`, so nothing breaks today, but Phase 2's forced strict tool call (`tool_choice`
any/tool, `docs/specs/2026-09-19-noof-finance-design.md` and `.claude/rules/model.md`)
cannot run on Claude Opus 5.5, Fable 5.1 or Mythos 5.1. Moving to one of them is a design change to the answer
contract — `auto` plus strict tools, or structured outputs — not a model-id change.
