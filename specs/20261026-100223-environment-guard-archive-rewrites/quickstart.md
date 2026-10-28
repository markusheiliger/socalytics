# Quickstart: Validating the Guard Change

Prerequisites: Node.js 24 and `git` on `PATH`; run everything from the repository root. No Docker or platform tooling is needed.

## 1. Unit and temporary-repository tests

```bash
node --test .github/scripts/speckit-implement-core.test.mjs .github/scripts/speckit-archive.test.mjs .github/scripts/speckit-implement.test.mjs
```

Expected: all pass, including the new cases for User Stories 1 and 2 of [spec.md](spec.md):

- a pure reference rewrite next to an environment action change gives no reason (also after a second rewrite);
- a non-reference edit, an added file, a deleted file, a `.github/` file outside the environment actions, and a source file each give the reason naming exactly that path;
- a spec file outside the spec's folder that is in the merge-base change set but has no diff against the base branch tip (not a change against the base branch) is not a foreign path;
- a mixed branch names only the foreign paths;
- `checkStagedTask` against a temporary repository with an archived spec accepts the rewritten spec file and refuses a non-reference edit.

## 2. Existing tooling tests (User Story 3)

```bash
node --test .github/scripts/speckit-*.test.mjs
```

Expected: all pass without changed expectations in existing cases.

## 3. Documentation

```bash
node .github/scripts/check-markdown.mjs
```

Expected: no finding. `AGENTS.md`, `README.md`, `.github/workflows/README.md`, and `.specify/extension-src/gha/README.md` state that archive reference rewrites outside the spec's folder do not count as other changes.

## 4. After merge (SC-004)

Start the stopped implementation of `specs/20261025-194925-dotnet-setup-resilience` again (`/speckit retry` or `/speckit sync` on pull request #327) and check that task T004 passes the land step.
