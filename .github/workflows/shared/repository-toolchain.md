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
  # Runs before the pull request branch is checked out, so the tool executes main's script.
  - name: Keep a trusted copy of the verification script
    run: |
      rm -rf /tmp/openspec-verification-tools
      cp -r .github/actions/run-verification /tmp/openspec-verification-tools
mcp-scripts:
  run_verification:
    description: >-
      Run the repository verification (the same script as the OpenSpec orchestrator's checkpoint
      verification: the platform tests with Docker and Testcontainers) on the runner host against your
      current working tree. Optionally pass a dotnet test --filter expression. Returns the verification
      summary and the end of the test log.
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
      out=/tmp/openspec-verification
      rm -rf "$out"
      # Agent-written test code runs here, so it gets a minimal environment without tokens.
      set +e
      env -i PATH="$PATH" HOME="$HOME" DOTNET_ROOT="${DOTNET_ROOT:-}" DOTNET_NOLOGO=1 DOTNET_CLI_TELEMETRY_OPTOUT=1 \
        VERIFY_SOURCE="$GITHUB_WORKSPACE" VERIFY_OUTPUT="$out" VERIFY_FILTER="${INPUT_FILTER:-}" \
        timeout 840 bash /tmp/openspec-verification-tools/verify.sh test > /dev/null 2>&1
      status=$?
      set -e
      cat "$out/verification.txt" 2>/dev/null
      echo
      echo "--- end of the test log"
      tail -n 60 "$out/log.txt" 2>/dev/null
      echo "exit code: $status"
    timeout: 900
---
