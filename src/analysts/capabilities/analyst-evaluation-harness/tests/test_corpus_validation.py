from __future__ import annotations

import copy
import json
import os
from collections.abc import Callable
from pathlib import Path
from typing import Any

import pytest

from analyst_evaluation_harness.corpus import (
    CorpusValidationError,
    load_corpus,
    manifest_identity,
    parse_manifest,
    validate_manifest,
)
from support.builders import Corpus, build_manifest, clip_bytes, digest_of, label, seal, write_corpus

CorpusFactory = Callable[..., Corpus]


def codes(problems: Any) -> list[str]:
    return [problem.code for problem in problems]


def problem_at(problems: Any, code: str, path: str) -> bool:
    return any(p.code == code and p.path == path for p in problems)


def test_valid_corpus_loads_with_both_profiles(corpus_factory: CorpusFactory) -> None:
    corpus = corpus_factory()

    manifest = load_corpus(corpus.manifest_path)

    assert manifest.identity == corpus.manifest["identity"]
    assert {clip.profile for clip in manifest.clips} == {"fixed", "broadcast"}
    assert all(clip.path.is_file() for clip in manifest.clips)


def test_clip_with_empty_label_list_is_valid(corpus_factory: CorpusFactory) -> None:
    corpus = corpus_factory(labels={"fixed-a": []})

    manifest = load_corpus(corpus.manifest_path)

    clip = next(clip for clip in manifest.clips if clip.clip_id == "fixed-a")
    assert clip.labels == ()


def test_corpus_of_one_profile_is_valid(corpus_factory: CorpusFactory) -> None:
    clips = (("only-a", "broadcast", 30.0, 5000, 0), ("only-b", "broadcast", 30.0, 5000, 0))
    corpus = corpus_factory(clips=clips)

    manifest = load_corpus(corpus.manifest_path)

    assert {clip.profile for clip in manifest.clips} == {"broadcast"}


def test_range_label_is_valid(corpus_factory: CorpusFactory) -> None:
    ranged = {"labelKind": "test-event", "rangeStartMs": 1000, "rangeEndMs": 2000}
    corpus = corpus_factory(labels={"fixed-a": [ranged]})

    assert load_corpus(corpus.manifest_path).clips[0].labels[0].range_end_ms == 2000


def test_every_refusal_is_reported_at_once(tmp_path: Path) -> None:
    manifest = build_manifest()
    clips = manifest["clips"]
    del manifest["rights"]["origin"]
    manifest["rights"]["license"] = ""
    del clips[0]["rights"]["production"]
    clips[1]["profile"] = "mystery"
    clips[2]["clipId"] = clips[0]["clipId"]
    clips[2]["digest"] = "sha-256:abc"
    clips[3]["file"] = "../outside.bin"
    clips.append({**copy.deepcopy(clips[0]), "clipId": "absolute", "file": "/etc/hostname"})
    clips.append({**copy.deepcopy(clips[0]), "clipId": "missing", "file": "clips/missing.bin"})
    clips.append(
        {**copy.deepcopy(clips[0]), "clipId": "changed", "file": "clips/changed.bin", "digest": digest_of(b"other")}
    )
    corpus = write_corpus(tmp_path / "corpus", manifest)
    (corpus.root / "clips" / "changed.bin").write_bytes(b"different bytes")
    (corpus.root / "clips" / "missing.bin").unlink()

    with pytest.raises(CorpusValidationError) as raised:
        load_corpus(corpus.manifest_path)

    problems = raised.value.problems
    assert problem_at(problems, "missing-rights-field", "/rights/origin")
    assert problem_at(problems, "missing-rights-field", "/rights/license")
    assert problem_at(problems, "missing-rights-field", "/clips/0/rights/production")
    assert problem_at(problems, "unknown-profile", "/clips/1/profile")
    assert problem_at(problems, "duplicate-clip-id", "/clips/2/clipId")
    assert problem_at(problems, "invalid-digest", "/clips/2/digest")
    assert problem_at(problems, "invalid-file-path", "/clips/3/file")
    assert problem_at(problems, "invalid-file-path", "/clips/4/file")
    assert problem_at(problems, "file-missing", "/clips/5/file")
    assert problem_at(problems, "digest-mismatch", "/clips/6/digest")
    for problem in problems:
        assert str(problem) in str(raised.value)


def test_schema_violation_is_reported(corpus_factory: CorpusFactory) -> None:
    manifest = build_manifest()
    manifest["surprise"] = True
    manifest["clips"][0]["durationMs"] = 0
    corpus = corpus_factory(manifest)

    problems = validate_manifest(corpus.manifest, corpus.root)

    assert problem_at(problems, "schema-violation", "/")
    assert problem_at(problems, "schema-violation", "/clips/0/durationMs")


def test_empty_clip_list_is_refused(corpus_factory: CorpusFactory) -> None:
    manifest = build_manifest()
    manifest["clips"] = []
    seal(manifest)

    problems = validate_manifest(manifest, corpus_factory(manifest).root)

    assert problem_at(problems, "schema-violation", "/clips")


def test_label_with_position_and_range_is_refused(corpus_factory: CorpusFactory) -> None:
    both = label(rangeStartMs=1, rangeEndMs=2)
    corpus = corpus_factory(labels={"fixed-a": [both]})

    problems = validate_manifest(corpus.manifest, corpus.root)

    assert problem_at(problems, "schema-violation", "/clips/0/labels/0")


def test_identity_mismatch_is_refused(corpus_factory: CorpusFactory) -> None:
    manifest = build_manifest()
    manifest["corpusVersion"] = "2.0.0"
    corpus = corpus_factory(manifest)

    problems = validate_manifest(corpus.manifest, corpus.root)

    assert codes(problems) == ["identity-mismatch"]
    assert problems[0].path == "/identity"


def test_malformed_identity_is_refused(corpus_factory: CorpusFactory) -> None:
    manifest = build_manifest()
    manifest["identity"] = "sha-256:nothex"
    corpus = corpus_factory(manifest)

    problems = validate_manifest(corpus.manifest, corpus.root)

    assert problem_at(problems, "invalid-digest", "/identity")


def test_identity_ignores_the_identity_field_and_key_order() -> None:
    manifest = build_manifest()
    reordered = dict(reversed(list(manifest.items())))
    reordered["identity"] = "sha-256:" + "f" * 64

    assert manifest_identity(manifest) == manifest["identity"]
    assert manifest_identity(reordered) == manifest["identity"]


def test_changed_clip_names_that_clip(corpus_factory: CorpusFactory) -> None:
    corpus = corpus_factory()
    corpus.clip_path("broadcast-a").write_bytes(clip_bytes("broadcast-a") + b"tampered")
    index = next(i for i, clip in enumerate(corpus.manifest["clips"]) if clip["clipId"] == "broadcast-a")

    with pytest.raises(CorpusValidationError) as raised:
        load_corpus(corpus.manifest_path)

    assert [(p.code, p.path) for p in raised.value.problems] == [("digest-mismatch", f"/clips/{index}/digest")]


@pytest.mark.skipif(os.name == "nt" or os.geteuid() == 0, reason="permissions are not enforced")
def test_unreadable_file_is_refused(corpus_factory: CorpusFactory) -> None:
    corpus = corpus_factory()
    target = corpus.clip_path("fixed-a")
    target.chmod(0)
    try:
        problems = validate_manifest(corpus.manifest, corpus.root)
    finally:
        target.chmod(0o600)

    assert problem_at(problems, "file-unreadable", "/clips/0/file")


@pytest.mark.skipif(os.name == "nt", reason="symbolic links need privileges")
def test_file_resolving_outside_the_corpus_is_refused(tmp_path: Path) -> None:
    outside = tmp_path / "outside.bin"
    outside.write_bytes(clip_bytes("fixed-a"))
    corpus = write_corpus(tmp_path / "corpus")
    corpus.clip_path("fixed-a").unlink()
    corpus.clip_path("fixed-a").symlink_to(outside)

    problems = validate_manifest(corpus.manifest, corpus.root)

    assert problem_at(problems, "invalid-file-path", "/clips/0/file")


def test_manifest_that_is_not_an_object_is_refused(tmp_path: Path) -> None:
    problems = validate_manifest([], tmp_path)

    assert codes(problems) == ["schema-violation"]


def test_missing_manifest_file_is_refused(tmp_path: Path) -> None:
    with pytest.raises(CorpusValidationError) as raised:
        load_corpus(tmp_path / "absent.json")

    assert codes(raised.value.problems) == ["manifest-unreadable"]


def test_manifest_that_is_not_json_is_refused(tmp_path: Path) -> None:
    path = tmp_path / "manifest.json"
    path.write_text("{not json", encoding="utf-8")

    with pytest.raises(CorpusValidationError) as raised:
        load_corpus(path)

    assert codes(raised.value.problems) == ["schema-violation"]


def test_parse_manifest_refuses_before_building_a_model(corpus_factory: CorpusFactory) -> None:
    manifest = json.loads(json.dumps(build_manifest()))
    manifest["clips"][0]["profile"] = "mystery"
    seal(manifest)
    corpus = corpus_factory(manifest)

    with pytest.raises(CorpusValidationError):
        parse_manifest(manifest, corpus.root)
