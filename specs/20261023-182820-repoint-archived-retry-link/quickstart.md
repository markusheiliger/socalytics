# Quickstart: Repoint the archived workload retry link in the stall expiry spec

## Prerequisites

- Node.js 24 and a checkout of the repository; no restore or build is needed.

## Validate the fix

1. From the repository root, run the whole-repository Markdown validation:

   ```bash
   node .github/scripts/check-markdown.mjs
   ```

   Expected: exit code 0 and no findings (SC-001, FR-004).

2. Confirm the change is the three lines of the archive rewrite and nothing else:

   ```bash
   git diff --stat
   git diff -U0 -- specs/archive/20261023-150804-deterministic-stall-expiry-test/
   ```

   Expected: two files and three changed lines (`spec.md` lines 80 and 19, `tasks.md` line 102), with only `archive/` added to the path of `20261020-224223-deterministic-simulated-workload-retry` in each; line 80 is the link fix, the other two are accepted plain-text side effects (SC-002, FR-001, FR-002, FR-003).

3. Open the Dependencies bullet of the stall expiry spec and follow the retry link; it opens the archived spec `20261020-224223-deterministic-simulated-workload-retry` (User Story 1, scenario 2).
