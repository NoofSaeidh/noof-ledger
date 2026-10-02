# Specs — designs as approved

One file per design, named `<date>-<topic>.md` after the day it was written. A spec records a design
as the operator approved it, and the reasoning behind it. It is not a description of the code as it
stands now: that is `docs/STATUS.md` and the rules in CLAUDE.md and `.claude/rules/`.

## Changing a spec

The same reasoning as `docs/decisions/README.md` applies: the text is a record that agents read
directly and that other documents cite, so what was approved is never silently rewritten.

- **Status line** — the `**Status:**` line under the title is kept current in place: approved,
  amended, implemented.
- **Before its phase is finalised** — changes the operator makes to a spec (after its reviews, at
  implementation planning) go into the spec itself, as a dated amendments section that says it wins
  where it and an earlier paragraph differ. The earlier paragraph keeps its text; an amended item
  gets a leading `*(Superseded by <amendment id>.)*`.
- **Once a later design changes it** — the later spec says in its status line which spec and
  sections it amends. The earlier spec's paragraph keeps its text behind a leading
  ``*(Superseded <date> by `<later spec>.md`.)*``.
- **Typos, wording, broken links** — edited in place.
