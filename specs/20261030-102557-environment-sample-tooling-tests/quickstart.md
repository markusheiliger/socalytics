# Quickstart: Validating Sample Tooling Tests in Environment Verification

Prerequisite: Node.js 24 on `PATH`; run from the repository root. Nothing is installed or downloaded.

## 1. Run the action's own tests

```bash
SOCALYTICS_ENVIRONMENT_TESTS_RUNNING=1 node --test \
  .github/actions/environment-verify/verification-scope.test.mjs \
  .github/actions/environment-verify/verification-plan.test.mjs \
  .github/actions/environment-verify/analyst-projects.test.mjs
```

Expected: all pass, including the new cases for selection, execution, failure propagation, a missing test file, coverage,
and sharding. The assertions for the check, its pattern, and its budget are what detect their removal.

## 2. Check the plan for a change of only the sample tooling test

```bash
printf 'tools/samples.test.mjs\nspecs/20261028-235035-case-insensitive-samples-test/spec.md\n' > /tmp/changed.txt
node .github/actions/environment-verify/verification-plan.mjs --root . --mode task --changed-files /tmp/changed.txt | grep -A3 '"uncovered"'
```

Expected: `"uncovered": []`. With `tools/other.mjs` added to the list, only that path is listed.

## 3. Run the sample tooling test itself

```bash
node --test tools/samples.test.mjs
```

Expected: the command the check runs passes on a healthy tree.

## 4. Existing checks stay green

```bash
node --test .github/scripts/speckit-*.test.mjs
node .github/scripts/check-markdown.mjs --files /tmp/changed.txt
```

Expected: both pass; contract and behavior details are in [contracts/sample-tooling-check.md](contracts/sample-tooling-check.md).
