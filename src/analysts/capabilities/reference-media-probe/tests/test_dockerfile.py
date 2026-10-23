import json
import re
import shlex
from pathlib import Path

import pytest

PROJECT_ROOT = Path(__file__).resolve().parent.parent
DOCKERFILE = PROJECT_ROOT / "Dockerfile"
MANIFEST_PATH = PROJECT_ROOT / "analyst-manifest.json"
MANIFEST_TARGET = "/opt/socalytics/analyst-manifest.json"
SHA256_DIGEST = re.compile(r"@sha256:[0-9a-f]{64}$")
SENSITIVE_ENV_NAME = re.compile(r"CREDENTIAL|TOKEN|SECRET|PASSWORD|PASSWD|API_?KEY|ADDRESS|ENDPOINT|URL|URI|HOST", re.I)


def _repository_root() -> Path:
    for candidate in PROJECT_ROOT.parents:
        if (candidate / ".github" / "actions" / "environment-setup" / "analyst-media-toolchain.json").is_file():
            return candidate
    raise AssertionError("repository root with analyst-media-toolchain.json not found")


def _instructions() -> list[tuple[str, str]]:
    """Return (INSTRUCTION, arguments) pairs with line continuations joined and comments dropped."""
    logical: list[str] = []
    pending = ""
    for raw in DOCKERFILE.read_text(encoding="utf-8").splitlines():
        stripped = raw.strip()
        if not pending and (not stripped or stripped.startswith("#")):
            continue
        if pending and stripped.startswith("#"):
            continue
        if stripped.endswith("\\"):
            pending += stripped[:-1].rstrip() + " "
            continue
        logical.append(pending + stripped)
        pending = ""
    assert pending == ""
    result = []
    for line in logical:
        keyword, _, arguments = line.partition(" ")
        result.append((keyword.upper(), arguments.strip()))
    return result


def _stages() -> dict[str, list[tuple[str, str]]]:
    stages: dict[str, list[tuple[str, str]]] = {}
    current: list[tuple[str, str]] | None = None
    for keyword, arguments in _instructions():
        if keyword == "FROM":
            match = re.match(r"(\S+)\s+AS\s+(\S+)$", arguments, re.I)
            assert match, f"FROM without a stage name: {arguments}"
            current = [("FROM", match.group(1))]
            stages[match.group(2)] = current
        else:
            assert current is not None
            current.append((keyword, arguments))
    return stages


def _arg_defaults() -> dict[str, str]:
    defaults = {}
    for keyword, arguments in _instructions():
        if keyword == "ARG" and "=" in arguments:
            name, _, value = arguments.partition("=")
            defaults[name] = value
    return defaults


def _key_values(arguments: str) -> dict[str, str]:
    pairs = {}
    for token in shlex.split(arguments):
        name, separator, value = token.partition("=")
        assert separator, f"expected KEY=value, got {token}"
        pairs[name] = value
    return pairs


@pytest.fixture(scope="module")
def manifest() -> dict:
    return json.loads(MANIFEST_PATH.read_text(encoding="utf-8"))


@pytest.fixture(scope="module")
def toolchain() -> dict[str, dict]:
    path = _repository_root() / ".github" / "actions" / "environment-setup" / "analyst-media-toolchain.json"
    return {tool["id"]: tool for tool in json.loads(path.read_text(encoding="utf-8"))["tools"]}


def test_stage_layout():
    stages = _stages()
    assert list(stages) == ["build", "final"]


def test_uv_pins_equal_the_toolchain_file(toolchain):
    defaults = _arg_defaults()
    archive = toolchain["uv"]["archive"]
    assert defaults["UV_URL"] == archive["url"]
    assert defaults["UV_SHA256"] == archive["sha256"]
    assert defaults["UV_ROOT"] == toolchain["uv"]["extractedRoot"]


def test_ffmpeg_pins_equal_the_toolchain_file(toolchain):
    defaults = _arg_defaults()
    archive = toolchain["ffmpeg"]["archive"]
    assert defaults["FFMPEG_URL"] == archive["url"]
    assert defaults["FFMPEG_SHA256"] == archive["sha256"]
    assert defaults["FFMPEG_ROOT"] == toolchain["ffmpeg"]["extractedRoot"]


def test_base_images_are_pinned_by_digest():
    for name, instructions in _stages().items():
        image = instructions[0][1]
        assert SHA256_DIGEST.search(image), f"stage {name} base image is not pinned by digest: {image}"


def test_labels_equal_the_manifest(manifest):
    import hashlib

    final = _stages()["final"]
    labels: dict[str, str] = {}
    for keyword, arguments in final:
        if keyword == "LABEL":
            labels.update(_key_values(arguments))
    expected = {
        "io.socalytics.analyst-manifest.version": manifest["manifestVersion"],
        "io.socalytics.analyst-manifest.sha256": hashlib.sha256(MANIFEST_PATH.read_bytes()).hexdigest(),
        "io.socalytics.capability.id": manifest["capability"]["id"],
        "io.socalytics.capability.version": manifest["capability"]["version"],
        "io.socalytics.model.digest": manifest["model"]["digest"],
    }
    socalytics_labels = {name: value for name, value in labels.items() if name.startswith("io.socalytics.")}
    assert socalytics_labels == expected
    assert _arg_defaults()["MANIFEST_SHA256"] == expected["io.socalytics.analyst-manifest.sha256"]


def test_user_is_numeric_and_not_root():
    users = [arguments for keyword, arguments in _stages()["final"] if keyword == "USER"]
    assert len(users) == 1
    match = re.fullmatch(r"(\d+)(?::(\d+))?", users[0])
    assert match, f"user is not numeric: {users[0]}"
    assert int(match.group(1)) != 0
    assert match.group(2) is None or int(match.group(2)) != 0


def test_final_stage_copies_only_the_python_environment_tools_and_manifest():
    stages = _stages()
    copies = [arguments for keyword, arguments in stages["final"] if keyword in ("COPY", "ADD")]
    assert [keyword for keyword, _ in stages["final"] if keyword == "ADD"] == []
    assert len(copies) == 2
    from_build = [arguments.split() for arguments in copies if "--from=build" in arguments.split()]
    assert len(from_build) == 1
    assert from_build[0][-2:] == ["/opt/reference-media-probe", "/opt/reference-media-probe"]

    # The build stage puts only ffmpeg, ffprobe, and the license notice into the copied bin directory.
    build_text = " ".join(arguments for keyword, arguments in stages["build"] if keyword == "RUN")
    ffmpeg_call = build_text[build_text.index("${FFMPEG_URL}") :]
    members = re.findall(r"--member\s+\"?([^\s\"]+)\"?", ffmpeg_call.split("&&")[0])
    assert members == [
        "${FFMPEG_ROOT}/bin/ffmpeg",
        "${FFMPEG_ROOT}/bin/ffprobe",
        "${FFMPEG_ROOT}/LICENSE.txt",
    ]
    assert "--dest /opt/reference-media-probe/bin" in ffmpeg_call


def test_last_layer_creating_instruction_copies_only_the_manifest():
    layer_keywords = {"RUN", "COPY", "ADD"}
    final = _stages()["final"]
    last_keyword, last_arguments = [item for item in final if item[0] in layer_keywords][-1]
    assert last_keyword == "COPY"
    assert "--from" not in last_arguments
    parts = last_arguments.split()
    assert parts[-1] == MANIFEST_TARGET
    assert parts[:-1] == ["src/analysts/capabilities/reference-media-probe/analyst-manifest.json"]
    assert final[-1] == (last_keyword, last_arguments)


def test_no_env_names_a_credential_address_or_token():
    for keyword, arguments in _instructions():
        if keyword == "ENV":
            for name in _key_values(arguments):
                assert not SENSITIVE_ENV_NAME.search(name), f"ENV {name} names a credential, address, or token"
