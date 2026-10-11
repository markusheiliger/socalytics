# gha Spec Kit Extension

Repository-local Spec Kit extension that connects `/speckit-implement` to the
`Spec Kit orchestrate` GitHub Actions workflow. The ID is `gha` (GitHub Actions)
because Spec Kit already bundles an extension with the ID `github`.

## Commands

| Command | Skill | Purpose |
| --- | --- | --- |
| `speckit.gha.route` | `/speckit-gha-route` | Mandatory `before_implement` hook: choose local implementation or a GitHub request |
| `speckit.gha.request` | `/speckit-gha-request [folder]` | Request implementation on GitHub directly |
| `speckit.gha.diagnose` | `/speckit-gha-diagnose [pull request, twin, or folder] [notes]` | Diagnose why an implementation on GitHub stopped and decide how to continue; amend the spec artifacts only when they are the cause |
| `speckit.gha.refine` | `/speckit-gha-refine [folder]` | Mandatory `after_tasks` hook: refine the `[P]` groups of the generated `tasks.md` so parallel tasks never change the same files |

## Refining Parallel Groups

`/speckit-tasks` sets `[P]` from the task texts alone, which miss shared edits
such as service registrations or project files. The implementation workflow runs
consecutive `[P]` tasks under one heading as one parallel stage, so
`/speckit-gha-refine` checks each group against the files its tasks will really
change and fixes overlaps: it names the shared file, moves a small shared edit
into one task after the group, or ends the group. For a fresh `tasks.md` it also
forms new groups, separates groups that follow each other with `###` headings,
and renumbers the tasks. For a spec already being implemented it only fixes
conflicting groups, without moving or renumbering tasks.

`node .github/scripts/speckit-tasks-groups.mjs <folder | tasks.md> [--json]`
prints the stages exactly as the workflow forms them, with warnings for headings
that do not separate groups. It is read-only.

`/speckit-gha-diagnose` is also the method of the `Spec Kit diagnose` agentic
workflow, which runs it non-interactively when an implementation stops or a
person comments `/speckit diagnose` on the pull request, and to rework an
amendment pull request after consistency findings (`fix`) or a person's
feedback (`revise`). Locally it runs interactively and asks before it changes
anything.

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
foreach ($name in 'speckit-gha-route', 'speckit-gha-request', 'speckit-gha-diagnose', 'speckit-gha-refine') {
    $skill = ".github/skills/$name/SKILL.md"
    $content = Get-Content -Raw ".specify/extensions/gha/.specify-dev/extension-skills/$name/SKILL.md"
    Remove-Item $skill -Force
    Set-Content -NoNewline -Path $skill -Value $content
    git rm --cached -q $skill
    git update-index --add --cacheinfo "100644,$(git hash-object -w $skill),$skill"
}
```
