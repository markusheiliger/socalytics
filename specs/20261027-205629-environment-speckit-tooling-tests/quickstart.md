# Quickstart: Validating the Spec Kit Tooling Tests Check

Run from the repository root with the Node.js the repository uses. Nothing is installed or downloaded. Data shapes are in [data-model.md](data-model.md).

## 1. The action's own tests

```bash
node --test .github/actions/environment-verify/analyst-projects.test.mjs \
  .github/actions/environment-verify/verification-scope.test.mjs \
  .github/actions/environment-verify/verification-plan.test.mjs \
  .github/actions/environment-setup/toolchain.test.mjs
```

Expected: all pass, including the new cases for check success, failure, skip, nested-run guard, finalize run, order, and scope. Removing the check, its trigger, or the tooling pattern makes at least one new case fail (SC-005).

## 2. The tooling tests the check runs

```bash
node --test .github/scripts/speckit-*.test.mjs
```

Expected: pass; this is the reproduce command printed by a failure.

## 3. Coverage of the stopped spec's task files

```bash
printf '.github/scripts/speckit-implement-core.mjs\n.github/scripts/speckit-implement-work.mjs\n' > /tmp/changed.txt
node .github/actions/environment-verify/verification-plan.mjs --root . --mode task --changed-files /tmp/changed.txt | node -e 'const p=JSON.parse(require("fs").readFileSync(0,"utf8"));console.log(p.scope,p.uncovered)'
rm /tmp/changed.txt
```

Expected: the plan reports the files as covered (the scope names a Spec Kit tooling file, not "is not Markdown", and `uncovered` is `[]`); the action reports the "Spec Kit tooling tests" check, which the action fixtures assert.

## 4. Unrelated `.github/` files stay uncovered

Repeat scenario 3 with `.github/workflows/speckit-orchestrate.yml` and `.github/scripts/check-markdown.mjs`. Expected: both are listed in `uncovered`.

## 5. Documentation

```bash
node .github/scripts/check-markdown.mjs
```

Expected: no findings; the environment-verify README documents the check, its trigger, command, failure message, and scope.
