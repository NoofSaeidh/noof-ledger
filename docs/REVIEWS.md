# Reviews

How to run each review `CLAUDE.md` §1 names. The policy — who reviews what, when, and how findings
are triaged — stays in §1; this file is the procedure *(operator's decisions, 2026-09-28)*.

Why other model families review: models in one family share blind spots, so a reviewer from the
implementer's family tends to miss exactly what the implementer missed, and agreement between them is
weak evidence. A different family is the cheapest independence available.

## Per task — opus

Inside a phase or plan, each task's review runs on opus (effort medium). No Codex per task.

## Per pull request — Codex

One Codex review per PR, run from the PR branch with the Codex CLI. The plugin's
`/codex:review`/`/codex:adversarial-review` slash commands cannot be invoked by an agent. Never enable
the plugin's stop-time review gate: it would review on every Stop and burn the quota.

- **Plain**, for a mechanical PR:

  ```
  codex review --base <base-branch> -c model_reasoning_effort="medium"
  ```

  The CLI rejects a PROMPT combined with `--base`, so this form takes no prompt.
- **Adversarial**, for a PR that makes design choices — and the default when unsure. Run it from the
  PR's worktree through the wrapper around the plugin's companion script, which takes both a base
  branch and a focus prompt:

  ```
  .\run.ps1 codex review -Base <base-branch> -Focus "<focus>"
  ```

  The focus asks it to challenge the approach, assumptions, trade-offs and failure modes — not just
  defects. Never call `codex-companion.mjs` directly: it leaves a broker process running in the
  worktree until reboot, and the worktree then cannot be removed. The wrapper stops it afterwards;
  `.\run.ps1 codex sweep -Stop` clears any a direct call left behind. One review per worktree at a
  time — two would share the broker (`ops/RUNBOOK.md`).
- Run either in the foreground with a 600000 ms timeout.
- Findings are judged, not obeyed (triage per §1): fix what's confirmed, reply in the PR description
  to what's rejected and why.
- Codex refuses on its usage limit → don't wait for the window; fall back to an opus review and say
  so in the PR description.

## Per phase — Codex adversarial, three times

A Codex adversarial review *(operator, 2026-10-08)*. It differs from the per-PR review in scope: the
whole spec, plan or phase rather than one PR. Never per PR, never per fix round or Copilot round — a
phase-point review per fix round cost ~5 hours on PR #3.

- **The spec**, once it is approved and before the plan is written. It reviews the decision, not
  code: alternatives considered, risks and failure modes, conflicts with *(settled)* rules and
  `docs/decisions/`, and whether the PR cut is right. Findings are triaged like any review; accepted
  ones amend the spec.
- **The implementation plan** *(operator, 2026-10-01)*, once it is written and before any
  implementation starts. It reviews the plan as something an engineer will execute task by task:
  money correctness against the code as it is, consistency across the plan's files and PRs (names,
  signatures, strings, dependencies, two PRs of one wave editing the same code), *(settled)* rules,
  tests that could not go red, and the failure modes a real user would hit that no task tests. It
  does not re-review the spec's decisions. Accepted findings are folded into the plan by the agents
  that wrote it, and into the spec as amendments where they change a decision. Phase 7's plan review
  found a fee charged twice, terms stored ×100 and three races that un-booked records — none of them
  visible in the spec.
- **At the end**, closing the phase or a batch of PRs.

How to run it:

- **Spec and plan:** one prompt naming the files — the spec, and for the plan review every plan file
  even under the ignored `.superpowers/` — and the adversarial focus above:

  ```
  codex exec -s read-only "<that prompt>"
  ```

  Run it in the background with a long timeout; a plan review reads tens of thousands of lines and
  takes a while. Its findings are the text after the last `tokens used` line of the output.
- **Closing:** `git fetch origin` first, then `.\run.ps1 codex review -Base origin/master` (or the
  batch's base) from the aggregate branch's worktree. Never a local `master`: a stale one widens the
  range — at Phase 8a's close it pulled all of Phase 7 into the review.
- Codex at its usage limit → an opus review stands in, said so where the findings are recorded.
- An opus pass triages the findings per §1.

### The Codex/Fable trial — closed *(operator, 2026-09-28 to 2026-10-08)*

For three phases a Fable 5.1 review ran in parallel with the Codex adversarial review at each phase
point, both given the same prompt; an opus pass merged the lists, tagged each finding *both* /
*Fable only* / *Codex only*, and added a line to [the tally below](#trial-tally). After the third
phase the operator dropped Fable *(2026-10-08)*: the real defects came from Codex — at Phase 8a's
close Fable found 0 critical and 0 major.

## Copilot

Optional — its quota runs out, so CI and the per-PR Codex review are the gate and nothing waits for
Copilot (`.\run.ps1 pr-wait` waits for it only when it is actually requested on the PR). When a
Copilot review does arrive:

- **Fix rounds:** handle every comment in one round — one commit, an opus-only review (effort
  medium), push. At most 2 rounds per PR; then list what's left for the operator.
- No Codex for a Copilot round unless it touches money, secrets, a migration or the public
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

One line per review of the closed trial: confirmed findings by tag. Dated history, not status.

- **Batch after Phase 6 (`801e283..843a777`), closing review, 2026-09-28:** 1 by both (hook quoted
  paths); 2 Codex only (pr-wait CI gating [high], template freshness coverage); 8 Fable only (CI
  compile gap, dangling refs, CLAUDE.md length, PR-size ambiguity, pr-wait timing, env-var test
  isolation, stale statements, hook tests unrun).
- **Phase 7 planning, spec `2026-10-01-transfers-and-exchange-design.md`, 2026-10-01:** 8 by both
  (gross vs net leg amounts and the fee's wallet, no recording path for a slip, slip duplicates
  undetected, the failure reason lost and a failed correction un-booking a record, fee lines fed back
  to the model on a correction, wallets across kind changes, rate edge cases, the PR cut); 3 Codex
  only (a correction repricing at today's terms, principal lines surviving a change to Transfer,
  acceptance wording); 5 Fable only (the one-wallet Cash default, a leg in a foreign currency, framing
  the slip rule as fiscal-only, the negative-balance hint on credit wallets, old snapshots on the trace
  page). Rejected: a pair-level default rate (Fable; the operator chose per wallet), gating a
  capability until its echo (Codex; the aggregate branch ships whole), foreign spending on a fiscal
  receipt (Codex; the operator has no such case).
- **Phase 7 planning, implementation plan (`.superpowers/p7-plan/`), 2026-10-01:** 3 by both (the crossing-zero
  line under a later checkpoint, 1c and 7b rewriting one method in one wave, a stated rate lost or ignored on a
  correction); 6 Codex only (a reply queued during slip extraction overwritten by the first recording, Restore of a
  record corrected while cancelled, failure writes racing a correction or Cancel, a said charge in another currency
  keeping an older stated one, a slip's date lost on completion by reply, the slip correction text omitting that a
  commission is included); 11 Fable only, plus 13 minor (a date-only correction charging a transfer's fee twice,
  `/wallets` reading `117,35` as 11735, an unnamed cash leg failing as `SameWallet`, a dinar fee rejected on a
  EUR → RSD exchange, charges on fiscal receipts, `DIN` not read as RSD, slip vision asked to compute, the routing
  predicate landing after the slip jobs, PR 8 unreviewed and stale backlog, a screenshot run deleting another PR's
  pictures, tests that could not go red). Rejected: dropping a kept stated charge when the foreign sum changes
  (Fable; the operator keeps it), refusing a same-currency transfer whose sides differ (Fable; P2-1).
- **Phase 7 closing (phase-7 vs master), 2026-10-02:** none by both; 1 Codex only (an incomplete slip
  cancelled during extraction and then restored was stranded); 2 Fable only, plus 3 minor (a
  receiving-side fee read as not included, storing the received leg short by the fee; a slip tolerance
  of about a para holding slips paid out in whole dinars; an unsupported slip currency called
  unreadable, the trace page hiding the failure reason, a stale comment). Rejected: `MissingReceivedAmount`
  naming the destination currency the record still holds (Fable; A-6, only the reason is stored), a
  both-sides-RSD misread failing as `SameWallet` (Fable; recoverable by reply). The Codex run took about
  two minutes over the whole phase, so its coverage was thin. Codex was at its usage limit for the fix
  PRs, so opus reviewed them; it found 1 more, deferred to the backlog (a job failing while its record
  is cancelled leaves no reason, and Restore strands the record).
- **Phase 8a planning, spec `2026-10-02-integrity-and-bug-reports-design.md`, 2026-10-02:** 14 by both (a
  cancelled failed record tripping I-2, cancelled held receipts warning forever, failed jobs counted as bugs,
  fiscal URLs in raw text reaching the prompt and the export, the awaiting-confirmation predicate copied into
  SQL, I-1/I-2 overlap, the oracle's branches untested, explanation and delivery sharing one state, the `/bug`
  Telegram seams, findings explained after filing, the log-retention claim, the export's privacy and trust
  boundary, the PR cut, health timeout cost [rejected]); 3 Codex only (`/bug` redelivery duplicates, a failing
  check blocking a report, unbounded snapshots); 4 Fable only (the boundary rule already existing, the CLI
  verb's wiring, dashboard Create duplicates [rejected], wallet currency immutability [rejected]).
- **Phase 8a planning, implementation plan (`.superpowers/p8a-plan/`), 2026-10-02:** 2 by both (a double click
  filing two reports or calling the model twice, the health check's 5 s budget unmeasured — to the operator); 5
  Codex only (fiscal links in wallet and category names, Telegram ids in snapshotted log lines, a report about a
  corrupt transfer unreadable through the trace reader, a Record anyway mid-run making a false Bug, the closing
  worktree's paths); 5 Fable only (database test classes of Host/Demo run outside the suite lock, a revoked key
  burning a report's attempts, the clipboard read-back in headless Chromium, a new bot reply without its scene, a
  wrong contract sentence), plus 13 minor left as they are. Rejected: composite `--filter` unverified (Fable),
  the boundary regex matching constructor parameters (Fable), a non-UTC `@idleSince` (Fable), the CLI host under
  Development (Codex). Fable's first run hit the session limit and was re-run after the reset.
- **Phase 8a closing (phase-8a vs master), 2026-10-08:** Fable 5.1 found 0 critical, 0 major and 11 minor; Codex
  (adversarial) 1 high and 2 medium. 2 by both (a transfer's fee line in another currency than its fee leg's wallet
  passing every check — Codex found it, Fable's F2 touched it from the trace page it breaks; a bug-report reply
  sent again on every tick once storing its reference failed, holding back later replies — Codex, with Fable's
  malformed reply address as a minor); 1 Codex only (fiscal links in non-canonical forms, now reaching reports and
  the export too); 9 Fable only, all minor (a `null` JSON element failing the export, the `/bug` matcher without a
  match timeout before the owner gate, Explain after Create offering a second report, `download.js` revoking the
  URL during the click, the skill's "prints one line", a one-record scope still aggregating the whole ledger and
  the export's full pass per report, nested log properties not scanned for Telegram ids, `/bug` silent while the
  model key is missing, no browser test for a stranger's `/bug` getting silence). Fixed on the branch: both shared
  findings (a new I-2 condition; the worker remembers a sent reply's reference and warns once, EventId 1910, on an
  unreadable reply address), the `null` element, the matcher's timeout, re-explain after Create, `download.js`,
  the skill's wording. Replied without a code change: fiscal-link forms (backlog by the operator), scale (backlog),
  nested log properties, the silence without a model key (P-21), the missing browser test. Both reviewers ran
  against a stale local `master`, so their range also held Phase 7; every finding was in Phase 8a's code.
- **Phase 8b planning, spec `2026-10-08-spending-summary-design.md`, 2026-10-08** (run the day the trial
  closed, before the branch had the news): 6 by both (a transfer fee on
  the source wallet and counted twice against the moved amount, a finished month compared with a clamped window,
  the average's history start unknowable from four months of rows, the 4 096-character limit not guaranteed,
  percentages against zero or a negative net, foreign lines with no charge and unconvertible amounts in rankings);
  6 Codex only (the automatic summary sent before the month's records settle, rate snapshots not validated,
  Explain rebuilding other figures than shown, displayed parts not adding up, the marker store in no PR, a
  closing PR where the rules now commit the closing straight to the aggregate branch); 10 Fable
  only (Explain's model call holding the Telegram update loop, an impossible "No wallet" bucket, stale rates
  unmarked, `/summary` matching, A-1 superseding a Phase 7 sentence and moving screenshots, refunds counted as
  income, the automatic summary's zone and logging, archived wallets, one text renderer, payload parsing).
  None rejected; the PR cut was then redone by stage, as `CLAUDE.md` §5 now asks.
