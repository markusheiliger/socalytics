from __future__ import annotations

from pathlib import Path

import pytest

from analyst_evaluation_harness.config import ConfigRefusal
from analyst_evaluation_harness.corpus import Clip, load_corpus
from analyst_evaluation_harness.matching import NO_FACT, PAYLOAD_MISMATCH, pair_clip
from analyst_evaluation_harness.metrics import Counts, clip_counts
from analyst_evaluation_harness.runners import PREDICT_EVERYTHING, PREDICT_NOTHING, run_builtin
from analyst_evaluation_harness.staging import stage_mount
from support.builders import KIND, build_manifest, label

OTHER_KIND = "other-event"


def _run(rule: str, clip: Clip, folder: Path, kinds: tuple[str, ...] = (KIND,)):
    folder.mkdir(parents=True, exist_ok=True)
    mount = stage_mount(
        folder,
        "comparator-run-1",
        clip_file=clip.path,
        match_start_ms=clip.match_start_ms,
        duration_ms=clip.duration_ms,
        capability_id="test-capability",
        capability_version="1.0.0",
        output_type="foundational-facts",
    )
    return run_builtin(rule, mount, kinds)


def _clips(corpus_factory, **options) -> dict[str, Clip]:
    corpus = load_corpus(corpus_factory(**options).manifest_path)
    return {clip.clip_id: clip for clip in corpus.clips}


def _counts(clip: Clip, facts, kinds=(KIND,)) -> Counts:
    total = Counts()
    for pairing in pair_clip(clip.labels, facts, None, kinds):
        labels = sum(1 for item in clip.labels if item.label_kind == pairing.label_kind)
        total += clip_counts(
            labels=labels,
            true_positives=pairing.true_positives,
            false_positives=len(pairing.false_positives),
            failed=False,
        )
    return total


def test_predict_nothing_writes_no_facts(corpus_factory, tmp_path: Path) -> None:
    for clip in _clips(corpus_factory).values():
        outcome = _run(PREDICT_NOTHING, clip, tmp_path / clip.clip_id)

        assert outcome.status == "succeeded"
        assert outcome.failure_category is None
        assert outcome.exit_code == 0
        assert outcome.facts is not None and outcome.facts.facts == ()


def test_predict_nothing_counts_every_label_as_a_false_negative(corpus_factory, tmp_path: Path) -> None:
    clip = _clips(corpus_factory)["fixed-a"]
    outcome = _run(PREDICT_NOTHING, clip, tmp_path / "run")

    assert _counts(clip, outcome.facts.facts) == Counts(0, 0, 1)
    pairing = pair_clip(clip.labels, outcome.facts.facts, None, (KIND,))[0]
    assert [(missed.label_index, missed.reason) for missed in pairing.missed_labels] == [(0, NO_FACT)]


def test_predict_everything_writes_one_fact_per_covered_second(corpus_factory, tmp_path: Path) -> None:
    clips = _clips(corpus_factory)
    for clip in clips.values():
        outcome = _run(PREDICT_EVERYTHING, clip, tmp_path / clip.clip_id)
        facts = outcome.facts.facts

        assert outcome.status == "succeeded"
        assert [fact.start_ms for fact in facts] == list(
            range(clip.match_start_ms, clip.match_start_ms + clip.duration_ms, 1000)
        )
        assert all(fact.end_ms - fact.start_ms == 1000 for fact in facts)
        assert all(fact.kind == KIND and fact.payload == {} for fact in facts)


def test_predict_everything_uses_the_match_start_of_the_clip(corpus_factory, tmp_path: Path) -> None:
    clips = _clips(corpus_factory)
    assert clips["broadcast-a"].match_start_ms == 10000

    facts = _run(PREDICT_EVERYTHING, clips["broadcast-a"], tmp_path / "run").facts.facts

    assert facts[0].start_ms == 10000
    assert facts[-1].end_ms == 15000
    assert len(facts) == 5


def test_predict_everything_counts_on_the_builder_corpus(corpus_factory, tmp_path: Path) -> None:
    clips = _clips(corpus_factory)
    expected = {
        "fixed-a": Counts(1, 4, 0),
        "fixed-b": Counts(1, 5, 0),
        "broadcast-a": Counts(1, 4, 0),
        "broadcast-b": Counts(1, 5, 0),
    }
    for clip_id, counts in expected.items():
        clip = clips[clip_id]
        facts = _run(PREDICT_EVERYTHING, clip, tmp_path / clip_id).facts.facts

        assert _counts(clip, facts) == counts, clip_id


def test_predict_everything_writes_every_declared_label_kind(corpus_factory, tmp_path: Path) -> None:
    clip = _clips(corpus_factory)["fixed-a"]

    facts = _run(PREDICT_EVERYTHING, clip, tmp_path / "run", (OTHER_KIND, KIND)).facts.facts

    assert len(facts) == 10
    assert {kind: sum(1 for fact in facts if fact.kind == kind) for kind in (KIND, OTHER_KIND)} == {
        KIND: 5,
        OTHER_KIND: 5,
    }


def test_predict_everything_payload_expectation_is_a_payload_mismatch(corpus_factory, tmp_path: Path) -> None:
    labels = {"broadcast-a": [label(position_ms=11000, payload={"team": "home"})]}
    clip = _clips(corpus_factory, labels=labels)["broadcast-a"]

    facts = _run(PREDICT_EVERYTHING, clip, tmp_path / "run").facts.facts
    pairing = pair_clip(clip.labels, facts, None, (KIND,))[0]

    assert pairing.true_positives == 0
    assert [(missed.label_index, missed.reason) for missed in pairing.missed_labels] == [(0, PAYLOAD_MISMATCH)]
    assert _counts(clip, facts) == Counts(0, 5, 1)


def test_the_comparator_is_produced_in_process_without_execution(
    corpus_factory, tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    import subprocess

    def refuse(*args, **kwargs):
        raise AssertionError("a built-in comparator must not execute anything")

    monkeypatch.setattr(subprocess, "Popen", refuse)
    monkeypatch.setattr(subprocess, "run", refuse)
    clip = _clips(corpus_factory)["fixed-a"]

    assert _run(PREDICT_EVERYTHING, clip, tmp_path / "run").status == "succeeded"


def test_an_unknown_rule_is_refused(corpus_factory, tmp_path: Path) -> None:
    clip = _clips(corpus_factory)["fixed-a"]

    with pytest.raises(ConfigRefusal):
        _run("predict-sometimes", clip, tmp_path / "run")


def test_an_unreadable_segment_manifest_is_a_failed_outcome(corpus_factory, tmp_path: Path) -> None:
    clip = _clips(corpus_factory, clips=(("only", "fixed", 25.0, 3000, 0),))["only"]
    folder = tmp_path / "run"
    folder.mkdir()
    mount = stage_mount(
        folder,
        "comparator-run-1",
        clip_file=clip.path,
        match_start_ms=0,
        duration_ms=3000,
        capability_id="test-capability",
        capability_version="1.0.0",
        output_type="foundational-facts",
    )
    mount.manifest_path.write_text("{", encoding="utf-8")

    outcome = run_builtin(PREDICT_EVERYTHING, mount, (KIND,))

    assert outcome.status == "failed"
    assert outcome.failure_category == "execution-failed"
    assert outcome.facts is None
