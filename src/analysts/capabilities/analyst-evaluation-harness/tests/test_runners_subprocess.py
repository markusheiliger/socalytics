from __future__ import annotations

import json
import sys
from collections.abc import Callable
from pathlib import Path

import pytest

from analyst_evaluation_harness.config import ConfigRefusal
from analyst_evaluation_harness.runners import run_subprocess, subprocess_argv
from analyst_evaluation_harness.staging import StagedMount, stage_mount
from support.builders import ScriptedCapability, clip_bytes, fact


@pytest.fixture
def run_folder(tmp_path: Path) -> Path:
    folder = tmp_path / "run"
    folder.mkdir()
    return folder


@pytest.fixture
def mount(run_folder: Path, tmp_path: Path) -> StagedMount:
    clip_file = tmp_path / "clip.bin"
    clip_file.write_bytes(clip_bytes("clip"))
    return stage_mount(
        run_folder,
        "mount-1",
        clip_file=clip_file,
        match_start_ms=10000,
        duration_ms=5000,
        capability_id="test-capability",
        capability_version="1.0.0",
        output_type="foundational-facts",
    )


def _run(capability: ScriptedCapability, mount: StagedMount, run_folder: Path, limit: float = 30):
    return run_subprocess(capability.command, mount, run_folder, limit)


def test_success_returns_validated_facts(
    capability_factory: Callable[..., ScriptedCapability], mount: StagedMount, run_folder: Path
) -> None:
    capability = capability_factory(facts=[fact(10500, 11500), fact(12000, 13000)])

    outcome = _run(capability, mount, run_folder)

    assert outcome.status == "succeeded"
    assert outcome.failure_category is None
    assert outcome.exit_code == 0
    assert outcome.facts is not None and outcome.facts.valid
    assert [(item.start_ms, item.end_ms) for item in outcome.facts.facts] == [(10500, 11500), (12000, 13000)]
    assert outcome.duration_ms >= 0
    assert capability.invocations == 1


def test_success_without_facts_is_an_empty_valid_result(
    capability_factory: Callable[..., ScriptedCapability], mount: StagedMount, run_folder: Path
) -> None:
    outcome = _run(capability_factory(facts=[]), mount, run_folder)

    assert outcome.status == "succeeded"
    assert outcome.facts is not None and outcome.facts.facts == ()


def test_non_zero_exit_reports_the_sdk_category(
    capability_factory: Callable[..., ScriptedCapability], mount: StagedMount, run_folder: Path
) -> None:
    outcome = _run(capability_factory(fail="input-invalid"), mount, run_folder)

    assert outcome.status == "failed"
    assert outcome.failure_category == "input-invalid"
    assert outcome.exit_code == 2
    assert outcome.facts is None


def test_non_zero_exit_without_category_is_execution_failed(
    capability_factory: Callable[..., ScriptedCapability], mount: StagedMount, run_folder: Path
) -> None:
    outcome = _run(capability_factory(crash=True), mount, run_folder)

    assert outcome.status == "failed"
    assert outcome.failure_category == "execution-failed"
    assert outcome.exit_code == 1


def test_unstartable_command_is_execution_failed(mount: StagedMount, run_folder: Path) -> None:
    outcome = run_subprocess("/nonexistent/launcher {mount}", mount, run_folder, 5)

    assert outcome.status == "failed"
    assert outcome.failure_category == "execution-failed"
    assert outcome.exit_code is None


def test_invalid_result_is_reported_after_a_zero_exit(
    capability_factory: Callable[..., ScriptedCapability], mount: StagedMount, run_folder: Path
) -> None:
    outcome = _run(capability_factory(malformed=True), mount, run_folder)

    assert outcome.status == "failed"
    assert outcome.failure_category == "invalid-result"
    assert outcome.exit_code == 0
    assert outcome.facts is not None and not outcome.facts.valid


def test_hang_is_bounded_by_the_time_limit(
    capability_factory: Callable[..., ScriptedCapability], mount: StagedMount, run_folder: Path
) -> None:
    outcome = _run(capability_factory(hang=True), mount, run_folder, limit=1)

    assert outcome.status == "failed"
    assert outcome.failure_category == "timeout"
    assert outcome.exit_code is None
    assert outcome.facts is None
    assert 1000 <= outcome.duration_ms < 9000


def test_mount_placeholder_is_replaced_in_every_token(mount: StagedMount) -> None:
    argv = subprocess_argv("tool --root={mount} {mount}/job.json 'literal arg'", mount)

    assert argv == ["tool", f"--root={mount.root}", f"{mount.root}/job.json", "literal arg"]


def test_command_is_split_without_a_shell(mount: StagedMount) -> None:
    argv = subprocess_argv("tool; rm -rf / $HOME", mount)

    assert argv == ["tool;", "rm", "-rf", "/", "$HOME"]


@pytest.mark.parametrize("command", ["", "   ", "tool 'unterminated"])
def test_unusable_command_refuses_the_configuration(command: str, mount: StagedMount) -> None:
    with pytest.raises(ConfigRefusal):
        subprocess_argv(command, mount)


def test_run_uses_the_run_folder_and_only_path_in_the_environment(
    mount: StagedMount, run_folder: Path, tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    monkeypatch.setenv("SOCALYTICS_SECRET", "must-not-leak")
    probe = tmp_path / "probe.py"
    probe.write_text(
        "import json, os, sys\n"
        "from pathlib import Path\n"
        "Path(sys.argv[1], 'probe.json').write_text(json.dumps({'cwd': os.getcwd(), 'env': sorted(os.environ)}))\n",
        encoding="utf-8",
    )

    outcome = run_subprocess(f"{sys.executable} {probe} {{mount}}", mount, run_folder, 30)

    assert outcome.exit_code == 0
    seen = json.loads((mount.root / "probe.json").read_text(encoding="utf-8"))
    assert Path(seen["cwd"]).resolve() == run_folder.resolve()
    assert "SOCALYTICS_SECRET" not in seen["env"]
    assert "PATH" in seen["env"]
