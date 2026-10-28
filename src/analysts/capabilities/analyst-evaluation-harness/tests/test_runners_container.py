from __future__ import annotations

import json
import os
import stat
import sys
from pathlib import Path
from typing import Any

import pytest

from analyst_evaluation_harness.config import ConfigRefusal
from analyst_evaluation_harness.facts import EXECUTION_FAILED, INVALID_RESULT, TIMEOUT
from analyst_evaluation_harness.runners import (
    CONTAINER_MEMORY_MEBIBYTES,
    CONTAINER_PIDS_LIMIT,
    container_argv,
    ensure_image_present,
    run_container,
)
from analyst_evaluation_harness.staging import StagedMount, stage_mount
from support.builders import clip_bytes, fact

IMAGE = "registry.example/analyst@sha256:" + "a" * 64

# The fake engine records every invocation as one JSON line; its behavior comes from environment variables.
FAKE_DOCKER = f"""\
#!{sys.executable}
import json
import os
import sys
import time
from pathlib import Path

argv = sys.argv[1:]
with open(os.environ["FAKE_DOCKER_LOG"], "a", encoding="utf-8") as log:
    log.write(json.dumps(argv) + "\\n")

if argv[:2] == ["image", "inspect"]:
    sys.exit(0 if argv[2] in os.environ.get("FAKE_DOCKER_IMAGES", "").split() else 1)

if argv[:1] == ["run"]:
    mode = os.environ.get("FAKE_DOCKER_MODE", "success")
    if mode == "hang":
        time.sleep(3600)
    if mode == "fail":
        sys.stderr.write("socalytics-analyst: failed input-invalid\\n")
        sys.exit(3)
    result = next(a for a in argv if a.endswith(":/socalytics/result:rw")).rsplit(":", 2)[0]
    if mode == "success":
        document = {{
            "contractVersion": "1.0.0",
            "capability": {{"id": "scripted", "version": "1.0.0"}},
            "schemaVersion": "1.0.0",
            "outputType": "foundational-facts",
            "facts": json.loads(os.environ["FAKE_DOCKER_FACTS"]),
        }}
        Path(result, "facts.json").write_text(json.dumps(document), encoding="utf-8")
    sys.exit(0)

sys.exit(0)
"""


class FakeEngine:
    def __init__(self, folder: Path, monkeypatch: pytest.MonkeyPatch) -> None:
        self.log = folder / "docker.log"
        binaries = folder / "bin"
        binaries.mkdir()
        executable = binaries / "docker"
        executable.write_text(FAKE_DOCKER, encoding="utf-8")
        executable.chmod(executable.stat().st_mode | stat.S_IXUSR)
        monkeypatch.setenv("PATH", f"{binaries}{os.pathsep}{os.environ.get('PATH', os.defpath)}")
        monkeypatch.setenv("FAKE_DOCKER_LOG", str(self.log))
        monkeypatch.setenv("FAKE_DOCKER_IMAGES", IMAGE)
        monkeypatch.setenv("FAKE_DOCKER_FACTS", json.dumps([fact(1000)]))

    def mode(self, monkeypatch: pytest.MonkeyPatch, mode: str) -> None:
        monkeypatch.setenv("FAKE_DOCKER_MODE", mode)

    def calls(self) -> list[list[str]]:
        if not self.log.exists():
            return []
        return [json.loads(line) for line in self.log.read_text(encoding="utf-8").splitlines()]

    def calls_of(self, verb: str) -> list[list[str]]:
        return [call for call in self.calls() if call[0] == verb]


@pytest.fixture
def engine(tmp_path: Path, monkeypatch: pytest.MonkeyPatch) -> FakeEngine:
    folder = tmp_path / "engine"
    folder.mkdir()
    return FakeEngine(folder, monkeypatch)


@pytest.fixture
def run_folder(tmp_path: Path) -> Path:
    folder = tmp_path / "run"
    folder.mkdir()
    return folder


@pytest.fixture
def mount(run_folder: Path, tmp_path: Path) -> StagedMount:
    clip = tmp_path / "clip.bin"
    clip.write_bytes(clip_bytes("clip"))
    return stage_mount(
        run_folder,
        "mount-1",
        clip_file=clip,
        match_start_ms=0,
        duration_ms=5000,
        capability_id="scripted",
        capability_version="1.0.0",
        output_type="foundational-facts",
    )


def _option_values(argv: list[str], option: str) -> list[str]:
    return [argv[index + 1] for index, token in enumerate(argv) if token == option]


def test_exact_restricted_arguments(mount: StagedMount) -> None:
    argv = container_argv(IMAGE, mount, "analyst-evaluation-test")

    expected = [
        "docker",
        "run",
        "--rm",
        "--name",
        "analyst-evaluation-test",
        "--pull",
        "never",
        "--network",
        "none",
        "--read-only",
        "--cap-drop",
        "ALL",
        "--security-opt",
        "no-new-privileges",
    ]
    if hasattr(os, "getuid"):
        expected += ["--user", f"{os.getuid()}:{os.getgid()}"]
    expected += [
        "--pids-limit",
        str(CONTAINER_PIDS_LIMIT),
        "--memory",
        f"{CONTAINER_MEMORY_MEBIBYTES}m",
        "-v",
        f"{mount.root / 'job.json'}:/socalytics/job.json:ro",
        "-v",
        f"{mount.root / 'attempt.json'}:/socalytics/attempt.json:ro",
        "-v",
        f"{mount.root / 'media'}:/socalytics/media:ro",
        "-v",
        f"{mount.root / 'result'}:/socalytics/result:rw",
        "-v",
        f"{mount.root / 'scratch'}:/socalytics/scratch:rw",
        IMAGE,
    ]
    assert argv == expected


def test_only_result_and_scratch_are_writable(mount: StagedMount) -> None:
    mounts = _option_values(container_argv(IMAGE, mount, "n"), "-v")

    writable = {entry.rsplit(":", 2)[1] for entry in mounts if entry.endswith(":rw")}
    assert writable == {"/socalytics/result", "/socalytics/scratch"}
    assert all(entry.endswith((":ro", ":rw")) for entry in mounts)


def test_run_passes_restricted_arguments_without_pull(
    engine: FakeEngine, mount: StagedMount, run_folder: Path
) -> None:
    outcome = run_container(IMAGE, mount, run_folder, 30)

    assert outcome.status == "succeeded"
    assert outcome.failure_category is None
    assert outcome.facts is not None and outcome.facts.valid
    runs = engine.calls_of("run")
    assert len(runs) == 1
    argv = runs[0]
    assert _option_values(argv, "--pull") == ["never"]
    assert _option_values(argv, "--network") == ["none"]
    assert "--read-only" in argv
    assert _option_values(argv, "--cap-drop") == ["ALL"]
    assert argv[-1] == IMAGE
    assert not engine.calls_of("pull")
    assert not engine.calls_of("kill")


def test_present_image_is_accepted_without_pull(engine: FakeEngine) -> None:
    ensure_image_present(IMAGE)

    assert engine.calls() == [["image", "inspect", IMAGE]]


def test_missing_image_refuses_the_configuration(engine: FakeEngine) -> None:
    other = "registry.example/other@sha256:" + "b" * 64

    with pytest.raises(ConfigRefusal) as refusal:
        ensure_image_present(other)

    assert any(other in problem and "never pulls" in problem for problem in refusal.value.problems)
    assert engine.calls() == [["image", "inspect", other]]
    assert not engine.calls_of("pull")


def test_unavailable_engine_refuses_the_configuration(tmp_path: Path, monkeypatch: pytest.MonkeyPatch) -> None:
    empty = tmp_path / "empty"
    empty.mkdir()
    monkeypatch.setenv("PATH", str(empty))

    with pytest.raises(ConfigRefusal):
        ensure_image_present(IMAGE)


def test_overrun_kills_the_named_container(
    engine: FakeEngine, monkeypatch: pytest.MonkeyPatch, mount: StagedMount, run_folder: Path
) -> None:
    engine.mode(monkeypatch, "hang")

    outcome = run_container(IMAGE, mount, run_folder, 1)

    assert outcome.status == "failed"
    assert outcome.failure_category == TIMEOUT
    runs = engine.calls_of("run")
    kills = engine.calls_of("kill")
    assert len(runs) == 1 and len(kills) == 1
    assert kills[0] == ["kill", _option_values(runs[0], "--name")[0]]


def test_failure_takes_the_sdk_category(
    engine: FakeEngine, monkeypatch: pytest.MonkeyPatch, mount: StagedMount, run_folder: Path
) -> None:
    engine.mode(monkeypatch, "fail")

    outcome = run_container(IMAGE, mount, run_folder, 30)

    assert outcome.status == "failed"
    assert outcome.exit_code == 3
    assert outcome.failure_category not in (None, TIMEOUT, EXECUTION_FAILED, INVALID_RESULT)
    assert not engine.calls_of("kill")


def test_missing_facts_are_an_invalid_result(
    engine: FakeEngine, monkeypatch: pytest.MonkeyPatch, mount: StagedMount, run_folder: Path
) -> None:
    engine.mode(monkeypatch, "silent")

    outcome = run_container(IMAGE, mount, run_folder, 30)

    assert outcome.status == "failed"
    assert outcome.failure_category == INVALID_RESULT
    assert not engine.calls_of("kill")


def test_every_execution_gets_a_fresh_container_name(
    engine: FakeEngine, mount: StagedMount, run_folder: Path
) -> None:
    run_container(IMAGE, mount, run_folder, 30)
    run_container(IMAGE, mount, run_folder, 30)

    names = [_option_values(call, "--name")[0] for call in engine.calls_of("run")]
    assert len(set(names)) == 2
    assert all(name.startswith("analyst-evaluation-") for name in names)
