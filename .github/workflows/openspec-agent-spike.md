---
name: OpenSpec agent spike
description: Trial one OpenSpec apply task in an agentic workflow instead of a Copilot cloud agent session.
on:
  workflow_dispatch:
    inputs:
      pr:
        description: Spike pull request number (its title must start with "OpenSpec spike:")
        required: true
        type: string
      change:
        description: OpenSpec change ref whose next unchecked task runs
        required: true
        type: string
      issue:
        description: Issue twin number of the change
        required: true
        type: string
permissions:
  contents: read
  pull-requests: read
  copilot-requests: write
concurrency:
  group: openspec-agent-spike
  job-discriminator: ${{ github.run_id }}
checkout:
  fetch-depth: 0
  fetch: ["*"]
strict: true
engine:
  id: copilot
network:
  allowed:
    - defaults
    - dotnet
    - node
tools:
  bash: true
  edit:
timeout-minutes: 60
pre-agent-steps:
  - name: Set up .NET for the platform build
    uses: actions/setup-dotnet@v6
    with:
      global-json-file: src/platform/global.json
  - name: Install the OpenSpec CLI
    run: npm install --global @fission-ai/openspec@1.13.0
  - name: Build the agent prompt with the trusted controller
    id: prepare
    env:
      GITHUB_TOKEN: ${{ github.token }}
      PR: ${{ inputs.pr }}
      CHANGE: ${{ inputs.change }}
      ISSUE: ${{ inputs.issue }}
    run: |
      mkdir -p /tmp/gh-aw/openspec
      node .github/scripts/openspec-change-orchestrator.mjs agent-prompt \
        --pr "$PR" --change "$CHANGE" --issue "$ISSUE" --out /tmp/gh-aw/openspec/prompt.md
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
    target: "*"
    required-title-prefix: "OpenSpec spike: "
    if-no-changes: error
    fallback-as-pull-request: false
    github-token-for-extra-empty-commit: none
    protected-files:
      policy: blocked
      exclude:
        - README.md
        - AGENTS.md
  noop:
    report-as-issue: false
  missing-tool:
    create-issue: false
  report-incomplete:
    create-issue: false
  report-failure-as-issue: false
---

# OpenSpec agent spike

Read `/tmp/gh-aw/openspec/prompt.md` and follow it exactly. A trusted step
built it with the same controller code that dispatches Copilot cloud agent
sessions, and already checked out the pull request branch at the dispatched
commit.

You cannot push. Finish by calling `push_to_pull_request_branch` exactly once,
as the prompt describes, after your final checkpoint commit exists.
