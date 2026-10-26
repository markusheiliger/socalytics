"""Corpus manifest model and validation (FR-001 to FR-003).

Validation runs before any execution and reports every problem at once.
"""

from __future__ import annotations

import hashlib
import json
import re
from dataclasses import dataclass, field
from pathlib import Path, PurePosixPath
from typing import Any

from jsonschema import Draft202012Validator

SCHEMA_PATH = Path(__file__).resolve().parents[2] / "schemas" / "corpus-manifest.schema.json"

_DIGEST = re.compile(r"^sha-256:[0-9a-f]{64}$")
_RIGHTS_FIELDS = ("origin", "license", "production")
_CHUNK = 1024 * 1024


@dataclass(frozen=True)
class CorpusProblem:
    code: str
    path: str
    message: str

    def __str__(self) -> str:
        return f"{self.code} at {self.path}: {self.message}"


class CorpusValidationError(Exception):
    """A corpus manifest was refused; ``problems`` lists every problem found."""

    def __init__(self, problems: list[CorpusProblem]) -> None:
        self.problems = problems
        super().__init__("corpus manifest refused:\n" + "\n".join(f"- {p}" for p in problems))


@dataclass(frozen=True)
class Rights:
    origin: str
    license: str
    production: str


@dataclass(frozen=True)
class Label:
    label_kind: str
    position_ms: int | None = None
    range_start_ms: int | None = None
    range_end_ms: int | None = None
    payload: dict[str, Any] | None = None


@dataclass(frozen=True)
class Clip:
    clip_id: str
    file: str
    digest: str
    profile: str
    frame_rate: float
    duration_ms: int
    match_start_ms: int
    rights: Rights
    labels: tuple[Label, ...]
    path: Path


@dataclass(frozen=True)
class CorpusManifest:
    manifest_version: str
    corpus_id: str
    corpus_version: str
    identity: str
    rights: Rights
    clips: tuple[Clip, ...]
    directory: Path
    raw: dict[str, Any] = field(repr=False, compare=False)


def manifest_identity(manifest: dict[str, Any]) -> str:
    """Canonical ``sha-256`` of the manifest without ``identity``."""
    body = {key: value for key, value in manifest.items() if key != "identity"}
    canonical = json.dumps(
        body, sort_keys=True, separators=(",", ":"), ensure_ascii=False, allow_nan=False
    ).encode("utf-8")
    return "sha-256:" + hashlib.sha256(canonical).hexdigest()


def _pointer(parts: Any) -> str:
    text = "/".join(str(part) for part in parts)
    return "/" + text if text else "/"


def _in_rights(parts: list[Any]) -> bool:
    return "rights" in parts and (parts[-1] == "rights" or parts[-2:-1] == ["rights"])


def _classify(error: Any) -> CorpusProblem | None:
    parts = list(error.absolute_path)
    path = _pointer(parts)
    last = parts[-1] if parts else None
    if _in_rights(parts) and error.validator in ("required", "minLength", "type"):
        return None  # reported by _rights_problems
    if last == "profile" and error.validator == "enum":
        return CorpusProblem("unknown-profile", path, "profile must be one of fixed, broadcast")
    if last in ("digest", "identity") and error.validator == "pattern":
        return CorpusProblem("invalid-digest", path, "must be sha-256:<64 lowercase hex>")
    if last == "file" and error.validator in ("pattern", "not"):
        return CorpusProblem("invalid-file-path", path, "must be a relative path without '..'")
    return CorpusProblem("schema-violation", path, error.message)


def _rights_problems(rights: Any, path: str) -> list[CorpusProblem]:
    if not isinstance(rights, dict):
        return []
    problems = []
    for name in _RIGHTS_FIELDS:
        if name not in rights:
            problems.append(CorpusProblem("missing-rights-field", f"{path}/{name}", "required"))
        elif not isinstance(rights[name], str) or not rights[name]:
            problems.append(
                CorpusProblem("missing-rights-field", f"{path}/{name}", "must be a non-empty string")
            )
    return problems


def _schema_problems(manifest: Any) -> list[CorpusProblem]:
    schema = json.loads(SCHEMA_PATH.read_text(encoding="utf-8"))
    problems: list[CorpusProblem] = []

    if isinstance(manifest, dict):
        problems += _rights_problems(manifest.get("rights"), "/rights")
        clips = manifest.get("clips")
        if isinstance(clips, list):
            for index, clip in enumerate(clips):
                if isinstance(clip, dict):
                    problems += _rights_problems(clip.get("rights"), f"/clips/{index}/rights")

    validator = Draft202012Validator(schema)
    for error in sorted(validator.iter_errors(manifest), key=lambda e: list(map(str, e.absolute_path))):
        problem = _classify(error)
        if problem is not None:
            problems.append(problem)
    return _dedupe(problems)


def _dedupe(problems: list[CorpusProblem]) -> list[CorpusProblem]:
    seen: set[CorpusProblem] = set()
    unique = []
    for problem in problems:
        if problem not in seen:
            seen.add(problem)
            unique.append(problem)
    return unique


def _safe_relative(file: Any) -> bool:
    if not isinstance(file, str) or not file or "\\" in file:
        return False
    pure = PurePosixPath(file)
    return not pure.is_absolute() and ".." not in pure.parts


def _file_digest(path: Path) -> str:
    sha = hashlib.sha256()
    with path.open("rb") as handle:
        while chunk := handle.read(_CHUNK):
            sha.update(chunk)
    return "sha-256:" + sha.hexdigest()


def _content_problems(manifest: dict[str, Any], directory: Path) -> list[CorpusProblem]:
    problems: list[CorpusProblem] = []
    clips = manifest.get("clips")
    if not isinstance(clips, list):
        return problems

    root = directory.resolve()
    first_seen: dict[str, int] = {}
    for index, clip in enumerate(clips):
        if not isinstance(clip, dict):
            continue
        base = f"/clips/{index}"
        clip_id = clip.get("clipId")
        if isinstance(clip_id, str):
            if clip_id in first_seen:
                problems.append(
                    CorpusProblem(
                        "duplicate-clip-id",
                        f"{base}/clipId",
                        f"'{clip_id}' already used by /clips/{first_seen[clip_id]}",
                    )
                )
            else:
                first_seen[clip_id] = index

        file = clip.get("file")
        if not _safe_relative(file):
            continue
        target = (root / file).resolve()
        if root != target and root not in target.parents:
            problems.append(
                CorpusProblem("invalid-file-path", f"{base}/file", "resolves outside the corpus directory")
            )
            continue
        if not target.is_file():
            problems.append(CorpusProblem("file-missing", f"{base}/file", f"'{file}' does not exist"))
            continue
        try:
            actual = _file_digest(target)
        except OSError as exc:
            problems.append(
                CorpusProblem("file-unreadable", f"{base}/file", f"'{file}' cannot be read: {exc.strerror or exc}")
            )
            continue
        declared = clip.get("digest")
        if isinstance(declared, str) and _DIGEST.match(declared) and declared != actual:
            problems.append(
                CorpusProblem(
                    "digest-mismatch", f"{base}/digest", f"declared {declared} but the file is {actual}"
                )
            )

    identity = manifest.get("identity")
    if isinstance(identity, str) and _DIGEST.match(identity):
        try:
            expected = manifest_identity(manifest)
        except (TypeError, ValueError):
            expected = None
        if expected is not None and expected != identity:
            problems.append(
                CorpusProblem("identity-mismatch", "/identity", f"declared {identity} but the manifest is {expected}")
            )
    return problems


def _rights(raw: dict[str, Any]) -> Rights:
    return Rights(raw["origin"], raw["license"], raw["production"])


def _label(raw: dict[str, Any]) -> Label:
    return Label(
        label_kind=raw["labelKind"],
        position_ms=raw.get("positionMs"),
        range_start_ms=raw.get("rangeStartMs"),
        range_end_ms=raw.get("rangeEndMs"),
        payload=raw.get("payload"),
    )


def validate_manifest(manifest: Any, directory: Path) -> list[CorpusProblem]:
    """Return every problem of ``manifest`` against ``directory``; empty when valid."""
    problems = _schema_problems(manifest)
    if isinstance(manifest, dict):
        problems += _content_problems(manifest, Path(directory))
    return _dedupe(problems)


def parse_manifest(manifest: dict[str, Any], directory: Path) -> CorpusManifest:
    """Validate ``manifest`` and build the model; raises :class:`CorpusValidationError`."""
    directory = Path(directory)
    problems = validate_manifest(manifest, directory)
    if problems:
        raise CorpusValidationError(problems)
    clips = tuple(
        Clip(
            clip_id=clip["clipId"],
            file=clip["file"],
            digest=clip["digest"],
            profile=clip["profile"],
            frame_rate=clip["frameRate"],
            duration_ms=clip["durationMs"],
            match_start_ms=clip["matchStartMs"],
            rights=_rights(clip["rights"]),
            labels=tuple(_label(label) for label in clip["labels"]),
            path=(directory.resolve() / clip["file"]).resolve(),
        )
        for clip in manifest["clips"]
    )
    return CorpusManifest(
        manifest_version=manifest["manifestVersion"],
        corpus_id=manifest["corpusId"],
        corpus_version=manifest["corpusVersion"],
        identity=manifest["identity"],
        rights=_rights(manifest["rights"]),
        clips=clips,
        directory=directory.resolve(),
        raw=manifest,
    )


def load_corpus(manifest_path: Path | str) -> CorpusManifest:
    """Load and validate the manifest file; clip files resolve beside it."""
    path = Path(manifest_path)
    try:
        text = path.read_text(encoding="utf-8")
    except OSError as exc:
        raise CorpusValidationError(
            [CorpusProblem("manifest-unreadable", "/", f"{path} cannot be read: {exc.strerror or exc}")]
        ) from exc
    try:
        manifest = json.loads(text)
    except ValueError as exc:
        raise CorpusValidationError(
            [CorpusProblem("schema-violation", "/", f"manifest is not valid JSON: {exc}")]
        ) from exc
    return parse_manifest(manifest, path.parent)
