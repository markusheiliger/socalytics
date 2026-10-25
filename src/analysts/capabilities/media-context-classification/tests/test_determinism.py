from __future__ import annotations

import os
import shutil
import subprocess
import sys
from collections.abc import Callable
from pathlib import Path

import pytest
from socalytics_analyst import run

from media_context_classification.__main__ import analyse
from support.media import hard_cuts_clip
from support.mounts import Mount

WINDOW = {"startMs": 0, "endMs": 5000}
SPANS = [{"mediaStartMs": 0, "mediaEndMs": 5000, "matchStartMs": 0}]

RUN_SCRIPT = (
    "import sys\n"
    "from socalytics_analyst import run\n"
    "from media_context_classification.__main__ import analyse\n"
    "run(analyse, output_type='foundational-facts', root=sys.argv[1])\n"
)


@pytest.fixture
def mounts(tmp_path: Path, mount_factory: Callable[..., Mount]) -> Callable[[int], list[Mount]]:
    clip = hard_cuts_clip(tmp_path / "media")

    def create(count: int) -> list[Mount]:
        return [
            mount_factory(
                clip.path,
                nominal_window=WINDOW,
                requested_source_interval=WINDOW,
                encoded_interval=WINDOW,
                timeline_spans=SPANS,
            )
            for _ in range(count)
        ]

    return create


def _run_in_process(mount: Mount) -> int:
    with pytest.raises(SystemExit) as exit_info:
        run(analyse, output_type="foundational-facts", root=mount.root)
    return exit_info.value.code or 0


def _run_pinned(mount: Mount, cpus: str) -> int:
    command = ["taskset", "-c", cpus, sys.executable, "-c", RUN_SCRIPT, str(mount.root)]
    return subprocess.run(command, capture_output=True, check=False).returncode


def _result_bytes(mount: Mount) -> dict[str, bytes]:
    return {name: (mount.result / name).read_bytes() for name in mount.result_entries()}


def test_three_runs_on_the_same_media_yield_byte_identical_results(mounts: Callable[[int], list[Mount]]) -> None:
    runs = mounts(3)

    assert [_run_in_process(mount) for mount in runs] == [0, 0, 0]

    results = [_result_bytes(mount) for mount in runs]
    assert results[0], "the first run wrote no result"
    assert "facts.json" in results[0]
    assert results[0] == results[1] == results[2]


def test_runs_under_different_cpu_limits_yield_byte_identical_results(mounts: Callable[[int], list[Mount]]) -> None:
    if shutil.which("taskset") is None:
        pytest.skip("taskset is not available")
    available = sorted(os.sched_getaffinity(0))
    several = ",".join(str(cpu) for cpu in available[:4])
    runs = mounts(4)

    codes = [
        _run_pinned(runs[0], str(available[0])),
        _run_pinned(runs[1], several),
        _run_pinned(runs[2], str(available[-1])),
        _run_pinned(runs[3], several),
    ]

    assert codes == [0, 0, 0, 0]
    results = [_result_bytes(mount) for mount in runs]
    assert results[0]
    assert all(result == results[0] for result in results)
