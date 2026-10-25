from __future__ import annotations

from collections.abc import Callable
from pathlib import Path

import pytest

from support.mounts import Mount, build_mount


@pytest.fixture
def mount_factory(tmp_path: Path) -> Callable[..., Mount]:
    """Build a mount root under the test's temporary directory; each call gets its own root."""
    counter = 0

    def create(media: Path, **options: object) -> Mount:
        nonlocal counter
        counter += 1
        return build_mount(tmp_path / f"mount-{counter}", media, **options)  # type: ignore[arg-type]

    return create
