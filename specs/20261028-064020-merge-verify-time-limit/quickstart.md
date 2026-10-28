# Quickstart: Validating the Change

Prerequisites: Node.js 24, Python 3 (for the action's tests), run from the repository root.

1. Environment action tests, including the new narrowed-run tests:

   ```bash
   node --test .github/actions/environment-verify/*.test.mjs .github/actions/environment-setup/toolchain.test.mjs
   ```

   Expected: all pass; the tests for `environment-tests-only` show that only "Environment action tests" runs, that no
   component, Python, or Markdown check runs, that a failing test fails the step, and that unconfigured tests fail it.

2. Spec Kit tooling tests:

   ```bash
   node --test .github/scripts/speckit-*.test.mjs
   ```

3. Workflow configuration (manual, after a person's direct edit of the workflow; it must be on the default branch
   before `/speckit implement` is requested, and an older workspace action ignores the input):

   ```bash
   grep -n "timeout-minutes: 150" .github/workflows/speckit-implement-merge.yml
   grep -n "environment-tests-only" .github/workflows/speckit-implement-merge.yml
   ```

   Expected: the job `verify` has `timeout-minutes: 150` and only the step `selftest-verify` passes the input.

   Also read the two passages of `.github/workflows/README.md` that the Scope Note of `tasks.md` names (the merge
   verification row of the table of where AI runs, and the sentence on merged environment-action repairs and their
   self-tests): they must state the 150-minute limit and that the self-test runs only the action's own tests.

4. Documentation: `node .github/scripts/check-markdown.mjs`.

5. End to end: the merge verification of an environment spec (for example twin #382) records its verdict;
   the step `Self-test the changed environment verification` takes minutes and its log lists only
   `Environment action tests`.
