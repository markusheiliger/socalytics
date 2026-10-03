---
# Repository-specific additions to the generic OpenSpec agent workflow (openspec-agent.md).
# The generic workflow always imports this file; a repository without extra tooling keeps it
# with empty frontmatter. Installations live in the composite action so that the verification
# workflow and the Copilot setup steps share them.
network:
  allowed:
    - dotnet
pre-agent-steps:
  - name: Set up the repository toolchain
    uses: ./.github/actions/setup-toolchain
mcp-scripts:
  run_verification:
    description: >-
      Run the platform tests (dotnet test src/platform/SocAlytics.Platform.slnx) on the runner host,
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
        echo "run_verification is disabled in this repository. Run what you can in the sandbox; the OpenSpec orchestrator runs the repository verification after your checkpoint and passes failures to the next attempt."
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
---
