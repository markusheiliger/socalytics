# Feature Specification: Environment Exclusivity Guard Ignores Archive Reference Rewrites

**Feature Branch**: `20261026-100223-environment-guard-archive-rewrites`

**Created**: 2026-10-26

**Status**: Draft

**Input**: User description: "Make the environment-exclusivity guard of the Spec Kit implementation tooling ignore archive reference rewrites. `environmentExclusivityReasons` in `.github/scripts/speckit-implement-core.mjs`, applied by `checkStagedTask` in `.github/scripts/speckit-implement-work.mjs` to every path the implementation branch changes against its base branch, counts the files that `refreshArchivedReferences` rewrites after every base branch merge as foreign changes, although `referenceOnlySpecChanges` explicitly allows them for every implementation. An environment spec therefore fails every land attempt, and retry and sync cannot help. The guard must not treat changes under `specs/` outside the spec's own folder as foreign when they are only archive reference rewrites, using the same criterion as `referenceOnlySpecChanges`."

## Problem

An environment spec is a spec whose implementation changes nothing but the two environment action folders (`.github/actions/environment-setup/` and `.github/actions/environment-verify/`) plus its own `tasks.md`. The implementation tooling enforces this through the environment exclusivity guard: when the implementation branch changes an environment action, every other changed path (compared to the base branch) makes the task fail with "changes to the environment actions must come from a standalone environment spec, but the branch also changes ...".

After every merge of the base branch into an implementation branch, the archive tooling rewrites links and mentions of `specs/<folder>` to `specs/archive/<folder>` in the other spec files, for every spec archived in the meantime. The merge-time check (`referenceOnlySpecChanges`) accepts exactly these rewrites outside the spec's own folder for every implementation. The exclusivity guard does not: it counts the rewritten spec files as foreign changes. As a result an environment spec cannot land any task once such a rewrite is on its branch, and because every sync brings the rewrites back, `/speckit retry` and `/speckit sync` cannot help. The stopped spec `specs/20261025-194925-dotnet-setup-resilience` (twin #326, pull request #327) is in exactly this state: its task T004 is implemented correctly but is rejected by the guard on every attempt, before and after a sync.

## User Scenarios & Testing *(mandatory)*

### User Story 1 - An environment spec lands after syncing the base branch (Priority: P1)

The implementation automation (and the person watching it) lands the tasks of an environment spec whose branch, after merging the base branch, also carries archive reference rewrites in other spec files. The guard accepts those rewrites as non-foreign changes, so the task lands as long as nothing else besides the environment actions and the spec's own `tasks.md` changed.

**Why this priority**: Without it no environment spec can be implemented once any other spec was archived while it was in progress; the stopped implementation of #327 and every future environment change stays blocked.

**Independent Test**: Run the guard's unit tests with a branch change set that contains an environment action file, the spec's own `tasks.md`, and a spec file outside the spec's folder that equals the base branch's version with the archive applied; the guard reports no reason. Repeat with the real staging check against a temporary repository.

**Acceptance Scenarios**:

1. **Given** a branch that changes an environment action file and, outside the spec's own folder, only spec files that are modified and equal the base branch's version with every archive of the staged tree applied, **When** the guard checks the branch, **Then** it reports no reason.
2. **Given** the same branch after another sync brings further archive rewrites, **When** the guard checks it again, **Then** it still reports no reason.
3. **Given** the stopped implementation of `specs/20261025-194925-dotnet-setup-resilience` is started again after this spec is merged and the base branch is synced, **When** its task T004 reaches the land step, **Then** the exclusivity guard no longer rejects it.

---

### User Story 2 - Everything else still fails the guard (Priority: P1)

A change that is not a pure archive reference rewrite keeps failing the guard, so environment specs stay standalone and the review hold on them keeps its meaning.

**Why this priority**: The guard exists to keep environment changes isolated; loosening it beyond reference rewrites would break that protection.

**Independent Test**: Unit tests with one foreign path per case; each case yields the "standalone environment spec" reason naming exactly the offending paths.

**Acceptance Scenarios**:

1. **Given** a branch that changes an environment action and a spec file outside the spec's folder with an edit that is not an archive reference rewrite, **When** the guard checks it, **Then** it fails and names that file.
2. **Given** a branch that adds or deletes a spec file outside the spec's own folder, **When** the guard checks it, **Then** it fails and names that file.
3. **Given** a branch that changes a file under `.github/` outside the environment actions, under `.specify/`, or any source file, **When** the guard checks it, **Then** it fails and names that file.
4. **Given** a branch with reference rewrites and a foreign change at once, **When** the guard checks it, **Then** only the foreign paths are named and the rewritten files are not.
5. **Given** a branch that changes no environment action, **When** the guard checks it, **Then** it reports no reason, as before.

---

### User Story 3 - Other checks keep their behavior (Priority: P2)

The staging validation, the protected-path rule, the merge-time `referenceOnlySpecChanges` check, and the archive rewrite itself behave exactly as before.

**Why this priority**: The change must not alter what other checks accept or refuse.

**Independent Test**: The existing tests of the implementation core, the work tooling, and the archive tooling pass unchanged.

**Acceptance Scenarios**:

1. **Given** the existing tests of the Spec Kit tooling, **When** they run after the change, **Then** they pass without edits to their expectations (new cases may be added).
2. **Given** an agent change that touches a protected path (for example a spec file outside its own folder) in a task's staged change, **When** the staged change is validated, **Then** it is rejected as before.

### Edge Cases

- Only `tasks.md` of the spec (at its active or archived location) is exempt, as before. Every other file of the spec's own folder (`spec.md`, `plan.md`, `research.md`, and so on) that the branch changes stays foreign, even when the change is an archive reference rewrite; the criterion of FR-002 applies only outside the spec's own folder.
- A file outside the spec's folder that is modified in a way that matches the archive rewrite only partially counts as foreign.
- A file that equals the base version because nothing changed does not appear in the change set and is not an issue.
- A path outside the spec's folder whose staged content equals the base branch tip version is not a change against the base branch (for example because the base branch advanced and already contains the identical archive rewrite) and MUST NOT count as foreign, even when it appears in the diff against the merge base.
- The base branch version or the staged version of a file cannot be read: the file counts as foreign (never silently accepted).
- No archive exists in the staged tree: no rewrite can match, so any modified spec file outside the spec's folder is foreign.
- The base branch is not in the checkout: the existing "cannot be compared with its base branch" reason is kept.
- Archived specs of the staged tree that the base branch does not know yet are applied in the comparison exactly as the merge-time check does.

## Requirements *(mandatory)*

### Functional Requirements

- **FR-001**: The environment exclusivity guard MUST NOT count a changed path under `specs/` outside the spec's own folder as foreign when it is only an archive reference rewrite.
- **FR-002**: "Only an archive reference rewrite" MUST use the same criterion as the merge-time check `referenceOnlySpecChanges`: the file is modified (neither added nor removed) and its staged content equals the base branch version with every archive of the staged tree applied. The criterion MUST be shared with that check (reusing its function or the archive rewrite logic), not reimplemented, so the two cannot drift apart.
- **FR-003**: Any other changed path outside the environment actions and the spec's own `tasks.md` (active or archived location) MUST still fail the guard with the existing reason, naming exactly those paths. This includes non-reference edits under `specs/`, added or deleted spec files, changes to `.github/` outside the environment actions, and any other file.
- **FR-004**: The guard decision MUST remain a pure function of its inputs (the spec folder, the changed paths, and the already determined set of reference-rewrite paths or an equivalent injected criterion) so it is unit-testable without a Git repository; reading blobs from Git happens in the caller.
- **FR-005**: When the content of a candidate file cannot be read or compared, the guard MUST treat the file as foreign.
- **FR-006**: The staging validation (`checkStagedTask`) MUST keep its other reasons, the protected-path rule, the size limits, and the merge-time `referenceOnlySpecChanges` check MUST keep their behavior and results.
- **FR-007**: Unit tests MUST cover: a pure reference rewrite accepted next to an environment change; a non-reference edit, an added file, a deleted file, a `.github/` file outside the environment actions, and a source file each still rejected; a mixed branch naming only the foreign paths; a branch without environment changes; and an integration-style test of `checkStagedTask` against a temporary Git repository with an archived spec.
- **FR-008**: `AGENTS.md`, `README.md`, and the guard's code comment MUST state the exception where they describe the environment exclusivity rule; the workflows README and the gha extension README MUST each get one sentence stating the rule with the exception: archive reference rewrites outside the spec's folder do not count as other changes.

### Key Entities

- **Environment spec**: A spec whose implementation changes only the environment action folders and its own `tasks.md`.
- **Branch change set**: Every path the implementation branch changes compared to its base branch, including the staged change.
- **Archive reference rewrite**: A modification of a spec file outside the spec's own folder that the archive tooling makes to repoint `specs/<folder>` references at `specs/archive/<folder>`.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: For a branch with an environment action change, the spec's own `tasks.md`, and any number of archive reference rewrites, the guard reports zero reasons in 100% of the unit test cases.
- **SC-002**: For every non-reference change class listed in FR-003, the guard still reports a reason that names exactly the foreign paths (zero false acceptances in the tests).
- **SC-003**: The existing tests of the Spec Kit tooling (`node --test .github/scripts/speckit-*.test.mjs`) pass, and the repository Markdown validation (`node .github/scripts/check-markdown.mjs`) reports no finding.
- **SC-004**: After this spec is merged, the stopped implementation of `specs/20261025-194925-dotnet-setup-resilience` lands task T004 after a sync without a person editing the tooling or the spec's files.

## Assumptions

- Archive reference rewrites in the spec's own folder other than `tasks.md` are not exempted: this spec limits the exemption to files outside the spec's folder and leaves the guard's rule for the own folder unchanged.
- `referenceOnlySpecChanges` and `archiveRewrite` in `.github/scripts/speckit-archive.mjs` are the single source of truth for what counts as a reference rewrite; they are not changed in behavior.
- The change set for the guard is the one `checkStagedTask` already computes against the base branch merge base; the criterion compares against the base branch commit as the merge-time check does.
- No product code, platform, contract, or `docs/architecture/` statement is affected; the plan's Architecture Updates section is `None`.
- Environment specs stay held for a person's review and the merge still self-tests changed actions; this spec changes neither.
- This spec changes only repository tooling (`.github/scripts/`) and its documentation. It is not an environment spec: it changes no file under the environment actions.
- The spec `specs/20261025-194925-dotnet-setup-resilience` depends on this spec and must be started again only after this spec is merged; this spec itself depends on no other spec.
