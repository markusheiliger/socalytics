---
name: OpenSpec agent
description: Run one OpenSpec agent session (apply task, verify, sync, or archive) that the OpenSpec orchestrator dispatched.
run-name: "openspec agent · #${{ inputs.pr }} ${{ inputs.branch }} · ${{ inputs.dispatch_id }}"
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
network:
  allowed:
    - defaults
    - dotnet
    - node
tools:
  bash: true
  edit:
  timeout: 900
timeout-minutes: 60
mcp-scripts:
  run_platform_tests:
    description: >-
      Run the platform test suite (dotnet test src/platform/SocAlytics.Platform.slnx) on the runner host,
      where Docker and Testcontainers are available, against your current working tree. Optionally pass a
      dotnet test --filter expression. Returns the end of the test output.
    inputs:
      filter:
        type: string
        description: Optional dotnet test --filter expression, for example FullyQualifiedName~Migration
        required: false
    env:
      HOST_TESTS: ${{ vars.OPENSPEC_AGENT_HOST_TESTS }}
    run: |
      if [[ "${HOST_TESTS:-}" != "true" ]]; then
        echo "run_platform_tests is disabled in this repository. Run what you can in the sandbox; the OpenSpec orchestrator runs the full platform tests with Testcontainers after your checkpoint and passes failures to the next attempt."
        exit 0
      fi
      cd "$GITHUB_WORKSPACE"
      args=(test src/platform/SocAlytics.Platform.slnx --nologo --blame-hang-timeout 10m)
      if [[ -n "${INPUT_FILTER:-}" ]]; then args+=(--filter "$INPUT_FILTER"); fi
      # Agent-written test code runs here, so it gets a minimal environment without tokens.
      set +e
      env -i PATH="$PATH" HOME="$HOME" DOTNET_ROOT="${DOTNET_ROOT:-}" DOTNET_NOLOGO=1 DOTNET_CLI_TELEMETRY_OPTOUT=1 \
        timeout 840 dotnet "${args[@]}" > /tmp/openspec-tests.log 2>&1
      status=$?
      set -e
      tail -n 150 /tmp/openspec-tests.log
      echo "exit code: $status"
    timeout: 900
pre-agent-steps:
  - name: Set up .NET for the platform build
    uses: actions/setup-dotnet@v6
    with:
      global-json-file: src/platform/global.json
  - name: Install the OpenSpec CLI
    run: npm install --global @fission-ai/openspec@1.13.0
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
  jobs:
    wake-controller:
      name: Wake the OpenSpec orchestrator
      description: Starts the OpenSpec orchestrator so it validates your checkpoint. Call it exactly once, last.
      runs-on: ubuntu-latest
      needs: safe_outputs
      permissions:
        actions: write
      inputs:
        note:
          description: Optional one-line note for the workflow log
          required: false
          type: string
      steps:
        - name: Start the OpenSpec orchestrator
          env:
            GH_TOKEN: ${{ github.token }}
            PR: ${{ github.event.inputs.pr }}
          run: |
            gh workflow run openspec-orchestrator.yml --repo "$GITHUB_REPOSITORY" --ref main -f reason="agent finished on #$PR"
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

You cannot push and the sandbox has no Docker. Use `run_platform_tests` for
tests that need Docker. Finish by calling `push_to_pull_request_branch` once
after your final checkpoint commit exists, then `wake_controller` once.
