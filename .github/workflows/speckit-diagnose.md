---
description: >-
  Diagnoses why a Spec Kit implementation stopped and proposes how to continue. Started by the orchestrator when an
  implementation stops for a person, or by a `/speckit diagnose` or `/speckit revise` comment (Spec Kit commands).
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
        description: Notes from the person who started the diagnosis (optional)
        required: false
        type: string
      previous:
        description: Comment id of the previous diagnosis, for a revision (optional)
        required: false
        type: string
      check_run:
        description: Id of the check run that tracks this diagnosis
        required: true
        type: string
  # Only the Spec Kit workflows dispatch this workflow, with the repository's token; dispatching already requires
  # write access.
  roles: all
run-name: "Spec Kit diagnose #${{ inputs.twin }}"
# One diagnosis per twin at a time; diagnoses of different twins run side by side.
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
checkout:
  ref: speckit/${{ inputs.folder }}
  fetch-depth: 0
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
  jobs:
    speckit-diagnosis:
      description: >-
        Report the diagnosis of the stopped implementation. Call it exactly once, after any create_pull_request call.
      runs-on: ubuntu-latest
      needs: safe_outputs
      output: "The diagnosis was posted on the implementation pull request."
      permissions:
        checks: write
        contents: read
        issues: write
        pull-requests: write
      inputs:
        report:
          description: >-
            The diagnosis report as one JSON object with the fields category, confidence, summary, cause, evidence,
            artifacts, options, and questions, as described in the speckit-gha-diagnose skill.
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
        - name: Post the diagnosis
          env:
            GITHUB_TOKEN: ${{ github.token }}
            SPECKIT_BRANCH: ${{ github.event.repository.default_branch }}
            SPECKIT_TWIN: ${{ inputs.twin }}
            SPECKIT_PULL: ${{ inputs.pull }}
            SPECKIT_FOLDER: ${{ inputs.folder }}
            SPECKIT_CHECK_RUN: ${{ inputs.check_run }}
            # Only a report that passed threat detection is posted, and only the amendment this run created is shown.
            SPECKIT_DETECTION: ${{ needs.detection.result }}
            SPECKIT_CREATED_PULL: ${{ needs.safe_outputs.outputs.created_pr_number }}
          run: node .github/scripts/speckit-diagnose.mjs record
---

# Diagnose a stopped Spec Kit implementation

The Spec Kit implementation of spec twin #${{ inputs.twin }} stopped in pull request #${{ inputs.pull }} and needs a
person's decision. Diagnose why it stopped and propose how to continue.

Follow the skill `.speckit-tooling/.github/skills/speckit-gha-diagnose/SKILL.md` in **non-interactive mode**. It
defines the method, the categories, the rules for amendments, and the report format.

- The workspace is the implementation branch `speckit/${{ inputs.folder }}`; the spec artifacts are in
  `specs/${{ inputs.folder }}/`.
- The evidence (stop reason, check runs, recent comments, logs of failed runs, branch state, the person's notes, and a
  previous diagnosis for a revision) is in `.speckit-diagnosis/evidence.md`, the identifiers in
  `.speckit-diagnosis/context.json`. Use the GitHub tools for anything else you need to read.
- Only an amendment of the spec artifacts may leave this run, as a pull request through `create_pull_request` with
  branch `speckit-amend/${{ inputs.folder }}` and base `speckit/${{ inputs.folder }}`. Changes outside
  `specs/${{ inputs.folder }}/` are scratch work for experiments and are discarded; revert them before you create the
  pull request.
- Finish by calling `speckit_diagnosis` exactly once with the report.
