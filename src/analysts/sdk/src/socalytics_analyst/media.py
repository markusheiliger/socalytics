"""Segment media: the read-only `media/` mount, verified against the job's `segmentMedia` and its manifest."""

from __future__ import annotations

import hashlib
from dataclasses import dataclass
from pathlib import Path

from . import errors, schemas
from .inputs import _digest_and_size, _model, _parse_json
from .models.job import LogicalJob
from .models.segment import SegmentManifest

MEDIA_DIRECTORY = "media"
MANIFEST_FILE = "segment-manifest.json"
MEDIA_FILE = "segment.mp4"
_MANIFEST_KIND = "segment-manifest"


@dataclass(frozen=True)
class SegmentMedia:
    """The verified materialized segment: its manifest, the media path, and the media-to-match time mapping."""

    manifest: SegmentManifest
    path: Path

    def to_match_ms(self, media_ms: int) -> int | None:
        """Match time of a media time inside a mapped span (half-open), or `None` outside every span."""
        for span in self.manifest.timeline_spans:
            if span.media_start_ms <= media_ms < span.media_end_ms:
                return span.match_start_ms + (media_ms - span.media_start_ms)
        return None


def _regular_file(path: Path) -> bool:
    return path.is_file() and not path.is_symlink()


def _parse_manifest(raw: bytes) -> SegmentManifest:
    try:
        document = _parse_json(raw, MANIFEST_FILE)
        schemas.validate(_MANIFEST_KIND, document)
        return _model(SegmentManifest, document, MANIFEST_FILE)
    except errors.InputError as exc:
        if exc.category == errors.UNSUPPORTED_CONTRACT_VERSION:
            raise errors.InputError(errors.UNSUPPORTED_VERSION, exc.detail) from None
        if exc.category == errors.INPUT_INVALID:
            raise errors.InputError(errors.SEGMENT_MANIFEST_INVALID, exc.detail) from None
        raise


def load_media(root: Path, job: LogicalJob) -> SegmentMedia | None:
    """Load and verify `root / "media"` against the job's `segmentMedia`; `None` when the job carries none.

    Raises `InputError` (`segment-media-missing`, `segment-media-unexpected`, `segment-manifest-mismatch`,
    `segment-media-digest-mismatch`, `segment-manifest-invalid`, `unsupported-version`). Files are only read.
    """
    folder = root / MEDIA_DIRECTORY
    declared = job.segment_media
    if declared is None:
        if folder.exists() or folder.is_symlink():
            raise errors.InputError(errors.SEGMENT_MEDIA_UNEXPECTED, "the media mount exists but the job has no segmentMedia.")
        return None

    manifest_path, media_path = folder / MANIFEST_FILE, folder / MEDIA_FILE
    if not folder.is_dir() or not _regular_file(manifest_path) or not _regular_file(media_path):
        raise errors.InputError(errors.SEGMENT_MEDIA_MISSING, "the job carries segmentMedia but the media mount is incomplete.")

    try:
        raw = manifest_path.read_bytes()
    except OSError:
        raise errors.InputError(errors.SEGMENT_MEDIA_MISSING, f"{MANIFEST_FILE} is unreadable.") from None
    if f"sha-256:{hashlib.sha256(raw).hexdigest()}" != declared.manifest_checksum:
        raise errors.InputError(errors.SEGMENT_MANIFEST_MISMATCH, f"{MANIFEST_FILE} differs from the job's manifest checksum.")
    manifest = _parse_manifest(raw)

    try:
        digest, size = _digest_and_size(media_path)
    except OSError:
        raise errors.InputError(errors.SEGMENT_MEDIA_MISSING, f"{MEDIA_FILE} is unreadable.") from None
    if digest != manifest.media.digest or size != manifest.media.size_bytes:
        raise errors.InputError(errors.SEGMENT_MEDIA_DIGEST_MISMATCH, f"{MEDIA_FILE} differs in size or digest from its manifest.")
    return SegmentMedia(manifest, media_path)

