"""Runners that execute a capability on one staged mount root (research R2)."""

from __future__ import annotations

import json
import os
import shlex
import signal
import subprocess
import time
import uuid
from collections.abc import Iterable
from dataclasses import dataclass
from pathlib import Path

from socalytics_analyst.models.facts import Fact

from .config import ConfigRefusal
from .facts import EXECUTION_FAILED, FactsOutcome, classify_failure, read_facts
from .staging import StagedMount

MOUNT_PLACEHOLDER = "{mount}"
CONTAINER_ROOT = "/socalytics"
CONTAINER_PIDS_LIMIT = 256
CONTAINER_MEMORY_MEBIBYTES = 512
_STDERR_LIMIT = 64 * 1024
_KILL_GRACE_SECONDS = 10
PREDICT_NOTHING = "predict-nothing"
PREDICT_EVERYTHING = "predict-everything"
BUILTIN_RULES = (PREDICT_NOTHING, PREDICT_EVERYTHING)
_SECOND_MS = 1000


@dataclass(frozen=True)
class ExecutionOutcome:
    """The outcome of one execution: `succeeded` or `failed` with a failure category, plus its duration."""

    status: str
    failure_category: str | None
    duration_ms: int
    exit_code: int | None = None
    facts: FactsOutcome | None = None


def _tail(text: bytes | None) -> str:
    return (text or b"")[-_STDERR_LIMIT:].decode("utf-8", errors="replace")


def _outcome(
    mount: StagedMount, *, started: float, timed_out: bool, exit_code: int | None, stderr: str
) -> ExecutionOutcome:
    duration_ms = int((time.monotonic() - started) * 1000)
    facts = None if timed_out or exit_code != 0 else read_facts(mount.result)
    category = classify_failure(timed_out=timed_out, exit_code=exit_code, stderr=stderr, facts=facts)
    return ExecutionOutcome(
        status="failed" if category else "succeeded",
        failure_category=category,
        duration_ms=duration_ms,
        exit_code=exit_code,
        facts=facts,
    )


def _kill_group(process: subprocess.Popen) -> None:
    try:
        os.killpg(process.pid, signal.SIGKILL)
    except (ProcessLookupError, PermissionError):
        process.kill()


def _run_bounded(
    argv: list[str], *, cwd: Path, env: dict[str, str] | None, time_limit_seconds: float, on_overrun=None
) -> tuple[bool, int | None, str]:
    """Run `argv`; returns (timed out, exit code, stderr tail). The process group is killed on overrun."""
    try:
        process = subprocess.Popen(
            argv,
            cwd=cwd,
            env=env,
            stdin=subprocess.DEVNULL,
            stdout=subprocess.DEVNULL,
            stderr=subprocess.PIPE,
            start_new_session=True,
        )
    except OSError as exc:
        return False, None, f"the command cannot be started: {exc.strerror or exc}"
    try:
        _, stderr = process.communicate(timeout=time_limit_seconds)
        return False, process.returncode, _tail(stderr)
    except subprocess.TimeoutExpired:
        if on_overrun is not None:
            on_overrun()
        _kill_group(process)
        try:
            _, stderr = process.communicate(timeout=_KILL_GRACE_SECONDS)
        except subprocess.TimeoutExpired:
            stderr = b""
        return True, None, _tail(stderr)


def subprocess_argv(command: str, mount: StagedMount) -> list[str]:
    """Split `command` with `shlex` and replace `{mount}` by the mount root."""
    try:
        tokens = shlex.split(command)
    except ValueError as exc:
        raise ConfigRefusal([f"the local command cannot be split: {exc}"]) from None
    if not tokens:
        raise ConfigRefusal(["the local command is empty."])
    return [token.replace(MOUNT_PLACEHOLDER, str(mount.root)) for token in tokens]


def run_subprocess(
    command: str, mount: StagedMount, run_folder: Path, time_limit_seconds: float
) -> ExecutionOutcome:
    """Run a local command with the run folder as working directory and `PATH` as the only environment variable."""
    argv = subprocess_argv(command, mount)
    env = {"PATH": os.environ.get("PATH", os.defpath)}
    started = time.monotonic()
    timed_out, exit_code, stderr = _run_bounded(
        argv, cwd=Path(run_folder), env=env, time_limit_seconds=time_limit_seconds
    )
    return _outcome(mount, started=started, timed_out=timed_out, exit_code=exit_code, stderr=stderr)


def ensure_image_present(image_reference: str) -> None:
    """Refuse the configuration when the image is absent from the local engine; never pulls."""
    try:
        completed = subprocess.run(
            ["docker", "image", "inspect", image_reference],
            stdin=subprocess.DEVNULL,
            stdout=subprocess.DEVNULL,
            stderr=subprocess.DEVNULL,
            check=False,
        )
    except OSError:
        raise ConfigRefusal(["the container engine (docker) is not available."]) from None
    if completed.returncode != 0:
        raise ConfigRefusal([f"the image {image_reference} is not present locally; the harness never pulls images."])


def container_argv(image_reference: str, mount: StagedMount, name: str) -> list[str]:
    """The restricted `docker run` arguments for one execution."""
    argv = [
        "docker",
        "run",
        "--rm",
        "--name",
        name,
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
        argv += ["--user", f"{os.getuid()}:{os.getgid()}"]
    argv += [
        "--pids-limit",
        str(CONTAINER_PIDS_LIMIT),
        "--memory",
        f"{CONTAINER_MEMORY_MEBIBYTES}m",
        "-v",
        f"{mount.job_path}:{CONTAINER_ROOT}/job.json:ro",
        "-v",
        f"{mount.attempt_path}:{CONTAINER_ROOT}/attempt.json:ro",
        "-v",
        f"{mount.media_folder}:{CONTAINER_ROOT}/media:ro",
        "-v",
        f"{mount.result}:{CONTAINER_ROOT}/result:rw",
        "-v",
        f"{mount.scratch}:{CONTAINER_ROOT}/scratch:rw",
        image_reference,
    ]
    return argv


def run_container(
    image_reference: str, mount: StagedMount, run_folder: Path, time_limit_seconds: float
) -> ExecutionOutcome:
    """Run the pinned image with the Manager-like restrictions; `docker kill` stops the named container on overrun."""
    name = f"analyst-evaluation-{uuid.uuid4().hex}"
    argv = container_argv(image_reference, mount, name)

    def kill_container() -> None:
        subprocess.run(
            ["docker", "kill", name],
            stdin=subprocess.DEVNULL,
            stdout=subprocess.DEVNULL,
            stderr=subprocess.DEVNULL,
            check=False,
            timeout=_KILL_GRACE_SECONDS,
        )

    started = time.monotonic()
    timed_out, exit_code, stderr = _run_bounded(
        argv, cwd=Path(run_folder), env=None, time_limit_seconds=time_limit_seconds, on_overrun=kill_container
    )
    return _outcome(mount, started=started, timed_out=timed_out, exit_code=exit_code, stderr=stderr)


def _covered_window(mount: StagedMount) -> tuple[int, int]:
    """The covered match-time window `[matchStartMs, matchStartMs + duration)` of the staged segment manifest."""
    manifest = json.loads(mount.manifest_path.read_text(encoding="utf-8"))
    span = manifest["timelineSpans"][0]
    duration_ms = manifest["encodedInterval"]["endMs"] - manifest["encodedInterval"]["startMs"]
    return span["matchStartMs"], span["matchStartMs"] + duration_ms


def run_builtin(rule: str, mount: StagedMount, label_kinds: Iterable[str]) -> ExecutionOutcome:
    """Produce the facts of a built-in comparator rule in process; nothing is executed (FR-007).

    `predict-nothing` has no facts. `predict-everything` has one fact per covered second for every label kind, with an
    empty payload.
    """
    if rule not in BUILTIN_RULES:
        raise ConfigRefusal([f"the built-in comparator rule {rule!r} is not one of {', '.join(BUILTIN_RULES)}."])
    started = time.monotonic()
    facts: list[Fact] = []
    if rule == PREDICT_EVERYTHING:
        try:
            start_ms, end_ms = _covered_window(mount)
        except (OSError, ValueError, KeyError, IndexError, TypeError):
            return ExecutionOutcome(status="failed", failure_category=EXECUTION_FAILED, duration_ms=0)
        for second_start in range(start_ms, end_ms, _SECOND_MS):
            second_end = min(second_start + _SECOND_MS, end_ms)
            facts.extend(
                Fact(kind=kind, start_ms=second_start, end_ms=second_end, confidence=1.0, payload={})
                for kind in sorted(set(label_kinds))
            )
    return ExecutionOutcome(
        status="succeeded",
        failure_category=None,
        duration_ms=int((time.monotonic() - started) * 1000),
        exit_code=0,
        facts=FactsOutcome(facts=tuple(facts)),
    )
