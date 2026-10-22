"""Segment media mount: verified loading, time mapping, and the input error categories (FR-021)."""

from __future__ import annotations

import json
from collections.abc import Callable
from pathlib import Path
from typing import Any

import pytest

from fixtures.mounts import CONTRACTS, Mount, build_mount, read_json, sha256_digest
from socalytics_analyst import errors
from socalytics_analyst.context import build_context, load_job
from socalytics_analyst.media import SegmentMedia, load_media

MEDIA_JOB = "segment-job-with-media.json"
MEDIA_BYTES = b"not-really-an-mp4"
MANIFEST_EXAMPLES = CONTRACTS / "segments" / "segment-manifest" / "v1" / "examples" / "valid"


def _manifest(mutate: Callable[[dict[str, Any]], None] | None = None, media: bytes = MEDIA_BYTES) -> bytes:
    document = read_json(MANIFEST_EXAMPLES / "segment-manifest.json")
    document["media"]["digest"] = sha256_digest(media)
    document["media"]["sizeBytes"] = len(media)
    if mutate:
        mutate(document)
    return json.dumps(document, indent=2).encode("utf-8")


def _mount(tmp_path: Path, manifest: bytes | None, media: bytes | None = MEDIA_BYTES, *, declare: bool = True) -> Mount:
    def adjust(job: dict[str, Any]) -> None:
        if declare and manifest is not None:
            job["segmentMedia"]["manifestChecksum"] = sha256_digest(manifest)
        if not declare:
            job.pop("segmentMedia")

    mount = build_mount(tmp_path, job_example_name=MEDIA_JOB, adjust_job=adjust)
    if manifest is not None:
        mount.stage_media(manifest, media)
    return mount


def _load(mount: Mount) -> SegmentMedia | None:
    return load_media(mount.root, load_job(mount.root))


def _category(mount: Mount) -> str:
    with pytest.raises(errors.InputError) as raised:
        _load(mount)
    assert raised.value.exit_code == 2
    return raised.value.category


def test_verified_media_is_loaded(tmp_path: Path) -> None:
    mount = _mount(tmp_path, _manifest())

    media = _load(mount)

    assert media is not None
    assert media.path == tmp_path / "media" / "segment.mp4"
    assert media.manifest.identity.segment_number == 1
    assert media.manifest.media.media_type == "video/mp4"


def test_clipped_manifest_example_is_loaded(tmp_path: Path) -> None:
    document = read_json(MANIFEST_EXAMPLES / "clipped-segment-manifest.json")
    document["media"]["digest"] = sha256_digest(MEDIA_BYTES)
    document["media"]["sizeBytes"] = len(MEDIA_BYTES)

    media = _load(_mount(tmp_path, json.dumps(document).encode("utf-8")))

    assert media is not None
    assert len(media.manifest.timeline_spans) >= 1


def test_manifest_is_frozen(tmp_path: Path) -> None:
    media = _load(_mount(tmp_path, _manifest()))
    assert media is not None
    with pytest.raises(Exception):
        media.manifest.identity = None  # type: ignore[misc]


def test_to_match_ms_maps_inside_spans_and_none_outside(tmp_path: Path) -> None:
    def spans(document: dict[str, Any]) -> None:
        document["timelineSpans"] = [
            {"mediaStartMs": 3600000, "mediaEndMs": 3605000, "matchStartMs": 0},
            {"mediaStartMs": 3606000, "mediaEndMs": 3610000, "matchStartMs": 6000},
        ]

    media = _load(_mount(tmp_path, _manifest(spans)))

    assert media is not None
    assert media.to_match_ms(3600000) == 0
    assert media.to_match_ms(3602500) == 2500
    assert media.to_match_ms(3605000) is None
    assert media.to_match_ms(3605500) is None
    assert media.to_match_ms(3606000) == 6000
    assert media.to_match_ms(3609999) == 9999
    assert media.to_match_ms(3610000) is None
    assert media.to_match_ms(3599500) is None


def test_job_without_media_has_none(tmp_path: Path) -> None:
    mount = _mount(tmp_path, None, declare=False)
    assert _load(mount) is None


def test_job_without_media_and_with_directory_is_unexpected(tmp_path: Path) -> None:
    mount = _mount(tmp_path, _manifest(), declare=False)
    assert _category(mount) == errors.SEGMENT_MEDIA_UNEXPECTED


def test_absent_directory_is_missing(tmp_path: Path) -> None:
    assert _category(_mount(tmp_path, None)) == errors.SEGMENT_MEDIA_MISSING


def test_absent_media_file_is_missing(tmp_path: Path) -> None:
    assert _category(_mount(tmp_path, _manifest(), None)) == errors.SEGMENT_MEDIA_MISSING


def test_absent_manifest_file_is_missing(tmp_path: Path) -> None:
    mount = _mount(tmp_path, _manifest())
    (tmp_path / "media" / "segment-manifest.json").unlink()
    assert _category(mount) == errors.SEGMENT_MEDIA_MISSING


def test_manifest_checksum_differing_from_the_job_is_a_mismatch(tmp_path: Path) -> None:
    mount = _mount(tmp_path, _manifest())
    (tmp_path / "media" / "segment-manifest.json").write_bytes(_manifest(lambda d: d["producer"].update(toolchain="x")))
    assert _category(mount) == errors.SEGMENT_MANIFEST_MISMATCH


def test_media_with_another_digest_is_refused(tmp_path: Path) -> None:
    mount = _mount(tmp_path, _manifest(), b"x" * len(MEDIA_BYTES))
    assert _category(mount) == errors.SEGMENT_MEDIA_DIGEST_MISMATCH


def test_media_with_another_size_is_refused(tmp_path: Path) -> None:
    def wrong_size(document: dict[str, Any]) -> None:
        document["media"]["sizeBytes"] += 1

    assert _category(_mount(tmp_path, _manifest(wrong_size))) == errors.SEGMENT_MEDIA_DIGEST_MISMATCH


@pytest.mark.parametrize(
    "mutate",
    [
        lambda d: d.pop("timelineSpans"),
        lambda d: d["identity"].update(segmentNumber=0),
        lambda d: d["media"].update(mediaType="audio/mpeg"),
        lambda d: d.update(unknown=1),
    ],
)
def test_schema_violating_manifest_is_invalid(tmp_path: Path, mutate: Callable[[dict[str, Any]], None]) -> None:
    assert _category(_mount(tmp_path, _manifest(mutate))) == errors.SEGMENT_MANIFEST_INVALID


def test_not_json_manifest_is_invalid(tmp_path: Path) -> None:
    assert _category(_mount(tmp_path, b"{not json")) == errors.SEGMENT_MANIFEST_INVALID


def test_newer_minor_version_is_unsupported(tmp_path: Path) -> None:
    mount = _mount(tmp_path, _manifest(lambda d: d.update(contractVersion="1.1.0")))
    assert _category(mount) == errors.UNSUPPORTED_VERSION


def test_other_major_version_is_unsupported(tmp_path: Path) -> None:
    mount = _mount(tmp_path, _manifest(lambda d: d.update(contractVersion="2.0.0")))
    assert _category(mount) == errors.UNSUPPORTED_VERSION


def test_error_messages_carry_no_manifest_values(tmp_path: Path) -> None:
    mount = _mount(tmp_path, _manifest(lambda d: d["producer"].update(toolchain="")))
    with pytest.raises(errors.InputError) as raised:
        _load(mount)
    assert "ffmpeg" not in str(raised.value)


def test_build_context_exposes_media(tmp_path: Path) -> None:
    mount = _mount(tmp_path, _manifest())
    job = load_job(mount.root)

    context = build_context(mount.root, job, None, object())  # type: ignore[arg-type]

    assert context.media is not None
    assert context.media.path.name == "segment.mp4"


def test_build_context_without_media_has_none(tmp_path: Path) -> None:
    mount = _mount(tmp_path, None, declare=False)

    context = build_context(mount.root, load_job(mount.root), None, object())  # type: ignore[arg-type]

    assert context.media is None
