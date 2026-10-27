"""Builders for test corpora, configurations, and a scripted test-only capability."""

from __future__ import annotations

import copy
import hashlib
import json
import shlex
import sys
from collections.abc import Mapping, Sequence
from dataclasses import dataclass
from pathlib import Path
from typing import Any

from analyst_evaluation_harness.canonical import identity_of

RIGHTS: dict[str, str] = {
    "origin": "generated for tests",
    "license": "CC0-1.0",
    "production": "arbitrary bytes with known labels",
}

# Clips: (clipId, profile, frameRate, durationMs, matchStartMs)
DEFAULT_CLIPS: tuple[tuple[str, str, float, int, int], ...] = (
    ("fixed-a", "fixed", 25.0, 5000, 0),
    ("fixed-b", "fixed", 25.0, 6000, 0),
    ("broadcast-a", "broadcast", 30.0, 5000, 10000),
    ("broadcast-b", "broadcast", 30.0, 6000, 20000),
)

KIND = "test-event"


def digest_of(data: bytes) -> str:
    return "sha-256:" + hashlib.sha256(data).hexdigest()


def clip_bytes(clip_id: str) -> bytes:
    """Arbitrary but distinct and deterministic content per clip."""
    return f"not-a-video:{clip_id}".encode() * 8


def label(kind: str = KIND, position_ms: int | None = 1000, **extra: Any) -> dict[str, Any]:
    document: dict[str, Any] = {"labelKind": kind}
    if position_ms is not None:
        document["positionMs"] = position_ms
    document.update(extra)
    return document


def seal(manifest: dict[str, Any]) -> dict[str, Any]:
    """Set the manifest identity to the canonical identity of the manifest without it."""
    manifest["identity"] = identity_of(manifest, exclude=("identity",))
    return manifest


def build_manifest(
    clips: Sequence[tuple[str, str, float, int, int]] = DEFAULT_CLIPS,
    labels: Mapping[str, Sequence[dict[str, Any]]] | None = None,
    *,
    corpus_id: str = "test-corpus",
    corpus_version: str = "1.0.0",
) -> dict[str, Any]:
    """A valid manifest; clips without an entry in `labels` get one label at `match start + 1000`."""
    entries = []
    for clip_id, profile, frame_rate, duration_ms, match_start_ms in clips:
        clip_labels = (
            [copy.deepcopy(item) for item in labels[clip_id]]
            if labels is not None and clip_id in labels
            else [label(position_ms=match_start_ms + 1000)]
        )
        entries.append(
            {
                "clipId": clip_id,
                "file": f"clips/{clip_id}.bin",
                "digest": digest_of(clip_bytes(clip_id)),
                "profile": profile,
                "frameRate": frame_rate,
                "durationMs": duration_ms,
                "matchStartMs": match_start_ms,
                "rights": dict(RIGHTS),
                "labels": clip_labels,
            }
        )
    manifest = {
        "manifestVersion": "1.0.0",
        "corpusId": corpus_id,
        "corpusVersion": corpus_version,
        "identity": "sha-256:" + "0" * 64,
        "rights": dict(RIGHTS),
        "clips": entries,
    }
    return seal(manifest)


@dataclass(frozen=True)
class Corpus:
    root: Path
    manifest_path: Path
    manifest: dict[str, Any]

    def clip_path(self, clip_id: str) -> Path:
        return self.root / "clips" / f"{clip_id}.bin"


def write_corpus(root: Path, manifest: dict[str, Any] | None = None) -> Corpus:
    """Write the manifest and the clip files of every clip it names into `root`."""
    manifest = manifest if manifest is not None else build_manifest()
    (root / "clips").mkdir(parents=True, exist_ok=True)
    for clip in manifest["clips"]:
        (root / "clips" / f"{clip['clipId']}.bin").write_bytes(clip_bytes(clip["clipId"]))
    manifest_path = root / "manifest.json"
    manifest_path.write_text(json.dumps(manifest, indent=2), encoding="utf-8")
    return Corpus(root, manifest_path, manifest)


def build_config(label_kinds: Mapping[str, Mapping[str, Any]] | None = None, **options: Any) -> dict[str, Any]:
    """A valid evaluation configuration; `options` add or replace top-level fields."""
    kinds = label_kinds if label_kinds is not None else {KIND: {"primaryMetric": "f1"}}
    config: dict[str, Any] = {
        "configVersion": "1.0.0",
        "repetitions": 2,
        "timeLimitSeconds": 30,
        "labelKinds": {kind: dict(settings) for kind, settings in kinds.items()},
    }
    config.update(options)
    return config


def fact(
    start_ms: int,
    end_ms: int | None = None,
    *,
    kind: str = KIND,
    confidence: float = 1.0,
    payload: Mapping[str, Any] | None = None,
) -> dict[str, Any]:
    return {
        "kind": kind,
        "startMs": start_ms,
        "endMs": end_ms if end_ms is not None else start_ms + 1000,
        "confidence": confidence,
        "payload": dict(payload or {}),
    }


# The scripted capability: a standalone launcher built on the SDK `run`; it is started as
# `python <script> <mount> <scenario.json>` and never imports the harness.
CAPABILITY_SCRIPT = '''\
import json
import os
import sys
import time
from pathlib import Path

from socalytics_analyst import Fact, InputError, run

mount = Path(sys.argv[1])
scenario_path = Path(sys.argv[2])
scenario = json.loads(scenario_path.read_text(encoding="utf-8"))

counter_path = scenario_path.with_suffix(".count")
invocation = int(counter_path.read_text()) if counter_path.exists() else 0
counter_path.write_text(str(invocation + 1))

if scenario.get("hang"):
    time.sleep(3600)
if scenario.get("crash"):
    os._exit(1)
if scenario.get("malformed"):
    (mount / "result").mkdir(exist_ok=True)
    (mount / "result" / "facts.json").write_text("{not json", encoding="utf-8")
    sys.exit(0)

facts = scenario.get("facts", [])
variants = scenario.get("variants")
if variants:
    facts = variants[min(invocation, len(variants) - 1)]
clip_facts = scenario.get("factsByClip")
if clip_facts is not None:
    manifest = json.loads((mount / "media" / "segment-manifest.json").read_text(encoding="utf-8"))
    facts = clip_facts.get(manifest["identity"]["recordingVersionId"], [])


def analyse(context):
    category = scenario.get("fail")
    if category:
        raise InputError(category, "scripted failure.")
    for item in facts:
        context.result.add_fact(
            Fact(
                kind=item["kind"],
                start_ms=item["startMs"],
                end_ms=item["endMs"],
                confidence=item["confidence"],
                payload=item["payload"],
            )
        )
    if facts:
        context.result.set_confidence(1.0)


run(analyse, output_type=scenario.get("outputType", "foundational-facts"), root=mount)
'''


@dataclass(frozen=True)
class ScriptedCapability:
    """A scripted subprocess capability: its command line and the scenario it plays."""

    script: Path
    scenario_path: Path

    @property
    def command(self) -> str:
        return " ".join(
            (shlex.quote(sys.executable), shlex.quote(str(self.script)), "{mount}", shlex.quote(str(self.scenario_path)))
        )

    @property
    def invocations(self) -> int:
        counter = self.scenario_path.with_suffix(".count")
        return int(counter.read_text()) if counter.exists() else 0


def write_capability(folder: Path, name: str = "capability", **scenario: Any) -> ScriptedCapability:
    """Write the scripted capability and its scenario into `folder`.

    Scenario keys: `facts` (a list of `fact(...)`), `variants` (facts per invocation, the last one repeating),
    `factsByClip` (facts keyed by the manifest's recording version id), `fail` (an SDK failure category), `hang`,
    `crash` (exit without a category line), `malformed` (a `facts.json` that is not valid), and `outputType`.
    """
    folder.mkdir(parents=True, exist_ok=True)
    script = folder / f"{name}.py"
    script.write_text(CAPABILITY_SCRIPT, encoding="utf-8")
    scenario_path = folder / f"{name}.scenario.json"
    scenario_path.write_text(json.dumps(scenario), encoding="utf-8")
    return ScriptedCapability(script, scenario_path)
