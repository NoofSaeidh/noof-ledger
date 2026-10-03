# Specs — designs as approved

One file per design, named `<date>-<topic>.md` after the day it was written. A spec records a design
as the operator approved it, and the reasoning behind it. It is not a description of the code as it
stands now: that is `docs/STATUS.md` and the rules in CLAUDE.md and `.claude/rules/`.

## Changing a spec

The same reasoning as `docs/decisions/README.md` applies: the text is a record that agents read
directly and that other documents cite, so what was approved is not silently rewritten once its phase
is over.

- **Status line** — the `**Status:**` line under the title stays true; one that still says a spec
  awaits review after it was approved is corrected in place.
- **While its phase is open** — the spec is still a working document. Changes the operator makes
  after its reviews or at implementation planning go into a dated amendments section, and the status
  line says the amendments win where they and an earlier paragraph differ. A body paragraph may be
  edited to match, citing the amendment's id. An amendment replaced by a later one keeps its text
  behind a leading `*(Superseded by <id>.)*`.
- **Once a later design changes it** — the later spec says near its top which spec and sections it
  amends; that header is the authority. A marker on the earlier spec's paragraph (the text kept,
  struck through or behind a `Superseded <date> by <later spec>` note) is welcome but not
  guaranteed — older specs were not marked retroactively, so an unmarked paragraph may still be
  amended by a later spec.
- **Typos, wording, broken links** — edited in place.
