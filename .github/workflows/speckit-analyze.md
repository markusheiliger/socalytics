---
name: Spec Kit analyze
description: >-
  Checks an amendment of Spec Kit artifacts, or a new spec authored by `/speckit author`, for consistency with an
  independent, read-only /speckit-analyze of the whole spec folder. Started by the amendment and new spec lifecycles
  (Spec Kit diagnose, Spec Kit author, and Spec Kit commands); its record job starts a correction round or a rework
  with pending feedback, merges a consistent new spec automatically, or hands the pull request to a person for review.
on:
  workflow_dispatch:
    inputs:
      issue:
        description: Spec twin issue number (labelled speckit:spec) of the implementation the pull request belongs to
        required: true
        type: string
      pull:
        description: Implementation pull request number
        required: true
        type: string
      folder:
        description: Spec folder name under specs/ that the pull request changes
        required: true
        type: string
      amendment:
        description: Amendment or authored spec pull request number
        required: true
        type: string
      round:
        description: Correction rounds done so far
        required: false
        type: string
        default: "0"
      stalls:
        description: Correction rounds without progress so far
        required: false
        type: string
        default: "0"
      previous:
        description: The tracked findings the last correction round fixed, as JSON (after a correction round)
        required: false
        type: string
      mode:
        description: loop (findings lead to correction rounds) or check (report only, after a person's push)
        required: false
        type: string
        default: loop
      check_run:
        description: Id of the check run that tracks this analysis
        required: true
        type: string
      kind:
        description: amend (an amendment on speckit-amend/<folder>), spec (a new spec authored by /speckit author on speckit-spec/<folder>), or fix (one on the earlier speckit-fix/<folder>)
        required: false
        type: string
        default: amend
  # Only the Spec Kit workflows dispatch this workflow, with the repository's token; dispatching already requires
  # write access.
  roles: all
run-name: "Spec Kit analyze spec #${{ inputs.issue }} · ${{ inputs.mode == 'check' && format('Check the {0} for consistency after a manual push', (inputs.kind == 'fix' || inputs.kind == 'spec') && 'new spec' || 'amendment') || inputs.round != '0' && inputs.round != '' && format('Check the {0} for consistency after round {1}', (inputs.kind == 'fix' || inputs.kind == 'spec') && 'new spec' || 'amendment', inputs.round) || format('Check the {0} for consistency', (inputs.kind == 'fix' || inputs.kind == 'spec') && 'new spec' || 'amendment') }}"
concurrency:
  group: speckit-analyze-${{ inputs.issue }}
  cancel-in-progress: false
  job-discriminator: ${{ inputs.issue }}
permissions:
  actions: read
  checks: read
  contents: read
  issues: read
  pull-requests: read
  copilot-requests: write
engine: copilot
timeout-minutes: 20
network:
  allowed:
    - defaults
    - github
tools:
  bash: [":*"]
  github:
    toolsets: [default]
steps:
  # Custom steps replace gh-aw's default checkout: the workspace is the amendment or authored spec branch.
  - name: Check out the amendment or authored spec branch
    uses: actions/checkout@v5
    with:
      ref: ${{ inputs.kind == 'spec' && format('speckit-spec/{0}', inputs.folder) || inputs.kind == 'fix' && format('speckit-fix/{0}', inputs.folder) || format('speckit-amend/{0}', inputs.folder) }}
      # Full history with all branches, so the agent can diff the pull request against its base.
      fetch-depth: 0
      persist-credentials: false
  # The trusted Spec Kit skill and scripts come from the default branch.
  - name: Check out the Spec Kit tooling
    uses: actions/checkout@v5
    with:
      ref: ${{ github.sha }}
      path: .speckit-tooling
      persist-credentials: false
  - name: Check the spec issue
    id: issue
    env:
      GITHUB_TOKEN: ${{ github.token }}
      SPECKIT_ISSUE: ${{ inputs.issue }}
    run: node .speckit-tooling/.github/scripts/speckit-issue.mjs check
  # The findings the last correction round fixed, so the analysis can say which of them are resolved.
  - name: Write the findings of the previous round
    env:
      SPECKIT_PREVIOUS: ${{ inputs.previous }}
    run: |
      mkdir -p .speckit-analysis
      printf '%s' "${SPECKIT_PREVIOUS:-[]}" > .speckit-analysis/previous.json
      printf '.speckit-tooling/\n.speckit-analysis/\n' >> .git/info/exclude
  # The spec folder moves to specs/archive/<folder> when the implementation archives it right before its merge. The
  # base the agent diffs against: the implementation branch for an amendment, the default branch for a new spec.
  - name: Locate the spec folder
    env:
      FOLDER: ${{ inputs.folder }}
      BASE: ${{ (inputs.kind == 'fix' || inputs.kind == 'spec') && github.event.repository.default_branch || format('speckit/{0}', inputs.folder) }}
    run: |
      if [ -d "specs/$FOLDER" ]; then dir="specs/$FOLDER"; else dir="specs/archive/$FOLDER"; fi
      printf '%s' "$dir" > .speckit-analysis/spec-dir
      printf '%s' "origin/$BASE" > .speckit-analysis/base
      echo "SPECIFY_FEATURE_DIRECTORY=$dir" >> "$GITHUB_ENV"
safe-outputs:
  # Spec Kit reports failures through its check runs and pull request comments.
  report-failure-as-issue: false
  jobs:
    speckit-analysis:
      description: Report the consistency analysis of the amendment or new spec. Call it exactly once.
      runs-on: ubuntu-latest
      output: "The analysis was recorded."
      # contents: write merges a consistent new spec automatically and deletes its branch.
      permissions:
        actions: write
        checks: write
        contents: write
        issues: write
        pull-requests: write
      inputs:
        report:
          description: >-
            The analysis as one JSON object: {"summary": "...", "findings": [{"severity": "CRITICAL|HIGH|MEDIUM|LOW",
            "category": "...", "location": "file and section", "summary": "...", "recommendation": "..."}], "resolved": ["F1"]}.
          required: true
          type: string
      steps:
        - name: Check out the Spec Kit tooling
          uses: actions/checkout@v5
          with:
            ref: ${{ github.sha }}
            persist-credentials: false
        - name: Set up Node.js
          uses: actions/setup-node@v5
          with:
            node-version: 24
        - name: Install the Markdown tools
          # The versions environment-setup installs; the record step checks the changed Markdown with them.
          run: npm install --global markdownlint-cli2@0.23.3 markdown-link-check@3.15.0
        - name: Record the analysis
          env:
            GITHUB_TOKEN: ${{ github.token }}
            SPECKIT_DEFAULT_BRANCH: ${{ github.event.repository.default_branch }}
            SPECKIT_MARKDOWN_GATE: "true"
            SPECKIT_AMENDMENT: ${{ inputs.amendment }}
            SPECKIT_ROUND: ${{ inputs.round }}
            SPECKIT_STALLS: ${{ inputs.stalls }}
            SPECKIT_PREVIOUS: ${{ inputs.previous }}
            SPECKIT_MODE: ${{ inputs.mode }}
            SPECKIT_CHECK_RUN: ${{ inputs.check_run }}
            # Only an analysis that passed threat detection is used.
            SPECKIT_DETECTION: ${{ needs.detection.result }}
          run: node .github/scripts/speckit-amend.mjs record-analysis
---

# Check an amendment of Spec Kit artifacts, or a new spec, for consistency

Pull request #${{ inputs.amendment }} is of kind **${{ inputs.kind }}**. Kind `amend` amends the Spec Kit artifacts of an
existing spec folder; kinds `spec` and `fix` add a new spec authored by `/speckit author`, an ordinary spec for a
cause outside the stopped spec of pull request #${{ inputs.pull }}, which depends on it. The spec folder is named in `.speckit-analysis/spec-dir`. For an amendment it is
`specs/${{ inputs.folder }}/` until the implementation archives it right before its merge, and
`specs/archive/${{ inputs.folder }}/` after that.
The workspace is the pull request's branch. Check the **whole** spec folder for consistency,
not only the changed files: the spec, the plan and its design artifacts, and the tasks must be consistent with each
other and with the constitution.

1. Follow `.speckit-tooling/.github/skills/speckit-analyze/SKILL.md` for that spec folder (set the environment
   variable `SPECIFY_FEATURE_DIRECTORY` to the content of `.speckit-analysis/spec-dir` to select it). It is strictly read-only: do not change any file.
   Skip its extension hooks and its offer of a remediation plan.
2. Also read the pull request's own changes (`git diff "$(cat .speckit-analysis/base)"...HEAD -- specs/ docs/architecture/`, or the
   pull request with the GitHub tools) and check in particular that every changed decision is reflected everywhere it
   matters: requirements and success criteria (`spec.md`), plan and risks (`plan.md`, including its
   `## Architecture Updates` section), `research.md`, `data-model.md`, `contracts/`, `quickstart.md`, checklists, and
   `tasks.md` (task text, references, order, and dependency notes). A statement that the change made stale is a
   finding. A changed design decision that `docs/architecture/` states must be covered by an unchecked documentation
   task that brings it into the owning narrative (constitution Principle I); a missing one is a finding. An amendment
   that changes `docs/architecture/` itself is a finding. A new spec (`spec` or `fix`) may change `docs/architecture/`
   for the design updates its plan's `## Architecture Updates` section applies now; every such change must match
   that section and the rest of the spec, and a change it does not explain is a finding.
   For a new spec (`spec` or `fix`), also check that it stays minimal and fixes the cause where it lives
   without weakening any requirement, test, or threshold of the feature that owns the code, and that its tasks can be
   implemented and verified on their own. It needs no special marker line: the stopped spec's dependency on it is
   recorded on GitHub once it is merged.
3. Use the severities of the skill. Report only real inconsistencies, ambiguities, coverage gaps, and constitution
   conflicts; no style preferences above LOW.
4. `.speckit-analysis/previous.json` lists the findings that the last correction round tried to fix, each with an id
   (`F1`, `F2`, …; `R…` are amendment rules, checked separately). It is `[]` before the first correction round. Judge
   each `F` finding against the current artifacts and list the ids of those that are fixed in `resolved`. A finding that
   persists, even partly or in other words, is not resolved and belongs in `findings` again. Find all issues afresh;
   do not limit the analysis to the previous findings.
5. Finish by calling `speckit_analysis` exactly once with the JSON report described in its input. An empty
   `findings` list means the folder is consistent.
