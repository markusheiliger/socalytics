# gha Spec Kit Extension

Repository-local Spec Kit extension that guides spec authoring in Copilot Chat
and connects `/speckit-implement` to the `Spec Kit orchestrate` GitHub Actions
workflow. The ID is `gha` (GitHub Actions) because Spec Kit already bundles an
extension with the ID `github`.

## Commands

| Command | Skill | Purpose |
| --- | --- | --- |
| `speckit.gha.author` | `/speckit-gha-author [architecture section, folder, or feature idea] [notes]` | Guide a spec through specify, clarify, plan, tasks, and analyze to a pushed spec; without input, propose the next logical spec |
| `speckit.gha.route` | `/speckit-gha-route` | Mandatory `before_implement` hook: choose local implementation or a GitHub request |
| `speckit.gha.request` | `/speckit-gha-request [folder]` | Request implementation on GitHub directly |
| `speckit.gha.diagnose` | `/speckit-gha-diagnose [pull request, twin, or folder] [notes]` | Diagnose why an implementation on GitHub stopped and decide how to continue; amend the spec artifacts only when they are the cause |
| `speckit.gha.fix` | `/speckit-gha-fix [pull request] [guidance]` | Author or rework a fix spec for the cause of a stopped implementation outside its spec; locally it explains `/speckit fix` and offers to post it |
| `speckit.gha.refine` | `/speckit-gha-refine [folder]` | Mandatory `after_tasks` hook: refine the `[P]` groups of the generated `tasks.md` so parallel tasks never change the same files |
| `speckit.gha.archive` | `/speckit-gha-archive [folder]` | Optional `after_converge` hook: archive an implemented spec to `specs/archive/<folder>` and rewrite every reference to it before a local or manual merge |
| `speckit.gha.architecture` | `/speckit-gha-architecture [folder]` | Mandatory `after_plan` hook: write the `## Architecture Updates` section of `plan.md` (narratives, design updates, status statements at merge) |
| `speckit.gha.drift` | `/speckit-gha-drift [folder]` | Mandatory `before_converge` hook: check `docs/architecture/` against the implementation and report drift as convergence findings (read-only) |

## Keeping the Architecture in Sync

The constitution (Principle I) makes `docs/architecture/` the authority for the
current design and requires it to describe the implemented state at merge.
Documentation tasks that only substituted prescribed sentences left planned and
unimplemented statements behind, so the extension checks the architecture at
both ends of a spec:

- **Plan:** `/speckit-gha-architecture` writes `## Architecture Updates` in
  `plan.md`: the narratives the feature changes, the design updates it applies
  now, and every status statement in `docs/architecture/` that the merge makes
  false, or `None` with a reason. It requires an outcome-based documentation
  task. `speckit-author.mjs status` refuses to publish a spec without the
  section, or, unless it says `None`, without a task that references
  `docs/architecture/`.
- **Convergence:** `/speckit-gha-drift` compares the architecture with the
  implemented code and reports stale, missing, or contradicting statements.
  `/speckit-converge` appends a task for each, which the next task run
  implements before the merge.
- **Amendments:** `/speckit-gha-diagnose` carries a changed design decision into
  the Architecture Updates section and an unchecked documentation task; an
  amendment never edits `docs/architecture/` itself.

## Authoring Specs

`/speckit-gha-author` keeps the whole authoring flow in Copilot Chat. It runs
the repository's Spec Kit skills in Spec Kit's order (`/speckit-specify`,
`/speckit-clarify`, `/speckit-plan`, optional `/speckit-checklist`,
`/speckit-tasks` with its refine hook, `/speckit-analyze`) and pauses after
specify and clarify, plan, tasks, and analyze. It changes spec artifacts only
through those skills and never ticks a checklist item without the user's
confirmation. Its additions are:

- **Input:** nothing (it compares the architecture with the existing specs,
  twin issues, and code, and proposes the next three specs), an architecture
  section such as `docs/architecture/analyst-manager.md#fleet-visibility-in-the-web-ui`,
  a feature idea in free text (it maps the idea to the architecture first and
  says when the architecture must change with it), or a spec folder to resume.
- **Environment check:** when the plan needs new frameworks, SDKs, or tools, it
  stops and offers to author the environment spec first, then records it as a
  dependency through `/speckit-clarify`.
- **Publish checks:** stage `tasked`, no open clarification markers or checklist
  items, a `specs/README.md` row, an `## Architecture Updates` section in
  `plan.md` with its documentation task, and a clean Markdown check.
- **Last decision:** commit and push the spec folder and `specs/README.md`.
  Everything after the push belongs to the GitHub workflows.

`node .github/scripts/speckit-author.mjs status [folder] [--json]` reports a
spec's next Spec Kit step, its publish blockers, and its Git state, which is how
the command resumes. It is read-only.

## Refining Parallel Groups

`/speckit-tasks` sets `[P]` from the task texts alone, which miss shared edits
such as service registrations or project files. The implementation workflow runs
consecutive `[P]` tasks under one heading in parallel, so
`/speckit-gha-refine` checks each group against the files its tasks will really
change and fixes overlaps: it names the shared file, moves a small shared edit
into one task after the group, or ends the group. For a fresh `tasks.md` it also
forms new groups, separates groups that follow each other with `###` headings,
and renumbers the tasks. For a spec already being implemented it only fixes
conflicting groups, without moving or renumbering tasks.

`node .github/scripts/speckit-tasks-groups.mjs <folder | tasks.md> [--json]`
prints the task order exactly as the workflow plans it, with warnings for headings
that do not separate groups. It is read-only.

## Archiving Implemented Specs

Implemented specs move to `specs/archive/<folder>` in the commit that merges
their implementation. Implementations on GitHub do it in their merge;
`/speckit-gha-archive` does it for a local or manual merge with
`node .github/scripts/speckit-archive.mjs <folder> [--check]`, which refuses
unchecked tasks, moves the folder (staged as a rename), and rewrites relative
Markdown links into and out of the folder and plain-text `specs/<folder>`
mentions such as code comments. Running it again only rewrites references that
appeared since.

`/speckit-gha-diagnose` is also the method of the `Spec Kit diagnose` agentic
workflow, which runs it non-interactively when an implementation stops or a
person comments `/speckit diagnose` on the pull request, and to rework an
amendment pull request after consistency findings (`fix`) or a person's
feedback (`revise`). Locally it runs interactively and asks before it changes
anything. Its report recommends exactly one command that fits its category and
can be posted as it is, with the guidance written out; the agent checks it with
`node .github/scripts/speckit-diagnose.mjs check-report <report.json>` first.

`/speckit-gha-fix` is the method of the `Spec Kit fix` agentic workflow, which
a person starts with `/speckit fix <what to fix>` when the cause lies outside
the stopped spec. It authors a small fix spec against the default branch, which
goes through the same consistency check and review as an amendment; once merged
and implemented, the stopped implementation resumes by itself.

`speckit.gha.route` and `speckit.gha.request` both run
`node .github/scripts/speckit-orchestrate.mjs request`, which
checks the spec on the remote default branch (merged, stage `tasked`, all
checklists checked), finds its twin issue, and adds the
`speckit:stage:implement` label with the user's own GitHub token. The
`Spec Kit prepare` workflow then validates the request on GitHub.

## Routing

`/speckit-gha-route` decides without asking when possible:

- `GITHUB_ACTIONS=true`: implement locally (server-side runs).
- `SPECKIT_IMPLEMENT_MODE=local` or `SPECKIT_IMPLEMENT_MODE=remote`: use that mode.
- Otherwise it asks the user. A GitHub request ends with an explicit STOP so
  `/speckit-implement` does not continue. Spec Kit hooks have no formal abort,
  so this relies on the agent honoring the hook result.

## Install or Update

The extension is committed together with the files Spec Kit generates from it,
so a fresh clone needs no install step. After changing this source, reinstall
from the repository root:

```powershell
specify extension add .specify/extension-src/gha --dev --force
```

A development install links `.github/skills/speckit-gha-*/SKILL.md` to copies
under `.specify/extensions/gha/.specify-dev/`. Symbolic links break on clones
without symlink support (the Git default on Windows), so commit them as regular
files:

```powershell
foreach ($name in 'speckit-gha-author', 'speckit-gha-route', 'speckit-gha-request', 'speckit-gha-diagnose', 'speckit-gha-fix', 'speckit-gha-refine', 'speckit-gha-archive', 'speckit-gha-architecture', 'speckit-gha-drift') {
    $skill = ".github/skills/$name/SKILL.md"
    $content = Get-Content -Raw ".specify/extensions/gha/.specify-dev/extension-skills/$name/SKILL.md"
    Remove-Item $skill -Force
    Set-Content -NoNewline -Path $skill -Value $content
    git rm --cached -q $skill
    git update-index --add --cacheinfo "100644,$(git hash-object -w $skill),$skill"
}
```
