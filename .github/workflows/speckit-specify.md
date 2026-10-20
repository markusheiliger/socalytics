---
name: Spec Kit specify
description: >-
  Authors a new spec with the Spec Kit authoring flow (/speckit-gha-author) for the cause of a stopped Spec Kit
  implementation that lies outside its spec (code or tests of another feature on the base branch, or the environment
  actions), and proposes it as a pull request against the default branch; the stopped spec depends on it once it is
  merged. Reworks it after consistency findings (fix) or a person's feedback (revise). Started by `/speckit specify`
  (Spec Kit commands) and by the spec pull request lifecycle (Spec Kit analyze, Spec Kit commands).
on:
  workflow_dispatch:
    inputs:
      twin:
        description: Spec twin issue number of the stopped implementation
        required: true
        type: string
      pull:
        description: Implementation pull request number of the stopped implementation
        required: true
        type: string
      folder:
        description: author — the stopped spec's folder under specs/; fix and revise — the new spec's folder
        required: true
        type: string
      guidance:
        description: The guidance of the person who asked for the new spec (author), the findings to fix (fix), or the feedback (revise)
        required: false
        type: string
      check_run:
        description: Id of the check run that tracks this run
        required: true
        type: string
      mode:
        description: author (write a new spec), fix (consistency findings), or revise (a person's feedback)
        required: false
        type: string
        default: author
      amendment:
        description: Spec pull request number (fix and revise)
        required: false
        type: string
      branch:
        description: The spec pull request's branch, speckit-spec/<folder> (fix and revise; empty for the earlier speckit-fix/<folder>)
        required: false
        type: string
      round:
        description: Correction round (fix)
        required: false
        type: string
        default: "0"
      stalls:
        description: Correction rounds without progress so far (fix)
        required: false
        type: string
        default: "0"
      findings:
        description: The tracked findings this correction round fixes, as JSON (fix)
        required: false
        type: string
      stamp:
        description: UTC time stamp (YYYYMMDD-HHMMSS) that starts the new spec's folder name (author)
        required: false
        type: string
  # Only the Spec Kit workflows dispatch this workflow, with the repository's token; dispatching already requires
  # write access.
  roles: all
run-name: "Spec Kit specify spec #${{ inputs.twin }} · ${{ inputs.mode == 'fix' && format('Fix consistency findings of the new spec, round {0}', inputs.round) || inputs.mode == 'revise' && 'Revise the new spec with review feedback' || 'Write a new spec that unblocks the implementation' }}"
# One run per twin at a time; runs for different twins run side by side.
concurrency:
  group: speckit-specify-${{ inputs.twin }}
  cancel-in-progress: false
  job-discriminator: ${{ inputs.twin }}
permissions:
  actions: read
  checks: read
  contents: read
  issues: read
  pull-requests: read
  copilot-requests: write
engine: copilot
timeout-minutes: 60
network:
  allowed:
    - defaults
    - github
    - dotnet
    - node
    - python
tools:
  edit:
  bash: [":*"]
  github:
    toolsets: [default, actions]
steps:
  # Custom steps replace gh-aw's default checkout. Authoring starts from the default branch, where the new spec is
  # proposed; a rework works on the spec branch and pushes to its pull request.
  - name: Check out the default branch or the spec branch
    uses: actions/checkout@v5
    with:
      ref: ${{ (inputs.mode == 'fix' || inputs.mode == 'revise') && inputs.branch != '' && inputs.branch || (inputs.mode == 'fix' || inputs.mode == 'revise') && format('speckit-fix/{0}', inputs.folder) || github.event.repository.default_branch }}
      fetch-depth: 0
      persist-credentials: false
  # The trusted tooling comes from the default branch.
  - name: Check out the Spec Kit tooling
    uses: actions/checkout@v5
    with:
      ref: ${{ github.event.repository.default_branch }}
      path: .speckit-tooling
      persist-credentials: false
  - name: Keep the tooling and the evidence out of the spec
    run: printf '.speckit-tooling/\n.speckit-fix/\n' >> .git/info/exclude
  - name: Set up Node.js
    uses: actions/setup-node@v5
    with:
      node-version: 24
  # The Spec Kit helper scripts (.specify/scripts/python) run as `python`.
  - name: Set up Python
    uses: actions/setup-python@v5
    with:
      python-version: "3.12"
  - name: Collect the evidence
    env:
      GITHUB_TOKEN: ${{ github.token }}
      SPECKIT_DEFAULT_BRANCH: ${{ github.event.repository.default_branch }}
      SPECKIT_TWIN: ${{ inputs.twin }}
      SPECKIT_PULL: ${{ inputs.pull }}
      SPECKIT_FOLDER: ${{ inputs.folder }}
      SPECKIT_GUIDANCE: ${{ inputs.guidance }}
      SPECKIT_MODE: ${{ inputs.mode }}
      SPECKIT_AMENDMENT: ${{ inputs.amendment }}
      SPECKIT_ROUND: ${{ inputs.round }}
      SPECKIT_STALLS: ${{ inputs.stalls }}
      SPECKIT_STAMP: ${{ inputs.stamp }}
      SPECKIT_EVIDENCE_DIR: .speckit-fix
    run: node .speckit-tooling/.github/scripts/speckit-fix.mjs evidence
  # Optional, solution-owned extension point, so the agent can build and run tests to check the cause.
  - name: Set up the solution environment
    continue-on-error: true
    if: hashFiles('.speckit-tooling/.github/actions/environment-setup/action.yml') != ''
    uses: ./.speckit-tooling/.github/actions/environment-setup
    with:
      workspace: .
safe-outputs:
  # Spec Kit reports failures through its check runs and pull request comments.
  report-failure-as-issue: false
  create-pull-request:
    title-prefix: "Spec: "
    draft: true
    allowed-files: ["specs/**", "docs/architecture/**"]
    base-branch: ${{ github.event.repository.default_branch }}
    allowed-branches: ["speckit-spec/*"]
    # Never overwrite an existing branch: only a branch this run creates can carry its new spec.
    preserve-branch-name: true
    recreate-ref: false
    fallback-as-issue: false
    auto-close-issue: false
    if-no-changes: "ignore"
    max: 1
  # Reworks push to the existing spec pull request; the title prefix keeps them away from other pull requests.
  push-to-pull-request-branch:
    # Only the spec pull request of this run; when authoring there is none, so nothing can be pushed.
    target: ${{ inputs.amendment }}
    base-branch: ${{ github.event.repository.default_branch }}
    required-title-prefix: "Spec: "
    allowed-files: ["specs/**", "docs/architecture/**"]
    fallback-as-pull-request: false
    if-no-changes: "ignore"
    max: 1
  jobs:
    speckit-fix:
      description: >-
        Report the result. Call it exactly once, after any create_pull_request or push_to_pull_request_branch call.
      runs-on: ubuntu-latest
      needs: safe_outputs
      output: "The result was recorded."
      permissions:
        actions: write
        checks: write
        contents: write
        issues: write
        pull-requests: write
      inputs:
        report:
          description: >-
            The report as one JSON object. When authoring: folder (the new spec folder, empty when none was written),
            summary, refused (why no spec was written, empty otherwise), reuse (the pull request number of an existing
            spec that already removes the cause, empty otherwise), and designDecisions. For a rework: summary,
            artifacts ([{"file", "reason"}]), responses (how each finding or piece of feedback was handled), and
            designDecisions. designDecisions is a list of {"decision", "why", "files"} for every architecture decision
            of the spec; it is required whenever the spec changes docs/architecture/, and in a rework it replaces the
            list reported before.
          required: true
          type: string
      steps:
        - name: Check out the Spec Kit tooling
          uses: actions/checkout@v5
          with:
            ref: ${{ github.event.repository.default_branch }}
            persist-credentials: false
        - name: Set up Node.js
          uses: actions/setup-node@v5
          with:
            node-version: 24
        - name: Record the result
          env:
            GITHUB_TOKEN: ${{ github.token }}
            SPECKIT_DEFAULT_BRANCH: ${{ github.event.repository.default_branch }}
            SPECKIT_TWIN: ${{ inputs.twin }}
            SPECKIT_PULL: ${{ inputs.pull }}
            SPECKIT_FOLDER: ${{ inputs.folder }}
            SPECKIT_CHECK_RUN: ${{ inputs.check_run }}
            SPECKIT_MODE: ${{ inputs.mode }}
            SPECKIT_AMENDMENT: ${{ inputs.amendment }}
            SPECKIT_ROUND: ${{ inputs.round }}
            SPECKIT_STALLS: ${{ inputs.stalls }}
            SPECKIT_FINDINGS: ${{ inputs.findings }}
            SPECKIT_GUIDANCE: ${{ inputs.guidance }}
            # Only a result that passed threat detection is used, and only the pull request this run created is shown.
            SPECKIT_DETECTION: ${{ needs.detection.result }}
            SPECKIT_CREATED_PULL: ${{ needs.safe_outputs.outputs.created_pr_number }}
            SPECKIT_PUSH_SHA: ${{ needs.safe_outputs.outputs.push_commit_sha }}
            SPECKIT_PUSH_FAILURES: ${{ needs.safe_outputs.outputs.code_push_failure_count }}
          run: node .github/scripts/speckit-fix.mjs record
---

# Author or rework a spec for the cause of a stopped Spec Kit implementation

The Spec Kit implementation of spec twin #${{ inputs.twin }} stopped in pull request #${{ inputs.pull }}, and its
diagnosis found the cause outside that spec. This run's mode is **`${{ inputs.mode }}`**.

You author the spec with the repository's Spec Kit authoring flow: load `.github/skills/speckit-gha-author/SKILL.md`
and follow it **non-interactively** (its server mode, which works like autopilot): never ask, take every recommended
(or suggested) choice, and record each answered decision as the skills prescribe (for example clarification answers
in the `## Clarifications` section of `spec.md`), so a person can review every autonomous choice in the artifacts.
Every Spec Kit step runs through its own skill under `.github/skills/`, including their mandatory hooks.

- The evidence is in `.speckit-fix/evidence.md`, the identifiers in `.speckit-fix/context.json`. Use the GitHub tools
  for anything else you need to read. Do not use `gh`, `git push`, or any other way to publish than the tools below.
- Run the Spec Kit helper scripts with UTC time (`TZ=UTC python …`).
- Changes outside the spec folder and `docs/architecture/` are scratch work for experiments and are discarded; revert
  them before you create or push anything. `specs/README.md` lists no specs.

## Mode `author`

The workspace is the default branch.

1. **Decide first.** Read the evidence: the stop, the latest diagnosis, the guidance of the person who asked, and the
   existing specs it lists.
   - **`reuse`**: one cause gets one spec. When one of the listed specs (open, or merged and not implemented yet)
     already removes the cause, write nothing and report its pull request number in `reuse`.
   - **`refused`**: when no spec can remove the cause (for example an outage, missing permissions or quotas, or a
     defect of the stopped spec itself), change nothing and report why in `refused`.
2. **Otherwise author exactly one spec** with `/speckit-gha-author`, given a feature description that you derive from
   the evidence: what has to change, where it lives (the code, tests, or environment actions of the feature that owns
   it), why the stopped spec needs it, and the guidance. State in it that the spec named `prerequisiteOf` in
   `context.json` depends on this spec. The new folder must be named `<folderPrefix><short-name>` with
   `folderPrefix` from `context.json`. Carry the spec through steps 1 to 6 of the skill until
   `node .github/scripts/speckit-author.mjs status <folder>` reports no publish blockers.
3. **Instead of step 7 (commit and push)**: commit the spec folder (and any `docs/architecture/` changes its
   `after_plan` hook made) and call `create_pull_request` on the branch `<branchPrefix><folder>` (`branchPrefix` from
   `context.json`), with the spec's title as the title (the tool adds the prefix `titlePrefix`) and a short description
   of the cause and the spec.
4. **Instead of step 8 (report)**: call `speckit_fix` exactly once with the author report. List every architecture
   decision of the spec in `designDecisions` (`decision`, `why`, `files`); it is required whenever the spec changes
   `docs/architecture/`, and a person reviews those decisions before the spec is merged.

## Mode `fix`

The workspace is the spec's pull request branch (`branch` in `context.json`) of pull request #${{ inputs.amendment }}.
Fix the consistency findings listed in the evidence (correction round ${{ inputs.round }}):
continue the folder with `/speckit-gha-author <folder>` and the Spec Kit skills it names, so every change comes from
the skill that owns the artifact. Commit, push with `push_to_pull_request_branch` for pull request #${{ inputs.amendment }},
and call `speckit_fix` exactly once with the rework report.

## Mode `revise`

The workspace is the spec's pull request branch of pull request #${{ inputs.amendment }}. Address every piece of
feedback listed in the evidence through `/speckit-gha-author <folder>` and the Spec Kit skills, commit, push with
`push_to_pull_request_branch` for pull request #${{ inputs.amendment }}, and call `speckit_fix` exactly once with the
rework report.

In a rework, report `designDecisions` again whenever the spec changes `docs/architecture/` (the full current list; it
replaces the earlier one).
