# gha Spec Kit Extension

Repository-local Spec Kit extension that ships the deterministic spec authoring
workflow `author` and connects `/speckit-implement` to the `Spec Kit orchestrate` GitHub Actions
workflow. The ID is `gha` (GitHub Actions) because Spec Kit already bundles an
extension with the ID `github`.

## Commands

| Command | Skill | Purpose |
| --- | --- | --- |
| `speckit.gha.route` | `/speckit-gha-route` | Mandatory `before_implement` hook: choose local implementation or a GitHub request |
| `speckit.gha.request` | `/speckit-gha-request [folder]` | Request implementation on GitHub directly |
| `speckit.gha.diagnose` | `/speckit-gha-diagnose [pull request, twin, or folder] [notes]` | Diagnose why an implementation on GitHub stopped and decide how to continue; amend the spec artifacts only when they are the cause |
| `speckit.gha.refine` | `/speckit-gha-refine [folder]` | Mandatory `after_tasks` hook: refine the `[P]` groups of the generated `tasks.md` so parallel tasks never change the same files |
| `speckit.gha.archive` | `/speckit-gha-archive [folder]` | Optional `after_converge` hook: archive an implemented spec to `specs/archive/<folder>` and rewrite every reference to it before a local or manual merge |
| `speckit.gha.architecture` | `/speckit-gha-architecture [folder]` | Mandatory `after_plan` hook: write the `## Architecture Updates` section of `plan.md` (narratives, design updates, status statements at merge) |
| `speckit.gha.drift` | `/speckit-gha-drift [folder]` | Mandatory `before_converge` hook: check `docs/architecture/` against the implementation and report drift as convergence findings (read-only) |

## Workflows

| Workflow | Run | Purpose |
| --- | --- | --- |
| `author` ([`workflows/author/workflow.yml`](workflows/author/workflow.yml)) | `/speckit-author [feature idea or folder]` in Copilot Chat, `specify workflow run author -i feature="…"` in a terminal (with `TZ=UTC`), or `/speckit author` on an implementation pull request (the GitHub workflow Spec Kit author runs it unattended) | Author one spec deterministically through specify, clarify, plan (repaired with `/speckit-gha-architecture`), tasks (repaired with `/speckit-gha-refine`), analyze, and the publish checks, and push the spec folder; every decision comes from `.github/scripts/speckit-author-flow.mjs`, gates ask a person, and `mode=unattended` takes every recommended choice |

Spec Kit extensions cannot declare workflows, so the extension carries the
workflow as a folder that its install copies to
`.specify/extensions/gha/workflows/`, and it is registered from there (see
Install or Update). `/speckit-author` is a hand-maintained repository skill
(`.github/skills/speckit-author/`) rather than an extension command, because
Spec Kit renders extension commands only as `speckit-gha-*` skills.

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

Specs are authored with the workflow `author` (see Workflows): in Copilot Chat
through the hand-maintained repository skill `/speckit-author`, which runs it
and asks its gates in Chat, in a terminal with `specify workflow run author`,
and on GitHub by the workflow Spec Kit author for `/speckit author`. It runs
one spec through the repository's Spec Kit skills in Spec Kit's order, and
every decision between them comes from
`node .github/scripts/speckit-author-flow.mjs`; see `specs/README.md` for the
stages, gates, and inputs.

`node .github/scripts/speckit-author.mjs status [folder] [--json]` reports a
spec's next Spec Kit step, its publish blockers, and its Git state, which is how
the workflow resumes. It is read-only.

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
workflow, which runs it non-interactively whenever an implementation stops, and to rework an
amendment pull request after consistency findings (`fix`) or a person's
feedback (`revise`). Locally it runs interactively and asks before it changes
anything. Its report recommends exactly one command that fits its category and
can be posted as it is, with the guidance written out; the agent checks it with
`node .github/scripts/speckit-diagnose.mjs check-report <report.json>` first.
What each command does comes from `PR_COMMANDS` in `speckit-implement-core.mjs`: every diagnosis' evidence carries
a generated "Commands you can recommend" section (with what `/speckit retry` does while a repair is pending), and
`node .github/scripts/speckit-diagnose.mjs commands` prints the same reference locally. The skill only says how to
choose and word a command; the diagnose drift tests fail when it names an unknown command or misses a recommendable
command or flag.
Publication rejects inconsistent category/command recommendations instead of replacing them with a retry.
When the required fix is already on the implementation pull request's base branch, the diagnosis uses `outside`
and recommends `/speckit sync`: the existing Copilot conflict-resolution workflow must merge and verify the
latest base before implementation can continue.

When the cause lies outside the stopped spec, a person comments
`/speckit author <what to fix>` on the implementation pull request. The
GitHub workflow `Spec Kit author` then decides once (reuse an existing spec,
refuse, or a feature description) and runs the workflow `author` unattended
(every recommended choice) to author an ordinary spec,
proposes it as a pull request against the default branch, and, once the
independent analysis finds it consistent, merges it automatically (a spec that
changes `docs/architecture/` waits for review; its plan's Architecture Updates
explain its design decisions).
The stopped spec then depends on it like on any spec; it is implemented only after
someone signs it off with `/speckit implement` on its twin, and the stopped
implementation retries by itself when it is implemented.

`speckit.gha.route` and `speckit.gha.request` both run
`node .github/scripts/speckit-orchestrate.mjs request`, which
checks the spec on the remote default branch (merged, stage `tasked`, all
checklists checked), finds its twin issue, and posts the comment
`/speckit implement` on it with the user's own GitHub token, which records who
requested the implementation. The `Spec Kit commands` workflow validates the
request on GitHub and sets the stage label; `speckit:stage:*` labels are set only
by the GitHub bot.

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
foreach ($name in 'speckit-gha-route', 'speckit-gha-request', 'speckit-gha-diagnose', 'speckit-gha-refine', 'speckit-gha-archive', 'speckit-gha-architecture', 'speckit-gha-drift') {
    $skill = ".github/skills/$name/SKILL.md"
    $content = Get-Content -Raw ".specify/extensions/gha/.specify-dev/extension-skills/$name/SKILL.md"
    Remove-Item $skill -Force
    Set-Content -NoNewline -Path $skill -Value $content
    git rm --cached -q $skill
    git update-index --add --cacheinfo "100644,$(git hash-object -w $skill),$skill"
}
```

Then register the workflow the extension ships from its installed copy. This
generates `.specify/workflows/author/workflow.yml` and its entry in
`.specify/workflows/workflow-registry.json`; commit both with the extension:

```powershell
specify workflow add --dev .specify/extensions/gha/workflows/author
```
