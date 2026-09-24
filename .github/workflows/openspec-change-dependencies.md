---
name: OpenSpec change dependency inference
description: Infer candidate dependencies between active OpenSpec changes and reconcile validated native issue blockers.
on:
  schedule: every 6h
  workflow_dispatch:
permissions:
  contents: read
  issues: read
  copilot-requests: write
strict: true
engine:
  id: copilot
network: defaults
tools:
  bash: true
safe-outputs:
  jobs:
    reconcile-openspec-dependencies:
      description: Validate inferred OpenSpec dependency candidates and reconcile managed native issue blockers.
      runs-on: ubuntu-latest
      output: OpenSpec dependency candidates were validated and reconciled.
      permissions:
        contents: read
        issues: write
      inputs:
        payload:
          description: A JSON string matching {"version":1,"candidates":[{"changeRef":"kebab-case","dependsOn":"kebab-case","confidence":0.0,"evidence":["non-empty explanation"]}]}.
          required: true
          type: string
      steps:
        - name: Check out trusted dependency reconciler
          uses: actions/checkout@v7
          with:
            ref: main
        - uses: actions/setup-node@v7
          with:
            node-version: 24
        - name: Validate and reconcile dependency candidates
          env:
            GITHUB_TOKEN: ${{ github.token }}
          run: node .github/scripts/openspec-change-dependencies.mjs "$GH_AW_AGENT_OUTPUT"
  noop:
    report-as-issue: false
  missing-tool:
    create-issue: false
  report-incomplete:
    create-issue: false
  report-failure-as-issue: false
timeout-minutes: 20
---

# OpenSpec change dependency inference

Analyze only the authoritative active changes under `openspec/changes/`.

For every active change:

1. Read its available `proposal.md`, `design.md`, `tasks.md`, and delta specs.
2. Infer only prerequisite relationships where one change cannot be correctly completed before another.
3. Do not infer an edge from thematic similarity, shared files, chronology, or convenience alone.
4. Give each candidate a confidence from 0 to 1 and concise evidence grounded in the artifacts.
5. Exclude self-edges, duplicate edges, unknown changes, and speculative relationships.

Call `reconcile-openspec-dependencies` exactly once, including when there are no
candidates. Its `payload` must be a compact JSON string with exactly this shape:

```json
{"version":1,"candidates":[{"changeRef":"dependent-change","dependsOn":"prerequisite-change","confidence":0.95,"evidence":["Artifact-grounded explanation."]}]}
```

The safe-output postprocessor independently validates identities, confidence,
evidence, duplicates, cycles, and managed-edge provenance. You cannot mutate
issues or native dependencies directly.
