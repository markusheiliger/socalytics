"""The result writer: collects facts and artifacts and finalizes an atomic, schema-valid result."""

from __future__ import annotations

import hashlib
import json
import math
import os
import re
import shutil
from pathlib import Path
from typing import Any

from jsonschema import Draft202012Validator
from pydantic import ValidationError

from . import errors, schemas
from .context import DEFAULT_ROOT
from .models.attempt import AttemptContext
from .models.facts import FACTS_MEDIA_TYPE, Fact
from .models.job import LogicalJob

RESULT_DIRECTORY = "result"
STAGING_DIRECTORY = ".socalytics-staging"
MANIFEST_NAME = "manifest.json"
FACTS_NAME = "facts.json"
MANIFEST_CONTRACT_VERSION = "1.1.0"
FACTS_CONTRACT_VERSION = "1.0.0"
MAX_ARTIFACTS = 256

_OUTPUT_TYPES = ("foundational-facts", "soccer-concepts")
_ARTIFACT_NAME = re.compile(r"^[a-z0-9][a-z0-9._\-]{0,127}$")
_MEDIA_TYPE = re.compile(r"^[a-z0-9][a-z0-9.+\-]*/[a-z0-9][a-z0-9.+\-]*$")
_IDENTIFIER = re.compile(r"^[A-Za-z0-9][A-Za-z0-9._\-]{0,127}$")
_CHUNK = 1024 * 1024


def _is_int(value: object) -> bool:
    return isinstance(value, int) and not isinstance(value, bool)


def _digest(hasher: Any) -> str:
    return f"sha-256:{hasher.hexdigest()}"


def _fsync_directory(path: Path) -> None:
    try:
        descriptor = os.open(path, os.O_RDONLY)
    except OSError:
        return
    try:
        os.fsync(descriptor)
    except OSError:
        pass
    finally:
        os.close(descriptor)


def _declared_payload_schemas(manifest: Any) -> dict[str, Any]:
    """The fact payload schemas of an Analyst manifest model or of its JSON object; empty when none are declared."""
    if manifest is None:
        return {}
    if isinstance(manifest, dict):
        output = manifest.get("output")
        declared = output.get("factPayloadSchemas") if isinstance(output, dict) else None
    else:
        output = getattr(manifest, "output", None)
        declared = getattr(output, "fact_payload_schemas", None)
    if not declared:
        return {}
    return {
        kind: schema.model_dump(by_alias=True, exclude_unset=True) if hasattr(schema, "model_dump") else schema
        for kind, schema in declared.items()
    }


class _Staged:
    """A document written into the staging directory, with the digest and size of the written bytes."""

    def __init__(self, name: str, media_type: str, digest: str, size: int) -> None:
        self.name = name
        self.media_type = media_type
        self.digest = digest
        self.size = size


class ResultWriter:
    """Collects the result of one attempt.

    States: open (additions allowed) -> finalized (manifest exists) or failed (no manifest, staging removed).
    Scope, attempt, provenance, and lineage come from the job and attempt and cannot be set by the author.
    """

    def __init__(
        self,
        job: LogicalJob,
        attempt: AttemptContext,
        output_type: str,
        root: Path = DEFAULT_ROOT,
        inputs: Any = (),
        manifest: Any = None,
    ) -> None:
        if output_type not in _OUTPUT_TYPES:
            raise errors.ResultError(errors.RESULT_VALUE_INVALID, "the output type is not a supported output type.")
        self._inputs = tuple(inputs or ())
        self._payload_schemas = _declared_payload_schemas(manifest)
        self._payload_validators: dict[str, Draft202012Validator] = {}
        self._job = job
        self._attempt = attempt
        self._output_type = output_type
        self._result = Path(root) / RESULT_DIRECTORY
        self._staging = self._result / STAGING_DIRECTORY
        self._facts: list[tuple[int, int, bytes]] = []
        self._artifacts: list[_Staged] = []
        self._confidence: float | None = None
        self._evidence: list[str] = []
        self._window: tuple[int, int] | None = None
        if job.execution_scope == "segment" and job.segment_window is not None:
            self._window = (job.segment_window.start_ms, job.segment_window.end_ms)
        self._state = "open"
        self._placed: list[Path] = []

    @property
    def fact_count(self) -> int:
        return len(self._facts)

    @property
    def artifact_count(self) -> int:
        return len(self._artifacts)

    def _require_open(self) -> None:
        if self._state != "open":
            raise errors.ResultError(errors.RESULT_ALREADY_FINALIZED, "the result is already closed.")

    def add_fact(self, fact: Fact) -> None:
        self._require_open()
        if not isinstance(fact, Fact):
            raise errors.ResultError(errors.FACT_INVALID, "a fact must be a Fact.")
        try:
            checked = Fact.model_validate(fact.model_dump(by_alias=True))
        except ValidationError:
            raise errors.ResultError(errors.FACT_INVALID, "a fact violates the fact-artifact contract.") from None
        if checked.start_ms >= checked.end_ms:
            raise errors.ResultError(errors.FACT_OUT_OF_WINDOW, "a fact range must satisfy startMs < endMs.")
        if self._window is not None and not (self._window[0] <= checked.start_ms and checked.end_ms <= self._window[1]):
            raise errors.ResultError(errors.FACT_OUT_OF_WINDOW, "a fact range is outside the validity window.")
        try:
            encoded = json.dumps(
                checked.model_dump(by_alias=True), ensure_ascii=False, allow_nan=False, separators=(",", ":")
            ).encode("utf-8")
        except (TypeError, ValueError):
            raise errors.ResultError(errors.FACT_INVALID, "a fact payload is not valid JSON.") from None
        self._validate_payload(checked.kind, json.loads(encoded)["payload"])
        self._facts.append((checked.start_ms, checked.end_ms, encoded))

    def _validate_payload(self, kind: str, payload: Any) -> None:
        if not self._payload_schemas:
            return
        validator = self._payload_validators.get(kind)
        if validator is None:
            schema = self._payload_schemas.get(kind)
            if schema is None:
                raise errors.ResultError(
                    errors.FACT_KIND_UNDECLARED, f"the fact kind {kind} has no declared payload schema."
                )
            validator = self._payload_validators[kind] = Draft202012Validator(schema)
        failure = next(iter(validator.iter_errors(payload)), None)
        if failure is not None:
            location = "/".join(str(part) for part in failure.absolute_path) or "(root)"
            raise errors.ResultError(
                errors.FACT_PAYLOAD_INVALID,
                f"the payload of a {kind} fact violates its declared schema at {location} ({failure.validator}).",
            )

    def add_artifact(self, name: str, media_type: str, data: bytes) -> None:
        self._require_open()
        if not isinstance(name, str) or not _ARTIFACT_NAME.match(name):
            raise errors.ResultError(errors.ARTIFACT_INVALID, "an artifact name does not match the name pattern.")
        if name in (MANIFEST_NAME, FACTS_NAME):
            raise errors.ResultError(errors.ARTIFACT_INVALID, f"the artifact name {name} is reserved.")
        if any(staged.name == name for staged in self._artifacts):
            raise errors.ResultError(errors.ARTIFACT_INVALID, f"the artifact {name} was already added.")
        if not isinstance(media_type, str) or len(media_type) > 100 or not _MEDIA_TYPE.match(media_type):
            raise errors.ResultError(errors.ARTIFACT_INVALID, f"the media type of artifact {name} is invalid.")
        if not isinstance(data, (bytes, bytearray, memoryview)) or len(data) == 0:
            raise errors.ResultError(errors.ARTIFACT_INVALID, f"the content of artifact {name} must be non-empty bytes.")
        if len(self._artifacts) + 1 >= MAX_ARTIFACTS:
            raise errors.ResultError(errors.ARTIFACT_INVALID, "too many artifacts.")
        view = memoryview(data).cast("B")
        self._artifacts.append(
            self._write_staged(name, media_type, (view[i : i + _CHUNK] for i in range(0, len(view), _CHUNK)))
        )

    def set_validity_window(self, start_ms: int, end_ms: int) -> None:
        self._require_open()
        if not _is_int(start_ms) or not _is_int(end_ms) or start_ms < 0 or end_ms <= start_ms:
            raise errors.ResultError(errors.VALIDITY_WINDOW_INVALID, "the validity window must satisfy 0 <= start < end.")
        bounds = self._job.segment_window
        if self._job.execution_scope == "segment" and bounds is not None:
            if start_ms < bounds.start_ms or end_ms > bounds.end_ms:
                raise errors.ResultError(
                    errors.VALIDITY_WINDOW_INVALID, "the validity window is outside the segment window."
                )
        self._window = (start_ms, end_ms)

    def set_confidence(self, value: float) -> None:
        self._require_open()
        if (
            isinstance(value, bool)
            or not isinstance(value, (int, float))
            or not math.isfinite(value)
            or not 0 <= value <= 1
        ):
            raise errors.ResultError(errors.RESULT_VALUE_INVALID, "the confidence must be a number from 0 to 1.")
        self._confidence = float(value)

    def add_evidence_reference(self, identifier: str) -> None:
        self._require_open()
        if not isinstance(identifier, str) or not _IDENTIFIER.match(identifier):
            raise errors.ResultError(errors.RESULT_VALUE_INVALID, "an evidence reference must be an identifier.")
        self._evidence.append(identifier)

    def finalize(self) -> None:
        """Validate, then move the artifacts and, last, the manifest into the result mount."""
        self._require_open()
        try:
            self._finalize()
        except BaseException:
            self._state = "failed"
            self.discard()
            raise
        self._state = "finalized"

    def discard(self) -> None:
        """Remove the staging directory and every file this writer placed in the result mount."""
        for path in reversed(self._placed):
            try:
                path.unlink()
            except OSError:
                pass
        self._placed.clear()
        shutil.rmtree(self._staging, ignore_errors=True)

    def _derived_window(self) -> tuple[int, int] | None:
        """The smallest window containing the validity window of every staged upstream result."""
        windows = [
            (result.manifest.validity_window.start_ms, result.manifest.validity_window.end_ms)
            for dependency in self._inputs
            for result in dependency.results
        ]
        if not windows:
            return None
        return min(start for start, _ in windows), max(end for _, end in windows)

    def _input_coverage(self) -> list[dict[str, Any]]:
        """The staged coverage, unchanged, of each dependency whose staged coverage states mode partial."""
        report: list[dict[str, Any]] = []
        for dependency in self._inputs:
            staged = dependency.coverage
            if staged is None or staged.upstream.coverage is None or staged.upstream.coverage.mode != "partial":
                continue
            report.append(
                {
                    "capability": staged.upstream.capability.model_dump(by_alias=True),
                    "schemaVersion": staged.upstream.schema_version,
                    "units": [unit.model_dump(by_alias=True, exclude_none=True) for unit in staged.coverage],
                }
            )
        return report

    def _finalize(self) -> None:
        if self._window is None and self._job.execution_scope != "segment":
            self._window = self._derived_window()
        if self._window is None:
            raise errors.ResultError(errors.VALIDITY_WINDOW_MISSING, "the match job has no validity window.")
        start, end = self._window
        for fact_start, fact_end, _ in self._facts:
            if fact_start < start or fact_end > end:
                raise errors.ResultError(errors.FACT_OUT_OF_WINDOW, "a fact range is outside the validity window.")
        self._refuse_unexpected_files()

        confidence = self._confidence
        if confidence is None:
            if self._facts:
                raise errors.ResultError(errors.CONFIDENCE_MISSING, "facts were added but no confidence was set.")
            confidence = 0.0

        facts = self._write_facts()
        artifacts = [facts, *self._artifacts]
        manifest = self._manifest(artifacts, confidence)
        schemas.validate("result-manifest", manifest, output=True)
        manifest_bytes = json.dumps(manifest, ensure_ascii=False, allow_nan=False, separators=(",", ":")).encode("utf-8")
        staged_manifest = self._write_staged(MANIFEST_NAME, "application/json", [manifest_bytes])

        for staged in [*artifacts, staged_manifest]:
            target = self._result / staged.name
            os.replace(self._staging / staged.name, target)
            self._placed.append(target)
        _fsync_directory(self._result)
        self._placed.clear()
        shutil.rmtree(self._staging, ignore_errors=True)

    def _refuse_unexpected_files(self) -> None:
        try:
            entries = list(self._result.iterdir())
        except FileNotFoundError:
            return
        for entry in entries:
            if entry.is_symlink() or not entry.is_dir():
                raise errors.ResultError(
                    errors.RESULT_UNEXPECTED_FILE, "the result mount holds a top-level file the writer did not write."
                )

    def _write_staged(self, name: str, media_type: str, chunks: Any) -> _Staged:
        self._staging.mkdir(parents=True, exist_ok=True)
        hasher = hashlib.sha256()
        size = 0
        with open(self._staging / name, "wb") as handle:
            for chunk in chunks:
                handle.write(chunk)
                hasher.update(chunk)
                size += len(chunk)
            handle.flush()
            os.fsync(handle.fileno())
        return _Staged(name, media_type, _digest(hasher), size)

    def _write_facts(self) -> _Staged:
        job = self._job

        def chunks() -> Any:
            yield (
                json.dumps(
                    {
                        "contractVersion": FACTS_CONTRACT_VERSION,
                        "capability": job.capability.model_dump(by_alias=True),
                        "schemaVersion": job.schema_version,
                        "outputType": self._output_type,
                    },
                    separators=(",", ":"),
                )[:-1]
                + ',"facts":['
            ).encode("utf-8")
            for index, (_, _, encoded) in enumerate(self._facts):
                yield encoded if index == 0 else b"," + encoded
            yield b"]}"

        staged = self._write_staged(FACTS_NAME, FACTS_MEDIA_TYPE, chunks())
        try:
            document = json.loads((self._staging / FACTS_NAME).read_bytes())
        except ValueError:
            raise errors.ResultError(errors.OUTPUT_INVALID, "the facts document is not valid JSON.") from None
        schemas.validate("fact-artifact", document, output=True)
        return staged

    def _manifest(self, artifacts: list[_Staged], confidence: float) -> dict[str, Any]:
        job = self._job
        assert self._window is not None
        scope: dict[str, Any] = {
            "teamId": job.team_id,
            "matchId": job.match_id,
            "recordingSetVersionId": job.recording_set_version_id,
            "runId": job.run_id,
            "nodeId": job.node_id,
            "logicalJobId": job.logical_job_id,
        }
        if job.execution_scope == "segment":
            assert job.segment_window is not None
            scope["segment"] = {
                "segmentId": job.segment_window.segment_id,
                "windowStartMs": job.segment_window.start_ms,
                "windowEndMs": job.segment_window.end_ms,
            }
        else:
            scope["match"] = True

        pinned = job.pinned
        provenance: dict[str, Any] = {
            "capability": job.capability.model_dump(by_alias=True),
            "schemaVersion": job.schema_version,
            "analystProfile": {"id": pinned.analyst_profile_id, "digest": pinned.analyst_profile_digest},
            "imageDigest": pinned.image_digest,
        }
        if pinned.model_digest is not None:
            provenance["modelDigest"] = pinned.model_digest
        provenance["policyVersions"] = [{"id": p.policy_id, "version": p.version} for p in pinned.policies]
        assert job.workflow is not None
        provenance["workflow"] = job.workflow.model_dump(by_alias=True)

        lineage = [
            {
                "acceptedResultId": result.accepted_result_id,
                "capability": result.capability.model_dump(by_alias=True),
                "schemaVersion": result.schema_version,
                "manifestChecksum": result.manifest_checksum,
            }
            for entry in job.inputs
            for result in entry.accepted_results
        ]

        manifest: dict[str, Any] = {
            "contractVersion": MANIFEST_CONTRACT_VERSION,
            "scope": scope,
            "provenance": provenance,
            "attempt": {"attemptId": self._attempt.attempt_id, "attemptNumber": self._attempt.attempt_number},
            "artifacts": [
                {"name": a.name, "digest": a.digest, "sizeBytes": a.size, "mediaType": a.media_type} for a in artifacts
            ],
            "validityWindow": {"startMs": self._window[0], "endMs": self._window[1]},
            "factCount": len(self._facts),
            "upstreamLineage": lineage,
            "outputType": self._output_type,
            "confidence": confidence,
            "evidenceReferences": list(self._evidence),
        }
        coverage = self._input_coverage()
        if coverage:
            manifest["inputCoverage"] = coverage
        return manifest
