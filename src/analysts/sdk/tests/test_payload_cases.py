"""The SDK gives the platform's verdict on every case of the shared `fact-payloads.json` examples."""

from __future__ import annotations

import json
from collections.abc import Callable
from pathlib import Path
from typing import Any

import pytest
from fixtures.mounts import MATCH_JOB_EXAMPLE, SEGMENT_JOB_EXAMPLE, Mount

from socalytics_analyst import errors
from socalytics_analyst.context import load_documents
from socalytics_analyst.models.facts import Fact
from socalytics_analyst.models.manifest import AnalystManifest
from socalytics_analyst.result import ResultWriter

EXAMPLES = Path(__file__).resolve().parents[4] / "contracts" / "analysis" / "analyst-manifest" / "v1" / "examples"
CASES = json.loads((EXAMPLES / "fact-payloads.json").read_text(encoding="utf-8"))


def _case_id(index: int) -> str:
    case = CASES[index]
    verdict = "valid" if case["valid"] else f"invalid{case['at'] or '-root'}"
    return f"{index:02d}-{Path(case['manifest']).stem}-{case['kind']}-{verdict}"


def _manifest(case: dict[str, Any]) -> AnalystManifest:
    return AnalystManifest.model_validate(json.loads((EXAMPLES / case["manifest"]).read_text(encoding="utf-8")))


def test_every_case_names_a_manifest_that_declares_its_kind() -> None:
    assert CASES
    for case in CASES:
        declared = _manifest(case).output.fact_payload_schemas
        assert declared is not None
        assert case["kind"] in declared


@pytest.mark.parametrize("index", range(len(CASES)), ids=[_case_id(i) for i in range(len(CASES))])
def test_sdk_gives_the_shared_verdict(index: int, mount_factory: Callable[..., Mount]) -> None:
    case = CASES[index]
    manifest = _manifest(case)
    example = SEGMENT_JOB_EXAMPLE if manifest.capability.execution_scope == "segment" else MATCH_JOB_EXAMPLE
    mount = mount_factory(job_example_name=example)
    job, attempt = load_documents(mount.root)
    writer = ResultWriter(job, attempt, "foundational-facts", root=mount.root, manifest=manifest)
    fact = Fact(kind=case["kind"], start_ms=1000, end_ms=2000, confidence=0.9, payload=case["payload"])

    if case["valid"]:
        writer.add_fact(fact)
        assert writer.fact_count == 1
        return

    with pytest.raises(errors.ResultError) as raised:
        writer.add_fact(fact)
    assert raised.value.category == errors.FACT_PAYLOAD_INVALID
    assert writer.fact_count == 0
    assert f" at {case['at'].lstrip('/') or '(root)'} (" in str(raised.value)
