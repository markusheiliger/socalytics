from __future__ import annotations

import json
from collections.abc import Callable
from pathlib import Path

import pytest
from jsonschema import Draft202012Validator
from socalytics_analyst import run

from reference_media_probe.__main__ import analyse
from support.mounts import Mount

PAYLOAD_SCHEMA = json.loads(
    (Path(__file__).parent.parent / "schemas" / "frame-second-probe.payload.schema.json").read_text(encoding="utf-8")
)
WINDOW = {"startMs": 0, "endMs": 5000}
SPANS = [{"mediaStartMs": 0, "mediaEndMs": 5000, "matchStartMs": 0}]


def _run(mount: Mount) -> int:
    with pytest.raises(SystemExit) as exit_info:
        run(analyse, output_type="foundational-facts", root=mount.root)
    return exit_info.value.code or 0


def _mount(
    media_factory: Callable[..., Path], mount_factory: Callable[..., Mount], spans: list[dict] = SPANS, **options: object
) -> Mount:
    return mount_factory(
        media_factory(),
        nominal_window=WINDOW,
        requested_source_interval=WINDOW,
        encoded_interval=WINDOW,
        timeline_spans=spans,
        **options,
    )


def _facts(mount: Mount) -> list[dict]:
    return json.loads((mount.result / "facts.json").read_text(encoding="utf-8"))["facts"]


def test_fully_mapped_segment_yields_five_facts(media_factory, mount_factory) -> None:
    mount = _mount(media_factory, mount_factory)

    assert _run(mount) == 0
    assert mount.result_entries() == ["facts.json", "manifest.json"]
    facts = _facts(mount)
    assert [(f["startMs"], f["endMs"]) for f in facts] == [(k * 1000, (k + 1) * 1000) for k in range(5)]
    validator = Draft202012Validator(PAYLOAD_SCHEMA)
    for fact in facts:
        assert fact["kind"] == "frame-second-probe" and fact["confidence"] == 1.0
        assert fact["payload"]["decodedFrames"] == 25
        validator.validate(fact["payload"])
        luma = fact["payload"]["meanLuma"]
        assert luma["min"] <= luma["mean"] <= luma["max"]


def test_partial_mapping_clips_facts_to_the_mapped_part(media_factory, mount_factory) -> None:
    spans = [{"mediaStartMs": 500, "mediaEndMs": 2500, "matchStartMs": 1500}]
    mount = _mount(media_factory, mount_factory, spans)

    assert _run(mount) == 0
    facts = _facts(mount)
    assert [(f["startMs"], f["endMs"]) for f in facts] == [(1500, 2000), (2000, 3000), (3000, 3500)]
    assert [f["payload"]["decodedFrames"] for f in facts] == [12, 25, 13]


def test_missing_segment_media_is_an_input_error(media_factory, mount_factory, capsys) -> None:
    mount = _mount(media_factory, mount_factory, with_segment_media=False)
    for entry in mount.media_folder.iterdir():
        entry.unlink()
    mount.media_folder.rmdir()

    assert _run(mount) == 2
    assert "segment-media-required" in capsys.readouterr().err
    assert mount.result_entries() == []
