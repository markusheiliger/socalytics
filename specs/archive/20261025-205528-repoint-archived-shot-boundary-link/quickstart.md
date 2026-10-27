# Quickstart: Validate the repointed link

## Prerequisites

- Node.js 24 and a checkout of the repository root; no other tooling or Docker is needed.

## Scenario 1: The whole repository validates (User Story 1, SC-001)

```bash
node .github/scripts/check-markdown.mjs
```

Expected: exit code 0 with no dead link and no other finding.

## Scenario 2: Only reference rewrites changed (SC-002, FR-002)

Compare against the merge base with the implementation pull request's base branch (the default branch, for example `origin/main`), scoped to the edited spec:

```bash
git diff $(git merge-base HEAD origin/HEAD) --unified=0 -- specs/20261025-003459-deterministic-agent-workload-test/
```

Expected: line 83 changed, with only `archive/` added to the Shot Boundary Analyst link target; lines 9 and 11 are either untouched or rewritten to `specs/archive/20261023-022148-shot-boundary-analyst` by the archive tooling's reference rewrite; no other line changes.

## Scenario 3: No other change outside this spec (FR-003)

```bash
git diff $(git merge-base HEAD origin/HEAD) --name-only
```

Expected: only files under `specs/archive/20261025-205528-repoint-archived-shot-boundary-link/` and `specs/20261025-003459-deterministic-agent-workload-test/spec.md`.

## Scenario 4: The link opens the archived spec (acceptance scenario 2)

Follow the link from the Dependencies entry; it opens `specs/archive/20261023-022148-shot-boundary-analyst/spec.md`.

See [data-model.md](data-model.md) for the link's before and after values.
