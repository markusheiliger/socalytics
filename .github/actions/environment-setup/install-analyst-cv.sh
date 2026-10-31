#!/usr/bin/env bash
# Installs the exact CPU computer-vision and inference wheels declared in analyst-cv-libraries.json.
# Every wheel is checked against its committed SHA-256 digest before it is made available, must be a binary wheel
# compatible with the declared interpreter tag and the host, and is installed offline, hash-pinned, and without
# dependency resolution into an isolated virtual environment that the synthetic smoke check then exercises.
# Optional environment (used by the action and its tests):
#   ANALYST_CV_LIBRARIES        wheel declaration (default: analyst-cv-libraries.json next to this script)
#   ANALYST_MEDIA_TOOLCHAIN     toolchain declaration, only read for the install root name
#   ANALYST_MEDIA_INSTALL_ROOT  action-owned install directory (default: $RUNNER_TEMP/<installRoot of the toolchain>)
#   ANALYST_MEDIA_HOST_OS / ANALYST_MEDIA_HOST_ARCH  host identity (default: uname)
#   SOCALYTICS_PYTHON           interpreter of the isolated environment (default: python3)
# Changes neither GITHUB_PATH nor GITHUB_ENV.
set -euo pipefail
export LC_ALL=C
export UV_PYTHON_DOWNLOADS=never

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
declaration="${ANALYST_CV_LIBRARIES:-$script_dir/analyst-cv-libraries.json}"
toolchain="${ANALYST_MEDIA_TOOLCHAIN:-$script_dir/analyst-media-toolchain.json}"
smoke_script="$script_dir/analyst-cv-smoke.py"
downloads=""
smoke_stderr=""

fail() {
  printf 'ERROR: Analyst CV libraries: library=%s stage=%s: %s\nRecommended action: %s\n' "$1" "$2" "$3" "$4" >&2
  exit 1
}

pin_action="inspect the named artifact and pin evidence, and correct the environment through a standalone change; never bypass the check."
declaration_action="correct the declaration through a standalone environment change and rerun setup."

cleanup() {
  [[ -z "$downloads" ]] || rm -rf "${downloads:?}" || true
  [[ -z "$smoke_stderr" ]] || rm -f -- "$smoke_stderr" || true
}
trap cleanup EXIT

for required in jq curl sha256sum timeout uv ffmpeg python3; do
  command -v "$required" > /dev/null 2>&1 \
    || fail "$required" prerequisites "the runner does not provide $required" \
      "use the supported Linux x64 runner image and the verified Analyst toolchain step, which provide $required, and rerun setup."
done
python="${SOCALYTICS_PYTHON:-python3}"
command -v "$python" > /dev/null 2>&1 \
  || fail python prerequisites "the interpreter '$python' is not available" \
    "run the Analyst Python and media toolchain step first, which provides SOCALYTICS_PYTHON, and rerun setup."

[[ -f "$declaration" ]] \
  || fail declaration declaration "declaration file '$declaration' does not exist" \
    "restore analyst-cv-libraries.json through a standalone environment change and rerun setup."
jq -e '.schemaVersion == 1 and (.wheels | type == "array" and length > 0) and (.interpreter.wheelTag | test("^cp3[0-9]+$"))' \
  "$declaration" > /dev/null 2>&1 \
  || fail declaration declaration "'$declaration' is not a valid schemaVersion 1 declaration with wheels and an interpreter wheel tag" \
    "$declaration_action"

supported_os="$(jq -r '.supportedHost.os' "$declaration")"
supported_arch="$(jq -r '.supportedHost.architecture' "$declaration")"
host_os="${ANALYST_MEDIA_HOST_OS:-$(uname -s)}"
host_arch="${ANALYST_MEDIA_HOST_ARCH:-$(uname -m)}"
if [[ "$host_os" != "$supported_os" || "$host_arch" != "$supported_arch" ]]; then
  fail host "host platform" "host $host_os/$host_arch is unsupported; only $supported_os/$supported_arch is declared" \
    "run setup on the supported $supported_os $supported_arch runner."
fi
[[ "$supported_arch" =~ ^[A-Za-z0-9_]+$ ]] \
  || fail declaration declaration "the declared architecture '$supported_arch' is not a platform tag suffix" \
    "$declaration_action"

wheel_tag="$(jq -r '.interpreter.wheelTag' "$declaration")"
interpreter_minor="${wheel_tag#cp3}"

if [[ -n "${ANALYST_MEDIA_INSTALL_ROOT:-}" ]]; then
  install_root="$ANALYST_MEDIA_INSTALL_ROOT"
else
  [[ -n "${RUNNER_TEMP:-}" ]] \
    || fail install-root declaration "RUNNER_TEMP is not set and no install root was given" \
      "run setup inside the GitHub Actions runner, which provides RUNNER_TEMP."
  install_dir="$(jq -r '.installRoot // empty' "$toolchain" 2> /dev/null || true)"
  [[ -n "$install_dir" && "$install_dir" != /* && "$install_dir" != *..* ]] \
    || fail install-root declaration "the toolchain declaration '$toolchain' names no safe installRoot" \
      "$declaration_action"
  install_root="$RUNNER_TEMP/$install_dir"
fi
mkdir -p "$install_root"
install_root="$(cd "$install_root" && pwd -P)"

downloads="$install_root/downloads"
wheelhouse="$install_root/analyst-cv-wheels"
venv="$install_root/analyst-cv-venv"
rm -rf "$downloads" "$wheelhouse" "$venv"
mkdir -p "$downloads"

wheel_count="$(jq '.wheels | length' "$declaration")"
names=()
versions=()
file_names=()
urls=()
digests=()

for ((index = 0; index < wheel_count; index++)); do
  name="$(jq -r ".wheels[$index].name // empty" "$declaration")"
  version="$(jq -r ".wheels[$index].version // empty" "$declaration")"
  file_name="$(jq -r ".wheels[$index].fileName // empty" "$declaration")"
  url="$(jq -r ".wheels[$index].url // empty" "$declaration")"
  digest="$(jq -r ".wheels[$index].sha256 // empty" "$declaration")"
  [[ "$name" =~ ^[A-Za-z0-9][A-Za-z0-9._-]*$ && "$version" =~ ^[A-Za-z0-9][A-Za-z0-9._+!-]*$ ]] \
    || fail "${name:-wheel-$index}" declaration "the declared name or version is missing or malformed" "$declaration_action"
  [[ "$file_name" =~ ^[A-Za-z0-9][A-Za-z0-9._+!-]*$ && "$file_name" != *..* ]] \
    || fail "$name" declaration "file name '$file_name' is missing or unsafe" "$declaration_action"
  [[ "$url" == https://* ]] \
    || fail "$name" declaration "the declared URL is not an https URL" "$declaration_action"
  [[ "$digest" =~ ^[0-9a-f]{64}$ ]] \
    || fail "$name" declaration "the committed digest is not a SHA-256 value" "$declaration_action"
  names+=("$name")
  versions+=("$version")
  file_names+=("$file_name")
  urls+=("$url")
  digests+=("$digest")
done

for ((index = 0; index < wheel_count; index++)); do
  echo "Downloading ${names[index]} ${versions[index]} from ${urls[index]}"
  curl --fail --silent --show-error --location --retry 3 --connect-timeout 30 --max-time 900 \
    --output "$downloads/${file_names[index]}" "${urls[index]}" \
    || fail "${names[index]}" "cv download" "cannot download ${urls[index]}" \
      "restore access to the named exact wheel and rerun setup; a permanently unavailable artifact requires a reviewed environment pin update."
done

for ((index = 0; index < wheel_count; index++)); do
  actual="$(sha256sum "$downloads/${file_names[index]}" | cut -d' ' -f1)"
  [[ "$actual" == "${digests[index]}" ]] \
    || fail "${names[index]}" "cv integrity" "SHA-256 of ${file_names[index]} is $actual, not the committed ${digests[index]}" \
      "$pin_action"
done

# Prints the reason a wheel file name is not acceptable; prints nothing when it is.
compatibility_problem() {
  local name="$1" version="$2" file_name="$3" stem
  local -a parts pythons abis platforms
  local python_tag abi_tag platform_tag p a interpreter_ok=0 platform_ok=0
  if [[ "$file_name" != *.whl ]]; then
    echo "'$file_name' is not a binary wheel (source distributions are never built)"
    return
  fi
  stem="${file_name%.whl}"
  IFS='-' read -ra parts <<< "$stem"
  if ((${#parts[@]} != 5 && ${#parts[@]} != 6)); then
    echo "'$file_name' is not a valid wheel file name"
    return
  fi
  local base="${name//[-.]/_}"
  if [[ "${parts[0],,}" != "${base,,}" ]]; then
    echo "wheel '$file_name' is named '${parts[0]}', not the declared '$name'"
    return
  fi
  if [[ "${parts[1]}" != "$version" ]]; then
    echo "wheel '$file_name' carries version ${parts[1]}, not the pinned $version"
    return
  fi
  python_tag="${parts[${#parts[@]} - 3]}"
  abi_tag="${parts[${#parts[@]} - 2]}"
  platform_tag="${parts[${#parts[@]} - 1]}"
  IFS='.' read -ra pythons <<< "$python_tag"
  IFS='.' read -ra abis <<< "$abi_tag"
  IFS='.' read -ra platforms <<< "$platform_tag"
  for p in "${pythons[@]}"; do
    for a in "${abis[@]}"; do
      if [[ "$p" == "$wheel_tag" && ("$a" == "$wheel_tag" || "$a" == abi3 || "$a" == none) ]]; then
        interpreter_ok=1
      elif [[ "$p" =~ ^cp3([0-9]+)$ && "$a" == abi3 ]]; then
        ((10#${BASH_REMATCH[1]} > 10#$interpreter_minor)) || interpreter_ok=1
      elif [[ "$p" =~ ^py3([0-9]*)$ && "$a" == none ]]; then
        if [[ -z "${BASH_REMATCH[1]}" ]] || ((10#${BASH_REMATCH[1]} <= 10#$interpreter_minor)); then
          interpreter_ok=1
        fi
      fi
    done
  done
  if ((interpreter_ok == 0)); then
    echo "wheel tags $python_tag-$abi_tag do not match the interpreter tag $wheel_tag"
    return
  fi
  for p in "${platforms[@]}"; do
    if [[ "$p" =~ ^(any|linux_${supported_arch}|manylinux(1|2010|2014)_${supported_arch}|manylinux_[0-9]+_[0-9]+_${supported_arch})$ ]]; then
      platform_ok=1
    fi
  done
  if ((platform_ok == 0)); then
    echo "platform tag $platform_tag does not match the host $supported_os/$supported_arch"
  fi
}

for ((index = 0; index < wheel_count; index++)); do
  problem="$(compatibility_problem "${names[index]}" "${versions[index]}" "${file_names[index]}")"
  [[ -z "$problem" ]] \
    || fail "${names[index]}" "cv compatibility" "$problem" "$pin_action"
done

licenses_script="$script_dir/analyst-cv-licenses.py"
sbom="$install_root/notices/analyst-cv/sbom.cdx.json"
licenses_args=(--declaration "$declaration" --wheels "$downloads" --sbom "$sbom")
[[ ! -f "$toolchain" ]] || licenses_args+=(--ffmpeg-toolchain "$toolchain")
licenses_status=0
licenses_output="$(python3 "$licenses_script" "${licenses_args[@]}" 2>&1 < /dev/null)" || licenses_status=$?
if ((licenses_status != 0)); then
  first_finding="${licenses_output%%$'\n'*}"
  finding_count="$(printf '%s\n' "$licenses_output" | grep -c . || true)"
  finding_library="${first_finding%%:*}"
  finding_problem="${first_finding#*: }"
  [[ -n "$finding_library" && "$finding_library" != "$first_finding" && "$finding_library" =~ ^[A-Za-z0-9][A-Za-z0-9._-]*$ ]] \
    || { finding_library=licenses; finding_problem="the licence check failed: ${first_finding:0:300}"; }
  ((finding_count <= 1)) || finding_problem="$finding_problem (and $((finding_count - 1)) more findings)"
  fail "$finding_library" "cv licenses" "$finding_problem" \
    "correct the pin or the licence table through a standalone environment change and rerun setup; never bypass the check."
fi
bundled_count="$(jq '[.components[]? | (.components // [])[]] | length' "$sbom" 2> /dev/null)" \
  || fail licenses "cv licenses" "the licence check wrote no readable SBOM at $sbom" "$pin_action"

mkdir -p "$wheelhouse"
requirements="$wheelhouse/requirements.txt"
{
  echo "--find-links $wheelhouse"
  for ((index = 0; index < wheel_count; index++)); do
    cp "$downloads/${file_names[index]}" "$wheelhouse/${file_names[index]}"
    echo "${names[index]}==${versions[index]} --hash=sha256:${digests[index]}"
  done
} > "$requirements"

install_output=""
if ! install_output="$(uv venv --python "$python" "$venv" 2>&1 < /dev/null)"; then
  install_output="${install_output//$'\n'/ }"
  fail uv "cv install" "creating the isolated environment failed: ${install_output:0:300}" "$pin_action"
fi
if ! install_output="$(uv pip install --python "$venv/bin/python" --offline --no-index --require-hashes --no-deps \
  -r "$requirements" 2>&1 < /dev/null)"; then
  install_output="${install_output//$'\n'/ }"
  fail uv "cv install" "the offline hash-pinned install failed: ${install_output:0:300}" "$pin_action"
fi

smoke_status=0
smoke_stderr="$(mktemp "$install_root/analyst-cv-smoke-stderr.XXXXXX")" \
  || fail smoke "cv smoke" "cannot create the smoke check's temporary stderr file" "$pin_action"
smoke_stdout="$(timeout 300 "$venv/bin/python" "$smoke_script" 2>"$smoke_stderr" < /dev/null)" || smoke_status=$?
smoke_failure_diagnostics() {
  local stderr_output diagnostics
  stderr_output="$(cat "$smoke_stderr" 2>/dev/null || true)"
  diagnostics="$smoke_stdout"
  [[ -z "$stderr_output" ]] || diagnostics+="${diagnostics:+ }$stderr_output"
  diagnostics="${diagnostics//$'\n'/ }"
  printf '%s' "${diagnostics:0:300}"
}
if ((smoke_status == 124)); then
  smoke_diagnostics="$(smoke_failure_diagnostics)"
  fail smoke "cv smoke" "the synthetic smoke check timed out after 300 seconds: $smoke_diagnostics" "$pin_action"
elif ((smoke_status != 0)); then
  smoke_diagnostics="$(smoke_failure_diagnostics)"
  fail smoke "cv smoke" "the synthetic smoke check failed: $smoke_diagnostics" "$pin_action"
fi

smoke_evidence_pattern='^Verified synthetic CV smoke check: decoded [0-9]+ frames, NumPy mean [0-9]+([.][0-9]+)?, ONNX Runtime Add graph executed$'
smoke_evidence=""
while IFS= read -r line; do
  if [[ "$line" =~ $smoke_evidence_pattern ]]; then
    smoke_evidence="$line"
  fi
done <<< "$smoke_stdout"
if [[ -z "$smoke_evidence" ]]; then
  smoke_diagnostics="$(smoke_failure_diagnostics)"
  fail smoke "cv smoke" "the smoke check printed no evidence line: $smoke_diagnostics" "$pin_action"
fi

echo "$smoke_evidence"
echo "Verified Analyst CV libraries: $wheel_count wheels, $bundled_count bundled libraries, SBOM $sbom"
