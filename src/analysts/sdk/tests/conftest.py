from __future__ import annotations

from collections.abc import Callable
from pathlib import Path

import pytest

from fixtures.mounts import MATCH_JOB_EXAMPLE, SEGMENT_JOB_EXAMPLE, Mount, build_mount


@pytest.fixture
def mount_factory(tmp_path: Path) -> Callable[..., Mount]:
    """Build a mount root under the test's temporary directory; each call gets its own root."""
    counter = 0

    def create(**options: object) -> Mount:
        nonlocal counter
        counter += 1
        return build_mount(tmp_path / f"mount-{counter}", **options)  # type: ignore[arg-type]

    return create


@pytest.fixture
def segment_mount(mount_factory: Callable[..., Mount]) -> Mount:
    return mount_factory(job_example_name=SEGMENT_JOB_EXAMPLE)


@pytest.fixture
def match_mount(mount_factory: Callable[..., Mount]) -> Mount:
    return mount_factory(job_example_name=MATCH_JOB_EXAMPLE)
