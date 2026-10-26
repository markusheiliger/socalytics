from __future__ import annotations

from collections.abc import Callable
from pathlib import Path
from typing import Any

import pytest

from support.builders import Corpus, ScriptedCapability, build_manifest, write_capability, write_corpus


@pytest.fixture
def corpus_factory(tmp_path: Path) -> Callable[..., Corpus]:
    """Write a corpus under the test's temporary directory; each call gets its own folder."""
    counter = 0

    def create(manifest: dict[str, Any] | None = None, **options: Any) -> Corpus:
        nonlocal counter
        counter += 1
        document = manifest if manifest is not None else build_manifest(**options)
        return write_corpus(tmp_path / f"corpus-{counter}", document)

    return create


@pytest.fixture
def capability_factory(tmp_path: Path) -> Callable[..., ScriptedCapability]:
    """Write a scripted capability under the test's temporary directory; each call gets its own folder."""
    counter = 0

    def create(**scenario: Any) -> ScriptedCapability:
        nonlocal counter
        counter += 1
        return write_capability(tmp_path / f"capability-{counter}", **scenario)

    return create
