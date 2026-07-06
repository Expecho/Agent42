# Decision records (ADRs)

One file per decision: `NNNN-short-slug.md` (zero-padded, chronological).
Created via `/decide` — which also checks for conflicts with existing ADRs and
handles supersession. Never edit an accepted ADR's decision content; supersede it.

## Rules

- `status:` is one of `accepted`, `superseded`, `rejected`.
- Superseding: new ADR gets `supersedes: NNNN`; the old ADR gets
  `superseded_by: MMMM` and `status: superseded`. Both edits land in the same commit.
- Keep it short. Context (2–5 lines), Decision (1–3 lines), Alternatives considered
  (bullets with the reason each lost), Consequences (incl. accepted downsides).
- Tag with the area (`tags: [database, auth, ...]`) so `grep -l "tags:.*auth"` works.

## Finding decisions

```
grep -rl "tags:.*<area>" .agentic/decisions/       # by area
grep -rL "superseded_by" .agentic/decisions/*.md   # still-active decisions
```
