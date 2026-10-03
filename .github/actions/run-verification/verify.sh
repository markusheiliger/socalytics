#!/usr/bin/env bash
# Repository-specific verification. The single definition shared by the run-verification composite
# action (the OpenSpec orchestrator's checkpoint verification) and the agent's run_verification tool.
#
#   verify.sh scope   decide whether the checkpoint needs verification (prints relevant=true|false)
#   verify.sh test    run the platform tests; write verification.txt and log.txt; exit with their status
#
# Environment: VERIFY_OUTPUT (result directory, required), VERIFY_SOURCE (checked-out code, default .),
# VERIFY_BASELINE and VERIFY_SHA (scope), VERIFY_FILTER (optional dotnet test --filter expression).
set -uo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
source_dir="${VERIFY_SOURCE:-.}"
out="${VERIFY_OUTPUT:?VERIFY_OUTPUT is required}"
mkdir -p "$out"

case "${1:-}" in
  scope)
    if [[ -n "${VERIFY_BASELINE:-}" && -n "${VERIFY_SHA:-}" ]] \
      && git -C "$source_dir" cat-file -e "${VERIFY_BASELINE}^{commit}" 2>/dev/null \
      && [[ -z "$(git -C "$source_dir" diff --name-only "$VERIFY_BASELINE" "$VERIFY_SHA" -- src/platform/)" ]]; then
      echo "Skipped: the checkpoint does not change src/platform/." > "$out/verification.txt"
      relevant=false
    else
      relevant=true
    fi
    echo "relevant=$relevant"
    if [[ -n "${GITHUB_OUTPUT:-}" ]]; then echo "relevant=$relevant" >> "$GITHUB_OUTPUT"; fi
    ;;
  test)
    args=(test src/platform/SocAlytics.Platform.slnx --nologo --blame-hang-timeout 10m)
    if [[ -n "${VERIFY_FILTER:-}" ]]; then args+=(--filter "$VERIFY_FILTER"); fi
    (cd "$source_dir" && dotnet "${args[@]}") 2>&1 | tee "$out/log.txt"
    status=${PIPESTATUS[0]}
    if [[ $status -eq 0 ]]; then
      {
        echo "All platform tests passed."
        grep -E '^\s*Passed!' "$out/log.txt" | sed -E 's/^\s+//' || true
      } > "$out/verification.txt"
    elif ! node "$here/dotnet-test-summary.mjs" "$out/log.txt" "$out/verification.txt"; then
      tail -n 80 "$out/log.txt" > "$out/verification.txt"
    fi
    exit "$status"
    ;;
  *)
    echo "usage: verify.sh scope|test" >&2
    exit 2
    ;;
esac