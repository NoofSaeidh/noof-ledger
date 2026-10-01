# Reviews

How to run each review `CLAUDE.md` §1 names. The policy — who reviews what, when, and how findings
are triaged — stays in §1; this file is the procedure *(operator's decisions, 2026-09-28)*.

Why other model families review: models in one family share blind spots, so a reviewer from the
implementer's family tends to miss exactly what the implementer missed, and agreement between them is
weak evidence. A different family is the cheapest independence available.

## Per task — opus

Inside a phase or plan, each task's review runs on opus (effort medium). No Codex, no Fable per task.

## Per pull request — Codex

One Codex review per PR, run from the PR branch with the Codex CLI. The plugin's
`/codex:review`/`/codex:adversarial-review` slash commands cannot be invoked by an agent. Never enable
the plugin's stop-time review gate: it would review on every Stop and burn the quota.

- **Plain**, for a mechanical PR:

  ```
  codex review --base <base-branch> -c model_reasoning_effort="medium"
  ```

  The CLI rejects a PROMPT combined with `--base`, so this form takes no prompt.
- **Adversarial**, for a PR that makes design choices — and the default when unsure. Use the plugin's
  companion script, which supports both a base branch and a focus prompt:

  ```
  node <path> adversarial-review --wait --base <base-branch> "<focus>"
  ```

  The focus asks it to challenge the approach, assumptions, trade-offs and failure modes — not just
  defects. Resolve `<path>` with Glob on
  `~/.claude/plugins/cache/openai-codex/codex/*/scripts/codex-companion.mjs`; the version directory
  changes on update, so never hard-code it.
- Run either in the foreground with a 600000 ms timeout.
- Findings are judged, not obeyed (triage per §1): fix what's confirmed, reply in the PR description
  to what's rejected and why.
- Codex refuses on its usage limit → don't wait for the window; fall back to an opus review and say
  so in the PR description.

## Per phase — Fable 5.1, twice

Agent tool with `model: "fable"`. Never per PR, never per fix round or Copilot round — a Fable review
per fix round cost ~5 hours on PR #3.

- **At planning**, once, after the phase's spec and plan are written and before implementation
  starts. It reviews the decision, not code: alternatives considered, risks and failure modes,
  conflicts with *(settled)* rules and `docs/decisions/`, and whether the PR cut is right. Its
  findings are triaged like any review.
- **At the end**, closing the phase or a batch of PRs.

### The Codex/Fable trial — 3 phases *(operator, 2026-09-28)*

Both Fable reviews run in parallel with a Codex adversarial review of the same scope:

- **Closing:** the companion script above with `--base <base>`.
- **Planning:**

  ```
  codex exec -s read-only "<prompt naming the spec and plan paths, even under the ignored .superpowers/, asking for an adversarial review of the decision>"
  ```

An opus pass merges both lists — deduped, each finding tagged *both* / *Fable only* / *Codex only* —
then triages them. Each review adds one tally line to [the tally below](#trial-tally) (confirmed
findings by tag). After the third phase the operator keeps both reviewers or drops one.

## Copilot

Optional — its quota runs out, so CI and the per-PR Codex review are the gate and nothing waits for
Copilot (`.\run.ps1 pr-wait` waits for it only when it is actually requested on the PR). When a
Copilot review does arrive:

- **Fix rounds:** handle every comment in one round — one commit, an opus-only review (effort
  medium), push. At most 2 rounds per PR; then list what's left for the operator.
- No Codex or Fable for a Copilot round unless it touches money, secrets, a migration or the public
  surface and a stronger review is judged necessary — say why in the PR.
- Every reply and resolve goes through `.\ops\gh-bot.ps1` (§5).

## Resolving review threads

Resolve only the threads you replied to or fixed, by id — never "resolve all unresolved", which hides
comments that arrived after you looked.

```
# list threads with their first comment
gh api graphql -F n=<pr> -f query='query($n:Int!){repository(owner:"NoofSaeidh",name:"noof-ledger"){pullRequest(number:$n){reviewThreads(first:100){nodes{id isResolved comments(first:1){nodes{databaseId body}}}}}}}'

# reply to one review comment
.\ops\gh-bot.ps1 api repos/NoofSaeidh/noof-ledger/pulls/<pr>/comments/<comment databaseId>/replies -f body='...'

# resolve exactly that thread
.\ops\gh-bot.ps1 api graphql -f id=<thread id> -f query='mutation($id:ID!){resolveReviewThread(input:{threadId:$id}){thread{isResolved}}}'
```

## Trial tally

One line per review: confirmed findings by tag. Dated history, not status.

- **Batch after Phase 6 (`801e283..843a777`), closing review, 2026-09-28:** 1 by both (hook quoted
  paths); 2 Codex only (pr-wait CI gating [high], template freshness coverage); 8 Fable only (CI
  compile gap, dangling refs, CLAUDE.md length, PR-size ambiguity, pr-wait timing, env-var test
  isolation, stale statements, hook tests unrun).
