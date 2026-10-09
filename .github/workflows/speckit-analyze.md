---
name: Spec Kit analyze
description: >-
  Checks an amendment of Spec Kit artifacts for consistency with an independent, read-only /speckit-analyze of the
  whole spec folder. Started by the amendment lifecycle (Spec Kit diagnose and Spec Kit commands); its record job
  starts a correction round, a rework with pending feedback, or hands the amendment to a person for review.
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
      amendment:
        description: Amendment pull request number
        required: true
        type: string
      round:
        description: Correction rounds done so far
        required: false
        type: string
        default: "0"
      mode:
        description: loop (findings lead to correction rounds) or check (report only, after a person's push)
        required: false
        type: string
        default: loop
      check_run:
        description: Id of the check run that tracks this analysis
        required: true
        type: string
  # Only the Spec Kit workflows dispatch this workflow, with the repository's token; dispatching already requires
  # write access.
  roles: all
run-name: "Spec Kit analyze #${{ inputs.twin }}"
concurrency:
  group: speckit-analyze-${{ inputs.twin }}
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
timeout-minutes: 20
env:
  SPECIFY_FEATURE_DIRECTORY: specs/${{ inputs.folder }}
network:
  allowed:
    - defaults
    - github
tools:
  bash: [":*"]
  github:
    toolsets: [default]
steps:
  # Custom steps replace gh-aw's default checkout: the workspace is the amendment branch.
  - name: Check out the amendment branch
    uses: actions/checkout@v5
    with:
      ref: speckit-amend/${{ inputs.folder }}
      persist-credentials: false
  # The trusted Spec Kit skill and scripts come from the default branch.
  - name: Check out the Spec Kit tooling
    uses: actions/checkout@v5
    with:
      ref: ${{ github.event.repository.default_branch }}
      path: .speckit-tooling
      persist-credentials: false
safe-outputs:
  jobs:
    speckit-analysis:
      description: Report the consistency analysis of the amendment. Call it exactly once.
      runs-on: ubuntu-latest
      output: "The analysis was recorded."
      permissions:
        actions: write
        checks: write
        contents: read
        issues: write
        pull-requests: write
      inputs:
        report:
          description: >-
            The analysis as one JSON object: {"summary": "...", "findings": [{"severity": "CRITICAL|HIGH|MEDIUM|LOW",
            "category": "...", "location": "file and section", "summary": "...", "recommendation": "..."}]}.
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
        - name: Record the analysis
          env:
            GITHUB_TOKEN: ${{ github.token }}
            SPECKIT_BRANCH: ${{ github.event.repository.default_branch }}
            SPECKIT_AMENDMENT: ${{ inputs.amendment }}
            SPECKIT_ROUND: ${{ inputs.round }}
            SPECKIT_MODE: ${{ inputs.mode }}
            SPECKIT_CHECK_RUN: ${{ inputs.check_run }}
            # Only an analysis that passed threat detection is used.
            SPECKIT_DETECTION: ${{ needs.detection.result }}
          run: node .github/scripts/speckit-amend.mjs record-analysis
---

# Check an amendment of Spec Kit artifacts for consistency

Pull request #${{ inputs.amendment }} amends the Spec Kit artifacts of the spec folder
`specs/${{ inputs.folder }}/`; the workspace is the amendment branch. Check the **whole** spec folder for consistency,
not only the changed files: an amendment must leave the spec, the plan and its design artifacts, and the tasks
consistent with each other and with the constitution.

1. Follow `.speckit-tooling/.github/skills/speckit-analyze/SKILL.md` for `specs/${{ inputs.folder }}/` (the
   environment variable `SPECIFY_FEATURE_DIRECTORY` selects it). It is strictly read-only: do not change any file.
   Skip its extension hooks and its offer of a remediation plan.
2. Also read the amendment's own changes (`git diff origin/speckit/${{ inputs.folder }}...HEAD -- specs/`, or the
   pull request with the GitHub tools) and check in particular that every changed decision is reflected everywhere it
   matters: requirements and success criteria (`spec.md`), plan and risks (`plan.md`), `research.md`,
   `data-model.md`, `contracts/`, `quickstart.md`, checklists, and `tasks.md` (task text, references, order, and
   dependency notes). A statement that the amendment made stale is a finding.
3. Use the severities of the skill. Report only real inconsistencies, ambiguities, coverage gaps, and constitution
   conflicts; no style preferences above LOW.
4. Finish by calling `speckit_analysis` exactly once with the JSON report described in its input. An empty
   `findings` list means the folder is consistent.
