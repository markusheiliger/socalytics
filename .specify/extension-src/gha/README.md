# gha Spec Kit Extension

Repository-local Spec Kit extension that ships the deterministic spec authoring
workflow `author` and connects `/speckit-implement` to the `Spec Kit orchestrate` GitHub Actions
workflow. The ID is `gha` (GitHub Actions) because Spec Kit already bundles an
extension with the ID `github`.

## Commands

| Command | Skill | Purpose |
| --- | --- | --- |
| `speckit.gha.author` | `/speckit-gha-author [feature idea or folder]` | Author one spec until it is ready to implement by operating the workflow `author`: start it, ask its gates in Chat, resume it, and report (see Authoring Specs) |
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
| `author` ([`workflows/author/workflow.yml`](workflows/author/workflow.yml)) | `/speckit-gha-author [feature idea or folder]` in Copilot Chat, `specify workflow run author -i feature="…"` in a terminal (with `TZ=UTC`), or `/speckit author` on an implementation pull request (the GitHub workflow Spec Kit author runs it unattended) | Author one spec deterministically through specify, clarify, plan (repaired with `/speckit-gha-architecture`), tasks (repaired with `/speckit-gha-refine`), analyze, and the publish checks, and push the spec folder; every decision comes from `.github/scripts/speckit-author-flow.mjs`, gates ask a person, and `mode=unattended` takes every recommended choice |
| `implement-task` ([`workflows/implement-task/workflow.yml`](workflows/implement-task/workflow.yml)) | `specify workflow run implement-task -i folder=<folder> -i task=T004` | Implement one task with `/speckit-implement` and check the change with the rules of the GitHub land job (only this task's box may change in `tasks.md`, no protected paths) |
| `converge` ([`workflows/converge/workflow.yml`](workflows/converge/workflow.yml)) | `specify workflow run converge -i folder=<folder>` once every task is checked | Run `/speckit-converge` with its drift check and accept only one appended convergence section of new, unchecked tasks |
| `repair` ([`workflows/repair/workflow.yml`](workflows/repair/workflow.yml)) | `specify workflow run repair -i folder=<folder> -i tasks=T004,T005 -i scope=phase [-i evidence=<folder>]` | Repair implemented tasks whose verification failed, without touching `tasks.md` or protected paths (environment specs may change the environment actions with `-i environment=true`) |
| `resolve` ([`workflows/resolve/workflow.yml`](workflows/resolve/workflow.yml)) | `git merge <base>`, then `specify workflow run resolve -i folder=<folder> -i base=<base>` | Resolve the conflicts of a base branch merge: no conflict marker may remain, `tasks.md` keeps every task and checked box, and base branch files it had to change are reported for review |

Spec Kit extensions cannot declare workflows, so the extension carries each
workflow as a folder that its install copies to
`.specify/extensions/gha/workflows/`, and it is registered from there (see
Install or Update). On GitHub the work jobs of the implementation run the
implementation workflows through the `speckit-workflow` action (see the
workflows README, "Spec Kit workflows in the jobs"), and Spec Kit author runs
`author`. In Copilot Chat the extension command `/speckit-gha-author` operates it.

## Workflow Design

Every workflow the extension ships follows one design, so the GitHub workflows
can own the flow and the state of issues and pull requests while the Spec Kit
workflows own everything on the level of a Spec Kit command:

- **Deterministic helper:** each workflow has one helper script
  (`.github/scripts/speckit-author-flow.mjs` for `author`,
  `.github/scripts/speckit-implement-flow.mjs` for the four implementation
  workflows) whose subcommands make every decision and print one JSON object
  for the workflow's conditions. Agent steps only run Spec Kit commands
  (`command` steps) or prompts (`prompt` steps with an explicit timeout) and
  write the files the helper validates. What the helpers share lives in
  `.github/scripts/speckit-flow.mjs`.
- **Shape:** `start` validates the inputs and records the starting point, the
  agent step does the work, `check` applies the rules the GitHub land jobs
  apply (the same validators), an optional review gate asks a person, and
  `result` writes `result.json`.
- **Run folder:** `.specify/workflows/runs/<run id>/` (ignored by Git) holds the
  engine's `inputs.json` and `state.json`, the helper's state, the files agent
  steps write, `changes.diff` for the review, and `result.json`: the outcome in
  one shape for every workflow (`{ workflow, outcome, folder, files, ... }`).
- **Inputs:** every workflow takes `mode` (`interactive`, the default, or
  `unattended`) and `integration`; the implementation workflows also take
  `folder`, `guidance` (from the person who started the work), and
  `feedback` (for a revised change).
- **Gates:** only in interactive mode. Each gate is a top-level step or alone in
  a top-level `if`, names its verdict input after its id (`review-verdict` →
  `review_verdict`), and lists its recommended option first, so a run without
  a terminal pauses there and `specify workflow resume <run> -i <input>=<choice>`
  answers it. `revise` stops the run and prints the command that runs it again
  with `-i feedback="…"`.
- **No Git state changes:** the implementation workflows never commit, push, or
  check a task's box; on GitHub the land jobs do that after the verification,
  locally the person does. They measure a change against the working tree at
  `start` (a tree object written through a temporary index), so earlier
  uncommitted work and a merge in progress stay as they are. Only `author`
  commits and pushes its spec folder, when its handover says so.
- **Interactive on the client, unattended on the server:** on GitHub a run
  always has `mode=unattended`, gets an empty standard input, and is never
  resumed: it finishes or fails inside the job that started it, and a retry
  is a new run. Its run folder lives only on the runner, so a sync of the
  default branch into a pull request branch never meets a run in progress. A
  run that pauses there stopped at a gate, which is a defect of the workflow.
- **Trusted tooling:** on GitHub the definition and its helper come from the
  trusted tooling checkout, never from the checked-out branch: the job runs
  `specify workflow run "$SPECKIT_TOOLING/.specify/workflows/<id>/workflow.yml"
  -i tooling="$SPECKIT_TOOLING" …` in the workspace, and every shell step calls
  `node "{{ inputs.tooling }}/.github/scripts/<helper>.mjs"` (locally
  `tooling` is `.`). Every job of one GitHub run checks the tooling out at the
  same commit (`github.sha`), so the job that runs a workflow and the job that
  applies its `result.json` follow the same rules. The Spec Kit skills the
  agent steps run come from the workspace, whose `.github/` and `.specify/` an
  implementation cannot change.
- **Resume guard:** the engine resumes a run from its own copy of the
  definition, but the helper comes from the checkout. `start` records a hash
  of the helper (`tooling.json`), and every later subcommand but `pending`
  refuses a run whose helper changed since, for example through a merge while
  it waited at a gate: start the workflow again; the changes stay in the
  working tree. `result.json` carries `format` and that fingerprint.

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
through the extension command `/speckit-gha-author`, which runs it
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
foreach ($name in 'speckit-gha-author', 'speckit-gha-route', 'speckit-gha-request', 'speckit-gha-diagnose', 'speckit-gha-refine', 'speckit-gha-archive', 'speckit-gha-architecture', 'speckit-gha-drift') {
    $skill = ".github/skills/$name/SKILL.md"
    $content = Get-Content -Raw ".specify/extensions/gha/.specify-dev/extension-skills/$name/SKILL.md"
    Remove-Item $skill -Force
    Set-Content -NoNewline -Path $skill -Value $content
    git rm --cached -q $skill
    git update-index --add --cacheinfo "100644,$(git hash-object -w $skill),$skill"
}
```

Then register the workflows the extension ships from their installed copies.
This generates `.specify/workflows/<id>/workflow.yml` and their entries in
`.specify/workflows/workflow-registry.json`. On Windows the registry records
the source paths with backslashes, so normalize them; commit everything with the
extension (a drift test in `speckit-author-flow.test.mjs` checks that the
copies and the registry match this source):

```powershell
foreach ($id in 'author', 'implement-task', 'converge', 'repair', 'resolve') {
    specify workflow add --dev ".specify/extensions/gha/workflows/$id"
}
$registry = '.specify/workflows/workflow-registry.json'
(Get-Content -Raw $registry) -replace '\\\\', '/' | Set-Content -NoNewline $registry
```
