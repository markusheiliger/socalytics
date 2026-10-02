---
name: OpenSpec prepare
description: Synchronize OpenSpec issue twins and incrementally reconcile validated native issue dependencies.
on:
  push:
    branches: [main]
    paths:
      - "openspec/changes/**"
      - ".github/scripts/openspec-change-core.mjs"
      - ".github/scripts/openspec-change-git-notes.mjs"
      - ".github/scripts/openspec-change-github.mjs"
      - ".github/scripts/openspec-change-sync.mjs"
      - ".github/scripts/openspec-change-dependencies.mjs"
      - ".github/scripts/openspec-change-reconciliation.mjs"
      - ".github/workflows/openspec-prepare.md"
  schedule:
    - cron: "weekly on monday"
  workflow_dispatch:
    inputs:
      mode:
        description: Reconciliation mode
        required: true
        default: auto
        type: choice
        options:
          - auto
          - full
          - dry-run
permissions:
  contents: read
  issues: read
  copilot-requests: write
concurrency:
  group: openspec-prepare
  queue: max
  job-discriminator: ${{ github.run_id }}
strict: true
engine:
  id: copilot
network: defaults
tools:
  bash: true
jobs:
  synchronize:
    name: Synchronize issue twins and prepare dependency context
    runs-on: ubuntu-latest
    timeout-minutes: 15
    permissions:
      contents: read
      issues: write
    outputs:
      should_infer: ${{ steps.decision.outputs.should_infer }}
      dependency_mode: ${{ steps.prepare.outputs.dependency_mode }}
      evaluated_refs: ${{ steps.prepare.outputs.evaluated_refs }}
      target_head: ${{ steps.prepare.outputs.target_head }}
    steps:
      - name: Check out trusted reconciliation source
        uses: actions/checkout@v7
        with:
          ref: main
          fetch-depth: 0
      - uses: actions/setup-node@v7
        with:
          node-version: 24
      - name: Install OpenSpec CLI
        run: npm install --global @fission-ai/openspec@1.13.0
      - name: Synchronize issue twins
        env:
          GITHUB_TOKEN: ${{ github.token }}
          DRY_RUN: ${{ github.event_name == 'workflow_dispatch' && inputs.mode == 'dry-run' }}
        run: |
          if [[ "$DRY_RUN" == "true" ]]; then
            node .github/scripts/openspec-change-sync.mjs --dry-run
          else
            node .github/scripts/openspec-change-sync.mjs
          fi
      - name: Prepare dependency reconciliation
        id: prepare
        env:
          GH_TOKEN: ${{ github.token }}
          DRY_RUN: ${{ github.event_name == 'workflow_dispatch' && inputs.mode == 'dry-run' }}
          REQUESTED_MODE: ${{ github.event_name == 'schedule' && 'full' || (github.event_name == 'workflow_dispatch' && inputs.mode == 'full' && 'full' || 'auto') }}
        run: |
          authorization="$(printf 'x-access-token:%s' "$GH_TOKEN" | base64 -w 0)"
          export GIT_CONFIG_COUNT=1
          export GIT_CONFIG_KEY_0=http.https://github.com/.extraheader
          export GIT_CONFIG_VALUE_0="AUTHORIZATION: basic $authorization"
          args=(prepare --context "$RUNNER_TEMP/openspec-change-reconciliation/agent-context.json" --mode "$REQUESTED_MODE")
          if [[ "$DRY_RUN" == "true" ]]; then
            args+=(--dry-run)
          fi
          node .github/scripts/openspec-change-reconciliation.mjs "${args[@]}"
      - name: Select inference execution
        id: decision
        env:
          DRY_RUN: ${{ github.event_name == 'workflow_dispatch' && inputs.mode == 'dry-run' }}
          EVALUATED_REFS: ${{ steps.prepare.outputs.evaluated_refs }}
        run: |
          if [[ "$DRY_RUN" == "true" || "$EVALUATED_REFS" == "[]" || -z "$EVALUATED_REFS" ]]; then
            echo "should_infer=false" >> "$GITHUB_OUTPUT"
          else
            echo "should_infer=true" >> "$GITHUB_OUTPUT"
          fi
      - name: Upload dependency context
        if: steps.decision.outputs.should_infer == 'true'
        uses: actions/upload-artifact@v7
        with:
          name: openspec-change-reconciliation-context
          path: ${{ runner.temp }}/openspec-change-reconciliation/agent-context.json
          if-no-files-found: error
          retention-days: 1
  agent:
    needs: [synchronize]
    if: needs.synchronize.outputs.should_infer == 'true'
pre-agent-steps:
  - name: Download prepared dependency context
    uses: actions/download-artifact@v8
    with:
      name: openspec-change-reconciliation-context
      path: ${{ runner.temp }}/openspec-change-reconciliation
  - name: Check out the prepared repository commit
    env:
      GH_TOKEN: ${{ github.token }}
      CONTEXT_PATH: ${{ runner.temp }}/openspec-change-reconciliation/agent-context.json
    run: |
      authorization="$(printf 'x-access-token:%s' "$GH_TOKEN" | base64 -w 0)"
      export GIT_CONFIG_COUNT=1
      export GIT_CONFIG_KEY_0=http.https://github.com/.extraheader
      export GIT_CONFIG_VALUE_0="AUTHORIZATION: basic $authorization"
      target_head="$(node -e "const fs=require('fs'); console.log(JSON.parse(fs.readFileSync(process.env.CONTEXT_PATH,'utf8')).targetHead)")"
      git fetch --no-tags origin "$target_head"
      git checkout --detach "$target_head"
safe-outputs:
  jobs:
    reconcile-openspec-dependencies:
      name: Reconcile dependencies and persist the checkpoint
      description: Validate an incremental OpenSpec dependency patch, reconcile native blockers, and persist the Git-note checkpoint.
      runs-on: ubuntu-latest
      output: OpenSpec issue twins and dependency state were reconciled.
      permissions:
        contents: write
        issues: write
      inputs:
        payload:
          description: A compact JSON string whose $schema is .github/scripts/schemas/dependency-graph-patch-v2.schema.json and whose evaluation scope matches the prepared context.
          required: true
          type: string
      steps:
        - name: Download prepared dependency context
          uses: actions/download-artifact@v8
          with:
            name: openspec-change-reconciliation-context
            path: ${{ runner.temp }}/openspec-change-reconciliation
        - name: Read prepared target commit
          id: target
          env:
            CONTEXT_PATH: ${{ runner.temp }}/openspec-change-reconciliation/agent-context.json
          run: |
            target_head="$(node -e "const fs=require('fs'); console.log(JSON.parse(fs.readFileSync(process.env.CONTEXT_PATH,'utf8')).targetHead)")"
            echo "target_head=$target_head" >> "$GITHUB_OUTPUT"
        - name: Check out trusted dependency reconciler
          uses: actions/checkout@v7
          with:
            ref: ${{ steps.target.outputs.target_head }}
            fetch-depth: 0
        - uses: actions/setup-node@v7
          with:
            node-version: 24
        - name: Validate, reconcile, and checkpoint dependency state
          env:
            GH_TOKEN: ${{ github.token }}
            GITHUB_TOKEN: ${{ github.token }}
            CONTEXT_PATH: ${{ runner.temp }}/openspec-change-reconciliation/agent-context.json
          run: |
            authorization="$(printf 'x-access-token:%s' "$GH_TOKEN" | base64 -w 0)"
            export GIT_CONFIG_COUNT=1
            export GIT_CONFIG_KEY_0=http.https://github.com/.extraheader
            export GIT_CONFIG_VALUE_0="AUTHORIZATION: basic $authorization"
            node .github/scripts/openspec-change-reconciliation.mjs reconcile \
              --context "$CONTEXT_PATH" \
              --safe-output "$GH_AW_AGENT_OUTPUT"
  noop:
    report-as-issue: false
  missing-tool:
    create-issue: false
  report-incomplete:
    create-issue: false
  report-failure-as-issue: false
timeout-minutes: 20
---

# OpenSpec prepare

Read the prepared dependency context at
`$RUNNER_TEMP/openspec-change-reconciliation/agent-context.json`. The trusted
job has already synchronized issue twins and selected the exact `main` commit,
evaluation mode, and evaluated change refs.

For every evaluated change:

1. Read its full available `proposal.md`, `design.md`, `tasks.md`, and delta
   specifications listed in the context.
2. Use cached summaries for unchanged changes to identify possible
   relationships. Read an unchanged change's full artifacts before proposing an
   edge involving it when its cached summary is insufficient.
3. Infer only prerequisites where one change cannot be completed correctly
   before another. Do not infer an edge from thematic similarity, shared files,
   chronology, or convenience.
4. Reevaluate existing managed edges incident to evaluated changes in both
   directions.
5. Provide a concise updated dependency-oriented summary for every evaluated
   ref. State provided capabilities, required capabilities, affected
   boundaries, and explicit non-dependencies relevant to future inference.
6. Emit explicit `upsert` operations for accepted edges and explicit `remove`
   operations only when artifact evidence proves a previously managed edge is
   obsolete.

Call `reconcile-openspec-dependencies` exactly once. Its `payload` must be a
compact JSON string with exactly this shape:

```json
{"$schema":".github/scripts/schemas/dependency-graph-patch-v2.schema.json","evaluationMode":"incremental","evaluatedRefs":["dependent-change"],"summaries":[{"ref":"dependent-change","summary":"Compact dependency-oriented summary."}],"upsert":[{"changeRef":"dependent-change","dependsOn":"prerequisite-change","confidence":0.95,"evidence":["Artifact-grounded explanation."]}],"remove":[{"changeRef":"dependent-change","dependsOn":"obsolete-prerequisite","evidence":["Artifact-grounded explanation for removal."]}]}
```

Use the exact `evaluationMode` and `evaluatedRefs` from the prepared context.
For `full` mode, evaluate every active change. Exclude self-edges, duplicate
operations, unknown changes, and speculative relationships.

The safe-output postprocessor independently validates the evaluation scope,
summaries, identities, confidence, evidence, managed-edge provenance, manual
edge preservation, and complete-graph acyclicity. You cannot mutate issues,
native dependencies, Git notes, or repository content directly.
