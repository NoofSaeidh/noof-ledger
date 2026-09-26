# 6. Closing a phase *(settled)*

A phase is not finished when its tests pass. It is finished when the next person — or the next
agent, with none of this conversation — can pick it up without rediscovering what it cost.

- **Write down what outlived the phase.** A rule that will bind future work goes in this file. A
  decision and its reasoning goes in `docs/OPEN-QUESTIONS.md`. Work deliberately not done goes in
  `docs/BACKLOG.md` with enough reasoning that nobody re-proposes it as new. A repeatable procedure
  goes in `ops/RUNBOOK.md` or a skill. If it changes how someone should work, it is not optional.
- **Only what generalises.** A defect fixed inside the phase is in the commit that fixed it; that
  is where it belongs. Promote a lesson here only when it would otherwise be paid for twice —
  the PostgreSQL microsecond flake earned its line by shipping twice before anyone noticed.
- **Keep this file short.** It is read in full at the start of every session, so length is a tax on
  every single one. Anything that runs past a short paragraph belongs in its own document under
  `docs/`, linked from here in one line. Prefer deleting a rule the code now enforces by itself: a
  test that fails is worth more than a paragraph that asks nicely.
- **Correct what has gone stale**, starting with the status block and `README.md`. A public README
  that understates the project by two phases, or claims a guarantee the code stopped providing, is
  worse than no README — someone trusts it.
- **Leave nothing uncommitted.** Working tree clean, every documentation change committed alongside
  the work it describes, and the branch integrated or explicitly left open by the operator's choice.
