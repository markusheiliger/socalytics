---
name: OpenSpec agent
description: Run one OpenSpec agent session (apply task, verify, sync, or archive) that the OpenSpec orchestrator dispatched.
run-name: "OpenSpec agent · #${{ inputs.pr }} ${{ inputs.branch }} · ${{ inputs.step || 'session' }} · ${{ inputs.dispatch_id }}"
on:
  workflow_dispatch:
    inputs:
      pr:
        description: OpenSpec pull request number
        required: true
        type: string
      dispatch_id:
        description: Dispatch id recorded in the pull request's OpenSpec lifecycle state
        required: true
        type: string
      branch:
        description: Pull request branch, shown in the run title only
        required: false
        type: string
      step:
        description: Operation and task, for example "apply 2.3", shown in the run title only
        required: false
        type: string
permissions:
  contents: read
  pull-requests: read
  checks: read
  copilot-requests: write
concurrency:
  group: openspec-agent-${{ inputs.pr }}
  job-discriminator: ${{ github.run_id }}
checkout:
  fetch-depth: 0
  fetch: ["*"]
strict: true
engine:
  id: copilot
imports:
  # Repository-specific toolchain, network access, and the run_verification tool.
  - shared/repository-toolchain.md
network:
  allowed:
    - defaults
    - node
tools:
  bash: true
  edit:
  timeout: 900
timeout-minutes: 60
pre-agent-steps:
  - name: Set up Node.js and the OpenSpec CLI
    uses: ./.github/actions/setup-openspec
  - name: Build the session prompt from the pull request state
    id: prepare
    env:
      GITHUB_TOKEN: ${{ github.token }}
      PR: ${{ inputs.pr }}
      DISPATCH_ID: ${{ inputs.dispatch_id }}
    run: |
      mkdir -p /tmp/gh-aw/openspec
      node .github/scripts/openspec-change-orchestrator.mjs agent-prompt \
        --pr "$PR" --dispatch-id "$DISPATCH_ID" --out /tmp/gh-aw/openspec/prompt.md
  - name: Check out the pull request branch at the dispatched commit
    env:
      GH_TOKEN: ${{ github.token }}
      BRANCH: ${{ fromJSON(steps.prepare.outputs.branch) }}
      HEAD_SHA: ${{ fromJSON(steps.prepare.outputs.head_sha) }}
    run: |
      authorization="$(printf 'x-access-token:%s' "$GH_TOKEN" | base64 -w 0)"
      export GIT_CONFIG_COUNT=1
      export GIT_CONFIG_KEY_0=http.https://github.com/.extraheader
      export GIT_CONFIG_VALUE_0="AUTHORIZATION: basic $authorization"
      git fetch --no-tags origin "$HEAD_SHA"
      git checkout -B "$BRANCH" "$HEAD_SHA"
      git config user.name "github-actions[bot]"
      git config user.email "41898282+github-actions[bot]@users.noreply.github.com"
      git log -1 --oneline
safe-outputs:
  push-to-pull-request-branch:
    target: ${{ inputs.pr }}
    required-title-prefix: "OpenSpec: "
    if-no-changes: error
    fallback-as-pull-request: false
    signed-commits: false
    check-branch-protection: false
    github-token-for-extra-empty-commit: none
    protected-files:
      policy: blocked
      exclude:
        - README.md
        - AGENTS.md
        - Directory.Packages.props
  noop:
    report-as-issue: false
  missing-tool:
    create-issue: false
  report-incomplete:
    create-issue: false
  report-failure-as-issue: false
---

# OpenSpec agent

Read `/tmp/gh-aw/openspec/prompt.md` and follow it exactly. A trusted step built
it from the pull request's OpenSpec state with the same controller code that
validates your result, and already checked out the pull request branch at the
dispatched commit.

You cannot push and the sandbox has no Docker. If the `run_verification` tool
is available, use it for verification that needs Docker. Finish by calling
`push_to_pull_request_branch` once after your final checkpoint commit exists.
The orchestrator reconciles the result when this workflow reaches a terminal state.
