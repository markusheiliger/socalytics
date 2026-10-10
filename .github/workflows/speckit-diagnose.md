---
name: Spec Kit diagnose
description: >-
  Diagnoses why a Spec Kit implementation stopped and proposes how to continue; for spec artifact defects it proposes
  an amendment pull request and reworks it after consistency findings (fix) or a person's feedback (revise). Started
  by the implementation chain (Spec Kit implement), a `/speckit diagnose` comment, and the amendment lifecycle (Spec Kit commands, Spec Kit analyze).
on:
  workflow_dispatch:
    inputs:
      twin:
        description: Spec twin issue number
        required: true
        type: string
      pull:
        description: Implementation pull request number
        required: true
        type: string
      folder:
        description: Spec folder name under specs/
        required: true
        type: string
      notes:
        description: Notes from the person who started the diagnosis, or the findings to fix (optional)
        required: false
        type: string
      previous:
        description: Comment id of the previous diagnosis (optional)
        required: false
        type: string
      check_run:
        description: Id of the check run that tracks this run
        required: true
        type: string
      mode:
        description: diagnose (a stopped implementation), fix (consistency findings), or revise (a person's feedback)
        required: false
        type: string
        default: diagnose
      amendment:
        description: Amendment pull request number (fix and revise)
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
  # Only the Spec Kit workflows dispatch this workflow, with the repository's token; dispatching already requires
  # write access.
  roles: all
run-name: "Spec Kit diagnose #${{ inputs.twin }}"
# One run per twin at a time; runs for different twins run side by side.
concurrency:
  group: speckit-diagnose-${{ inputs.twin }}
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
timeout-minutes: 30
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
  # Custom steps replace gh-aw's default checkout. A diagnosis works on the implementation branch, from which an
  # amendment pull request is generated; a rework works on the amendment branch and pushes to its pull request.
  - name: Check out the implementation or amendment branch
    uses: actions/checkout@v5
    with:
      ref: ${{ inputs.mode == 'fix' && format('speckit-amend/{0}', inputs.folder) || inputs.mode == 'revise' && format('speckit-amend/{0}', inputs.folder) || format('speckit/{0}', inputs.folder) }}
      fetch-depth: 0
      persist-credentials: false
  # The trusted tooling comes from the default branch; the implementation branch may carry older copies.
  - name: Check out the Spec Kit tooling
    uses: actions/checkout@v5
    with:
      ref: ${{ github.event.repository.default_branch }}
      path: .speckit-tooling
      persist-credentials: false
  - name: Keep the tooling and the evidence out of any amendment
    run: printf '.speckit-tooling/\n.speckit-diagnosis/\n' >> .git/info/exclude
  - name: Set up Node.js
    uses: actions/setup-node@v5
    with:
      node-version: 24
  - name: Collect the evidence
    env:
      GITHUB_TOKEN: ${{ github.token }}
      SPECKIT_BRANCH: ${{ github.event.repository.default_branch }}
      SPECKIT_TWIN: ${{ inputs.twin }}
      SPECKIT_PULL: ${{ inputs.pull }}
      SPECKIT_FOLDER: ${{ inputs.folder }}
      SPECKIT_NOTES: ${{ inputs.notes }}
      SPECKIT_PREVIOUS: ${{ inputs.previous }}
      SPECKIT_MODE: ${{ inputs.mode }}
      SPECKIT_AMENDMENT: ${{ inputs.amendment }}
      SPECKIT_ROUND: ${{ inputs.round }}
      SPECKIT_STALLS: ${{ inputs.stalls }}
      SPECKIT_EVIDENCE_DIR: .speckit-diagnosis
    run: node .speckit-tooling/.github/scripts/speckit-diagnose.mjs evidence
  # Optional, solution-owned extension point, so the agent can build and run tests to check a hypothesis.
  - name: Set up the solution environment
    continue-on-error: true
    if: hashFiles('.speckit-tooling/.github/actions/environment-setup/action.yml') != ''
    uses: ./.speckit-tooling/.github/actions/environment-setup
    with:
      workspace: .
safe-outputs:
  create-pull-request:
    title-prefix: "Amend: "
    draft: true
    allowed-files: ["specs/**"]
    # The patch is computed against this branch, so it contains only the amendment, not the implementation.
    base-branch: "speckit/${{ inputs.folder }}"
    allowed-base-branches: ["speckit/*"]
    allowed-branches: ["speckit-amend/*"]
    # Never overwrite an existing branch: a new diagnosis closes and deletes the previous amendment before it starts,
    # so only a branch this run creates can carry its amendment.
    preserve-branch-name: true
    recreate-ref: false
    fallback-as-issue: false
    auto-close-issue: false
    if-no-changes: "ignore"
    max: 1
  # Reworks push to the existing amendment pull request; the title prefix keeps them away from other pull requests.
  push-to-pull-request-branch:
    # Only the amendment pull request of this run; in diagnose mode there is none, so nothing can be pushed.
    target: ${{ inputs.amendment }}
    # The amendment's base: the allowed-files check covers the commits since it (the amendment's own), not since main.
    base-branch: speckit/${{ inputs.folder }}
    required-title-prefix: "Amend: "
    allowed-files: ["specs/**"]
    fallback-as-pull-request: false
    if-no-changes: "ignore"
    max: 1
  jobs:
    speckit-diagnosis:
      description: >-
        Report the diagnosis or the rework. Call it exactly once, after any create_pull_request or
        push_to_pull_request_branch call.
      runs-on: ubuntu-latest
      needs: safe_outputs
      output: "The result was recorded."
      permissions:
        actions: write
        checks: write
        contents: read
        issues: write
        pull-requests: write
      inputs:
        report:
          description: >-
            The report as one JSON object, as described in the speckit-gha-diagnose skill: for a diagnosis the fields
            category, confidence, summary, cause, evidence, artifacts, options, and questions; for a rework the fields
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
            SPECKIT_BRANCH: ${{ github.event.repository.default_branch }}
            SPECKIT_TWIN: ${{ inputs.twin }}
            SPECKIT_PULL: ${{ inputs.pull }}
            SPECKIT_FOLDER: ${{ inputs.folder }}
            SPECKIT_CHECK_RUN: ${{ inputs.check_run }}
            SPECKIT_MODE: ${{ inputs.mode }}
            SPECKIT_AMENDMENT: ${{ inputs.amendment }}
            SPECKIT_ROUND: ${{ inputs.round }}
            SPECKIT_STALLS: ${{ inputs.stalls }}
            SPECKIT_FINDINGS: ${{ inputs.findings }}
            SPECKIT_NOTES: ${{ inputs.notes }}
            # Only a report that passed threat detection is used, and only the amendment this run created is shown.
            SPECKIT_DETECTION: ${{ needs.detection.result }}
            SPECKIT_CREATED_PULL: ${{ needs.safe_outputs.outputs.created_pr_number }}
            SPECKIT_PUSH_SHA: ${{ needs.safe_outputs.outputs.push_commit_sha }}
            SPECKIT_PUSH_FAILURES: ${{ needs.safe_outputs.outputs.code_push_failure_count }}
          run: node .github/scripts/speckit-diagnose.mjs record
---

# Diagnose a stopped Spec Kit implementation, or rework its amendment

The Spec Kit implementation of spec twin #${{ inputs.twin }} stopped in pull request #${{ inputs.pull }}. This run's
mode is **`${{ inputs.mode }}`**.

Follow the skill `.speckit-tooling/.github/skills/speckit-gha-diagnose/SKILL.md` in **non-interactive mode**. It
defines the method, the categories, the modes, the rules for amendments, and the report formats.

- The evidence is in `.speckit-diagnosis/evidence.md`, the identifiers in `.speckit-diagnosis/context.json`. Use the
  GitHub tools for anything else you need to read. The spec artifacts are in `specs/${{ inputs.folder }}/`.
- **`diagnose`**: the workspace is the implementation branch `speckit/${{ inputs.folder }}`. Diagnose why the
  implementation stopped. Only an amendment of the spec artifacts may leave this run, as a pull request through
  `create_pull_request` with branch `speckit-amend/${{ inputs.folder }}` and base `speckit/${{ inputs.folder }}`.
- **`fix`**: the workspace is the amendment branch `speckit-amend/${{ inputs.folder }}` of pull request
  #${{ inputs.amendment }}. Fix the consistency findings listed in the evidence (correction round ${{ inputs.round }}),
  commit, and push with `push_to_pull_request_branch` for pull request #${{ inputs.amendment }}.
- **`revise`**: the workspace is the amendment branch of pull request #${{ inputs.amendment }}. Address every piece
  of feedback listed in the evidence, commit, and push with `push_to_pull_request_branch` for pull request
  #${{ inputs.amendment }}.
- Changes outside `specs/${{ inputs.folder }}/` are scratch work for experiments and are discarded; revert them before
  you create or push anything.
- Finish by calling `speckit_diagnosis` exactly once with the report.
