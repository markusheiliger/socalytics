---
name: Spec Kit fix
description: >-
  Authors a fix spec for the cause of a stopped Spec Kit implementation that lies outside its spec (code or tests of
  another feature on the base branch, or the environment actions) and proposes it as a pull request against the
  default branch; reworks it after consistency findings (fix) or a person's feedback (revise). Started by
  `/speckit fix` (Spec Kit commands) and by the fix spec lifecycle (Spec Kit analyze, Spec Kit commands).
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
        description: author — the stopped spec's folder under specs/; fix and revise — the fix spec's folder
        required: true
        type: string
      guidance:
        description: The guidance of the person who asked for the fix spec (author), the findings to fix (fix), or the feedback (revise)
        required: false
        type: string
      check_run:
        description: Id of the check run that tracks this run
        required: true
        type: string
      mode:
        description: author (write a new fix spec), fix (consistency findings), or revise (a person's feedback)
        required: false
        type: string
        default: author
      amendment:
        description: Fix spec pull request number (fix and revise)
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
        description: UTC time stamp (YYYYMMDD-HHMMSS) that starts the new fix spec's folder name (author)
        required: false
        type: string
  # Only the Spec Kit workflows dispatch this workflow, with the repository's token; dispatching already requires
  # write access.
  roles: all
run-name: "Spec Kit fix spec #${{ inputs.twin }} · ${{ inputs.mode == 'fix' && format('Fix consistency findings of the fix spec, round {0}', inputs.round) || inputs.mode == 'revise' && 'Revise the fix spec with review feedback' || 'Write a fix spec that unblocks the implementation' }}"
# One run per twin at a time; runs for different twins run side by side.
concurrency:
  group: speckit-fix-${{ inputs.twin }}
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
timeout-minutes: 40
network:
  allowed:
    - defaults
    - github
    - dotnet
    - node
tools:
  edit:
  bash: [":*"]
  github:
    toolsets: [default, actions]
steps:
  # Custom steps replace gh-aw's default checkout. Authoring starts from the default branch, where the fix spec is
  # proposed; a rework works on the fix spec branch and pushes to its pull request.
  - name: Check out the default branch or the fix spec branch
    uses: actions/checkout@v5
    with:
      ref: ${{ inputs.mode == 'fix' && format('speckit-fix/{0}', inputs.folder) || inputs.mode == 'revise' && format('speckit-fix/{0}', inputs.folder) || github.event.repository.default_branch }}
      fetch-depth: 0
      persist-credentials: false
  # The trusted tooling comes from the default branch.
  - name: Check out the Spec Kit tooling
    uses: actions/checkout@v5
    with:
      ref: ${{ github.event.repository.default_branch }}
      path: .speckit-tooling
      persist-credentials: false
  - name: Keep the tooling and the evidence out of the fix spec
    run: printf '.speckit-tooling/\n.speckit-fix/\n' >> .git/info/exclude
  - name: Set up Node.js
    uses: actions/setup-node@v5
    with:
      node-version: 24
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
    title-prefix: "Fix spec: "
    draft: true
    allowed-files: ["specs/**"]
    base-branch: ${{ github.event.repository.default_branch }}
    allowed-branches: ["speckit-fix/*"]
    # Never overwrite an existing branch: only a branch this run creates can carry its fix spec.
    preserve-branch-name: true
    recreate-ref: false
    fallback-as-issue: false
    auto-close-issue: false
    if-no-changes: "ignore"
    max: 1
  # Reworks push to the existing fix spec pull request; the title prefix keeps them away from other pull requests.
  push-to-pull-request-branch:
    # Only the fix spec pull request of this run; when authoring there is none, so nothing can be pushed.
    target: ${{ inputs.amendment }}
    base-branch: ${{ github.event.repository.default_branch }}
    required-title-prefix: "Fix spec: "
    allowed-files: ["specs/**"]
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
            The report as one JSON object, as described in the speckit-gha-fix skill: when authoring, the fields
            folder, summary, refused (why no fix spec was written, empty otherwise), and reuse (the pull request
            number of an existing fix spec that already removes the cause, empty otherwise); for a rework the fields
            summary, artifacts, and responses.
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

# Author or rework a fix spec for a stopped Spec Kit implementation

The Spec Kit implementation of spec twin #${{ inputs.twin }} stopped in pull request #${{ inputs.pull }}, and its
diagnosis found the cause outside that spec. This run's mode is **`${{ inputs.mode }}`**.

Follow the skill `.speckit-tooling/.github/skills/speckit-gha-fix/SKILL.md` in **non-interactive mode**. It defines
what a fix spec is, its rules, and the report formats.

- The evidence is in `.speckit-fix/evidence.md`, the identifiers in `.speckit-fix/context.json`. Use the GitHub tools
  for anything else you need to read.
- **`author`**: the workspace is the default branch. One cause gets one fix spec: when one of the existing fix specs
  listed in the evidence already removes the cause, write nothing and report its pull request number in `reuse`.
  Otherwise write one new fix spec folder named
  `<folderPrefix><short-name>` (from `context.json`) under `specs/`, add its row to `specs/README.md`, and propose it
  with `create_pull_request` on a branch `speckit-fix/<folder>`. When the cause cannot be fixed by a fix spec, change
  nothing and report why in `refused`.
- **`fix`**: the workspace is the fix spec branch `speckit-fix/${{ inputs.folder }}` of pull request
  #${{ inputs.amendment }}. Fix the consistency findings listed in the evidence (correction round ${{ inputs.round }}),
  commit, and push with `push_to_pull_request_branch` for pull request #${{ inputs.amendment }}.
- **`revise`**: the workspace is the fix spec branch of pull request #${{ inputs.amendment }}. Address every piece of
  feedback listed in the evidence, commit, and push with `push_to_pull_request_branch` for pull request
  #${{ inputs.amendment }}.
- Changes outside the fix spec folder and `specs/README.md` are scratch work for experiments and are discarded;
  revert them before you create or push anything.
- Finish by calling `speckit_fix` exactly once with the report.
