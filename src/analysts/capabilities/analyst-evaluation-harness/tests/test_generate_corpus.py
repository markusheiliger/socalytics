from __future__ import annotations

import importlib.util
import json
import shutil
from pathlib import Path
from types import ModuleType

import pytest

from analyst_evaluation_harness.__main__ import main
from analyst_evaluation_harness.corpus import load_corpus

SCRIPT = Path(__file__).resolve().parents[1] / "scripts" / "generate_corpus.py"

pytestmark = pytest.mark.skipif(shutil.which("ffmpeg") is None, reason="the pinned ffmpeg is not on PATH")


@pytest.fixture(scope="module")
def generator() -> ModuleType:
    spec = importlib.util.spec_from_file_location("generate_corpus", SCRIPT)
    assert spec is not None and spec.loader is not None
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


@pytest.fixture(scope="module")
def corpora(tmp_path_factory: pytest.TempPathFactory, generator: ModuleType) -> tuple[Path, Path]:
    first = tmp_path_factory.mktemp("corpus") / "a"
    second = tmp_path_factory.mktemp("corpus") / "b"
    assert generator.main(["--output", str(first)]) == 0
    assert generator.main(["--output", str(second)]) == 0
    return first, second


def test_two_runs_give_identical_manifests_and_clip_digests(corpora: tuple[Path, Path]) -> None:
    first, second = corpora

    assert (first / "corpus-manifest.json").read_bytes() == (second / "corpus-manifest.json").read_bytes()
    clips = sorted(path.name for path in first.glob("*.mp4"))
    assert len(clips) == 8
    assert clips == sorted(path.name for path in second.glob("*.mp4"))
    for name in clips:
        assert (first / name).read_bytes() == (second / name).read_bytes()


def test_validate_corpus_accepts_the_generated_corpus(corpora: tuple[Path, Path], capsys) -> None:
    manifest_path = corpora[0] / "corpus-manifest.json"

    assert main(["validate-corpus", "--corpus", str(manifest_path)]) == 0

    manifest = load_corpus(manifest_path)
    assert capsys.readouterr().out.count(manifest.identity) == 1
    assert {clip.profile for clip in manifest.clips} == {"fixed", "broadcast"}
    assert sum(1 for clip in manifest.clips if not clip.labels) == 1


def test_changing_one_byte_of_a_clip_is_refused_naming_the_clip(
    corpora: tuple[Path, Path], tmp_path: Path, capsys
) -> None:
    copy = tmp_path / "corpus"
    shutil.copytree(corpora[0], copy)
    clip = copy / "broadcast-2.mp4"
    content = bytearray(clip.read_bytes())
    content[-1] ^= 0xFF
    clip.write_bytes(bytes(content))

    assert main(["validate-corpus", "--corpus", str(copy / "corpus-manifest.json")]) == 2

    clips = json.loads((copy / "corpus-manifest.json").read_text(encoding="utf-8"))["clips"]
    index = next(i for i, c in enumerate(clips) if c["clipId"] == "broadcast-2")
    problems = capsys.readouterr().err.strip().splitlines()
    assert len(problems) == 1
    assert problems[0].startswith(f"digest-mismatch at /clips/{index}/digest")


def test_non_empty_output_directory_is_refused(corpora: tuple[Path, Path], generator: ModuleType, capsys) -> None:
    assert generator.main(["--output", str(corpora[0])]) == 2
    assert "must be empty" in capsys.readouterr().err
