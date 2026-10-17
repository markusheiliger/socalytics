#!/usr/bin/env bash
# Installs the exact CPython, uv, and FFmpeg/ffprobe distributions declared in analyst-media-toolchain.json.
# Every archive is checked against its committed SHA-256 digest before it is listed or extracted.
# Optional environment (used by the action and its tests):
#   ANALYST_MEDIA_TOOLCHAIN     toolchain declaration (default: analyst-media-toolchain.json next to this script)
#   ANALYST_MEDIA_INSTALL_ROOT  action-owned install directory (default: $RUNNER_TEMP/<installRoot of the declaration>)
#   ANALYST_MEDIA_HOST_OS / ANALYST_MEDIA_HOST_ARCH  host identity (default: uname)
#   GITHUB_PATH / GITHUB_ENV    when set, receive the verified tool directories and the tool environment variables
set -euo pipefail
export LC_ALL=C

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
toolchain="${ANALYST_MEDIA_TOOLCHAIN:-$script_dir/analyst-media-toolchain.json}"

fail() {
  printf 'ERROR: Analyst media toolchain: tool=%s stage=%s: %s\nRecommended action: %s\n' "$1" "$2" "$3" "$4" >&2
  exit 1
}

for required in jq curl tar sha256sum; do
  command -v "$required" > /dev/null 2>&1 \
    || fail "$required" prerequisites "the runner does not provide $required" \
      "use the supported Linux x64 runner image, which provides $required, and rerun setup."
done

[[ -f "$toolchain" ]] \
  || fail toolchain declaration "declaration file '$toolchain' does not exist" \
    "restore analyst-media-toolchain.json through a standalone environment change and rerun setup."
jq -e '.schemaVersion == 1 and (.tools | type == "array" and length > 0)' "$toolchain" > /dev/null 2>&1 \
  || fail toolchain declaration "'$toolchain' is not a valid schemaVersion 1 declaration with tools" \
    "correct the declaration through a standalone environment change and rerun setup."

supported_os="$(jq -r '.supportedHost.os' "$toolchain")"
supported_arch="$(jq -r '.supportedHost.architecture' "$toolchain")"
host_os="${ANALYST_MEDIA_HOST_OS:-$(uname -s)}"
host_arch="${ANALYST_MEDIA_HOST_ARCH:-$(uname -m)}"
if [[ "$host_os" != "$supported_os" || "$host_arch" != "$supported_arch" ]]; then
  fail host platform "host $host_os/$host_arch is unsupported; only $supported_os/$supported_arch is declared" \
    "run setup on the supported $supported_os $supported_arch runner."
fi

install_dir="$(jq -r '.installRoot' "$toolchain")"
if [[ -n "${ANALYST_MEDIA_INSTALL_ROOT:-}" ]]; then
  install_root="$ANALYST_MEDIA_INSTALL_ROOT"
else
  [[ -n "${RUNNER_TEMP:-}" ]] \
    || fail toolchain install-root "RUNNER_TEMP is not set and no install root was given" \
      "run setup inside the GitHub Actions runner, which provides RUNNER_TEMP."
  install_root="$RUNNER_TEMP/$install_dir"
fi
mkdir -p "$install_root"
install_root="$(cd "$install_root" && pwd -P)"

downloads="$install_root/downloads"
staging_root="$install_root/staging"
notices_root="$install_root/notices"
rm -rf "$downloads" "$staging_root"
mkdir -p "$downloads" "$staging_root" "$notices_root"

# Prints the normalized path, or ESCAPE when it leaves the archive root.
normalize_path() {
  local -a stack=()
  local part
  IFS='/' read -ra parts <<< "$1"
  for part in "${parts[@]}"; do
    case "$part" in
      '' | '.') ;;
      '..')
        if ((${#stack[@]} == 0)); then
          echo ESCAPE
          return
        fi
        unset 'stack[${#stack[@]}-1]'
        ;;
      *) stack+=("$part") ;;
    esac
  done
  local IFS=/
  echo "${stack[*]:-}"
}

inside_root() {
  [[ "$1" == "$2" || "$1" == "$2"/* ]]
}

validate_archive_layout() {
  local id="$1" archive="$2" root="$3" entry normalized line name target resolved listing
  listing="$(tar -tf "$archive")" \
    || fail "$id" extract "the archive cannot be listed" \
      "inspect the named archive and pin evidence, and correct the environment through a standalone change."
  while IFS= read -r entry; do
    [[ -n "$entry" ]] || continue
    [[ "$entry" != /* ]] \
      || fail "$id" extract "archive entry '$entry' is an absolute path" \
        "reject this archive; correct the pinned artifact through a standalone environment change."
    normalized="$(normalize_path "$entry")"
    [[ "$normalized" != ESCAPE ]] \
      || fail "$id" extract "archive entry '$entry' escapes the extraction directory" \
        "reject this archive; correct the pinned artifact through a standalone environment change."
    [[ -n "$normalized" ]] && ! inside_root "$normalized" "$root" \
      && fail "$id" layout "archive entry '$entry' is outside the expected root '$root'" \
        "inspect the archive layout and correct the declared extractedRoot through a standalone environment change."
  done <<< "$listing"

  while IFS= read -r line; do
    case "${line:0:1}" in
      l)
        name="${line%% -> *}"
        name="${name##* }"
        target="${line#* -> }"
        [[ "$target" != /* ]] \
          || fail "$id" extract "symbolic link '$name' has the absolute target '$target'" \
            "reject this archive; correct the pinned artifact through a standalone environment change."
        resolved="$(normalize_path "$(dirname "$name")/$target")"
        ;;
      h)
        target="${line#* link to }"
        resolved="$(normalize_path "$target")"
        ;;
      *) continue ;;
    esac
    if [[ "$resolved" == ESCAPE ]] || ! inside_root "$resolved" "$root"; then
      fail "$id" extract "archive link '$line' points outside '$root'" \
        "reject this archive; correct the pinned artifact through a standalone environment change."
    fi
  done < <(tar -tvf "$archive")
}

validate_relative() {
  local id="$1" label="$2" value="$3" normalized
  normalized="$(normalize_path "$value")"
  if [[ "$value" == /* || "$normalized" == ESCAPE || -z "$normalized" ]]; then
    fail "$id" declaration "declared $label '$value' is not a safe relative path" \
      "correct the declaration through a standalone environment change."
  fi
}

run_version_check() {
  local id="$1" check="$2" executable_key executable expected output first
  local -a args
  executable_key="$(jq -r '.executable' <<< "$check")"
  expected="$(jq -r '.expectedOutput' <<< "$check")"
  mapfile -t args < <(jq -r '.arguments[]' <<< "$check")
  executable="${tool_paths[$executable_key]:-}"
  [[ -n "$executable" ]] \
    || fail "$id" version "no executable named '$executable_key' is declared for the version check" \
      "correct the declaration through a standalone environment change."
  if ! output="$("$executable" "${args[@]}" 2>&1)"; then
    fail "$id" version "$executable_key failed to run: ${output:0:200}" \
      "inspect the named artifact and pin evidence, and correct the environment through a standalone change."
  fi
  first="${output%%$'\n'*}"
  if [[ "$first" != "$expected"* ]]; then
    fail "$id" version "$executable_key reported '$first' instead of '$expected'" \
      "inspect the named artifact and pin evidence, and correct the environment through a standalone change; never bypass the check."
  fi
  echo "Verified $id: $first"
}

path_directories=()
env_assignments=()
tool_count="$(jq '.tools | length' "$toolchain")"

for ((index = 0; index < tool_count; index++)); do
  tool="$(jq -c ".tools[$index]" "$toolchain")"
  id="$(jq -r '.id' <<< "$tool")"
  url="$(jq -r '.archive.url' <<< "$tool")"
  file_name="$(jq -r '.archive.fileName' <<< "$tool")"
  format="$(jq -r '.archive.format' <<< "$tool")"
  expected_digest="$(jq -r '.archive.sha256' <<< "$tool")"
  root="$(jq -r '.extractedRoot' <<< "$tool")"

  [[ "$expected_digest" =~ ^[0-9a-f]{64}$ ]] \
    || fail "$id" declaration "the committed digest is not a SHA-256 value" \
      "correct the declaration through a standalone environment change."
  [[ "$file_name" != */* && "$file_name" != ..* && "$file_name" == *."$format" ]] \
    || fail "$id" declaration "archive file name '$file_name' is unsafe or does not match format '$format'" \
      "correct the declaration through a standalone environment change."
  validate_relative "$id" extractedRoot "$root"
  declare -A tool_paths=()
  while IFS=$'\t' read -r key value; do
    validate_relative "$id" "executable $key" "$value"
    inside_root "$(normalize_path "$value")" "$root" \
      || fail "$id" declaration "executable '$key' ($value) is not inside the extracted root '$root'" \
        "correct the declaration through a standalone environment change."
    tool_paths["$key"]="$install_root/$value"
  done < <(jq -r '.executables | to_entries[] | [.key, .value] | @tsv' <<< "$tool")

  archive="$downloads/$file_name"
  echo "Downloading $id from $url"
  curl --fail --silent --show-error --location --retry 3 --connect-timeout 30 --max-time 900 \
    --output "$archive" "$url" \
    || fail "$id" download "cannot download $url" \
      "restore access to the named exact archive and rerun setup; a permanently unavailable artifact requires a reviewed environment pin update."

  actual_digest="$(sha256sum "$archive" | cut -d' ' -f1)"
  [[ "$actual_digest" == "$expected_digest" ]] \
    || fail "$id" integrity "SHA-256 of $file_name is $actual_digest, not the committed $expected_digest" \
      "inspect the named artifact and pin evidence, and correct the environment through a standalone change; never bypass the check."

  validate_archive_layout "$id" "$archive" "$root"

  staging="$staging_root/$id"
  rm -rf "$staging"
  mkdir -p "$staging"
  tar -xf "$archive" -C "$staging" --no-same-owner \
    || fail "$id" extract "extraction of $file_name failed" \
      "inspect the named archive and pin evidence, and correct the environment through a standalone change."

  staging_real="$(cd "$staging" && pwd -P)"
  while IFS= read -r link; do
    resolved="$(readlink -m "$link")"
    inside_root "$resolved" "$staging_real" \
      || fail "$id" extract "extracted link '${link#"$staging"/}' resolves outside the extraction directory" \
        "reject this archive; correct the pinned artifact through a standalone environment change."
  done < <(find "$staging" -type l)

  [[ -d "$staging/$root" ]] \
    || fail "$id" layout "the expected root '$root' is missing after extraction" \
      "inspect the archive layout and correct the declared extractedRoot through a standalone environment change."
  for key in "${!tool_paths[@]}"; do
    relative="${tool_paths[$key]#"$install_root"/}"
    [[ -f "$staging/$relative" && -x "$staging/$relative" ]] \
      || fail "$id" layout "executable '$key' is missing or not executable at '$relative'" \
        "inspect the archive layout and correct the declared executables through a standalone environment change."
  done

  mkdir -p "$notices_root/$id"
  while IFS= read -r notice; do
    [[ -n "$notice" ]] || continue
    validate_relative "$id" "license location $notice" "$notice"
    [[ -f "$staging/$notice" ]] \
      || fail "$id" notices "license file '$notice' is missing from the archive" \
        "inspect the archive layout and correct the declared license locations through a standalone environment change."
    cp "$staging/$notice" "$notices_root/$id/$(basename "$notice")"
  done < <(jq -r '.licenseLocations[]' <<< "$tool")
  jq -r '"\(.name) \(.version) from \(.provenanceLocation)\narchive \(.archive.fileName) sha256 \(.archive.sha256)"' \
    <<< "$tool" > "$notices_root/$id/PROVENANCE.txt"

  rm -rf "${install_root:?}/$root"
  mv "$staging/$root" "$install_root/$root"

  jq -c '[.versionCheck | {executable, arguments, expectedOutput}] + (.versionCheck.additionalChecks // []) | .[]' \
    <<< "$tool" > "$staging_root/$id.checks"
  while IFS= read -r check; do
    run_version_check "$id" "$check"
  done < "$staging_root/$id.checks"

  while IFS= read -r directory; do
    [[ -n "$directory" ]] || continue
    validate_relative "$id" "path directory $directory" "$directory"
    path_directories+=("$install_root/$directory")
  done < <(jq -r '.pathDirectories[]' <<< "$tool")

  variable="$(jq -r '.environmentVariable // empty' <<< "$tool")"
  if [[ -n "$variable" ]]; then
    env_assignments+=("$variable=${tool_paths[python]:-${tool_paths[$id]:-}}")
  fi
  unset tool_paths
done

rm -rf "$downloads" "$staging_root"

for directory in "${path_directories[@]}"; do
  PATH="$directory:$PATH"
  [[ -z "${GITHUB_PATH:-}" ]] || echo "$directory" >> "$GITHUB_PATH"
done
export PATH
for assignment in "${env_assignments[@]}"; do
  export "${assignment?}"
  [[ -z "${GITHUB_ENV:-}" ]] || echo "$assignment" >> "$GITHUB_ENV"
done

echo "Analyst media toolchain installed and verified in $install_root"
