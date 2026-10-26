from __future__ import annotations

import json
from pathlib import Path

import pytest

from analyst_evaluation_harness.__main__ import main
from support.builders import KIND, build_config


def _write_config(tmp_path: Path, **options) -> Path:
    path = tmp_path / "config.json"
    path.write_text(json.dumps(build_config(**options)), encoding="utf-8")
    return path


def _evaluate_arguments(corpus, config: Path, output: Path, capability) -> list[str]:
    return [
        "evaluate",
        "--corpus", str(corpus.manifest_path),
        "--config", str(config),
        "--output", str(output),
        "--subject", capability.command,
        "--capability-id", "test-capability",
        "--capability-version", "1.0.0",
        "--output-type", "foundational-facts",
    ]  # fmt: skip


def test_evaluate_exits_zero_and_prints_report_identity_and_gates(
    tmp_path, corpus_factory, capability_factory, capsys
):
    corpus = corpus_factory()
    capability = capability_factory()
    output = tmp_path / "out"

    code = main(_evaluate_arguments(corpus, _write_config(tmp_path), output, capability))

    captured = capsys.readouterr()
    report = json.loads((output / "report.json").read_text(encoding="utf-8"))
    assert code == 0
    assert captured.err == ""
    assert f"report: {output / 'report.json'}" in captured.out
    assert f"identity: {report['identity']}" in captured.out
    for gate, status in report["gates"].items():
        assert f"{gate}: {status}" in captured.out
    assert capability.invocations == len(corpus.manifest["clips"])


def test_evaluate_accepts_the_corpus_directory(tmp_path, corpus_factory, capability_factory):
    corpus = corpus_factory()
    (corpus.root / "corpus-manifest.json").write_bytes(corpus.manifest_path.read_bytes())
    arguments = _evaluate_arguments(corpus, _write_config(tmp_path), tmp_path / "out", capability_factory())
    arguments[arguments.index("--corpus") + 1] = str(corpus.root)

    assert main(arguments) == 0
    assert (tmp_path / "out" / "report.json").is_file()


def test_refused_manifest_exits_two_without_running_a_clip(
    tmp_path, corpus_factory, capability_factory, capsys
):
    manifest = corpus_factory().manifest
    manifest["identity"] = "sha-256:" + "1" * 64
    corpus = corpus_factory(manifest=manifest)
    capability = capability_factory()
    output = tmp_path / "out"

    code = main(_evaluate_arguments(corpus, _write_config(tmp_path), output, capability))

    captured = capsys.readouterr()
    assert code == 2
    assert captured.err.strip() != ""
    assert captured.out == ""
    assert capability.invocations == 0
    assert not (output / "report.json").exists()


def test_refused_configuration_exits_two_without_running_a_clip(
    tmp_path, corpus_factory, capability_factory, capsys
):
    corpus = corpus_factory()
    capability = capability_factory()
    config = _write_config(tmp_path, label_kinds={"undeclared-kind": {"primaryMetric": "f1"}})
    output = tmp_path / "out"

    code = main(_evaluate_arguments(corpus, config, output, capability))

    captured = capsys.readouterr()
    assert code == 2
    assert "undeclared-kind" in captured.err or KIND in captured.err
    assert capability.invocations == 0
    assert not (output / "report.json").exists()


def test_unusable_command_line_exits_two(capsys):
    with pytest.raises(SystemExit) as raised:
        main(["evaluate", "--corpus", "somewhere"])

    assert raised.value.code == 2
    assert "error" in capsys.readouterr().err


def test_failed_clip_exits_three_and_still_writes_the_report(
    tmp_path, corpus_factory, capability_factory, capsys
):
    corpus = corpus_factory()
    capability = capability_factory(crash=True)
    output = tmp_path / "out"

    code = main(_evaluate_arguments(corpus, _write_config(tmp_path), output, capability))

    captured = capsys.readouterr()
    report = json.loads((output / "report.json").read_text(encoding="utf-8"))
    assert code == 3
    assert f"identity: {report['identity']}" in captured.out
    assert {clip["subject"]["execution"]["status"] for clip in report["clips"]} == {"failed"}


def test_validate_corpus_succeeds_for_a_valid_corpus(corpus_factory, capsys):
    corpus = corpus_factory()

    assert main(["validate-corpus", "--corpus", str(corpus.manifest_path)]) == 0

    captured = capsys.readouterr()
    assert corpus.manifest["identity"] in captured.out
    assert captured.err == ""


def test_validate_corpus_lists_every_problem_and_exits_two(corpus_factory, capsys):
    corpus = corpus_factory()
    corpus.clip_path("fixed-a").write_bytes(b"changed")
    corpus.clip_path("fixed-b").unlink()

    assert main(["validate-corpus", "--corpus", str(corpus.manifest_path)]) == 2

    captured = capsys.readouterr()
    assert "digest-mismatch" in captured.err
    assert "file-missing" in captured.err
    assert captured.out == ""


@pytest.fixture
def report_path(tmp_path, corpus_factory, capability_factory) -> Path:
    output = tmp_path / "out"
    assert main(_evaluate_arguments(corpus_factory(), _write_config(tmp_path), output, capability_factory())) == 0
    return output / "report.json"


def test_verify_report_accepts_a_written_report(report_path, capsys):
    capsys.readouterr()

    assert main(["verify-report", str(report_path)]) == 0

    captured = capsys.readouterr()
    assert json.loads(report_path.read_text(encoding="utf-8"))["identity"] in captured.out


def test_verify_report_refuses_a_changed_report(report_path, capsys):
    report = json.loads(report_path.read_text(encoding="utf-8"))
    report["corpus"]["corpusVersion"] = "9.9.9"
    report_path.write_text(json.dumps(report), encoding="utf-8")
    capsys.readouterr()

    assert main(["verify-report", str(report_path)]) == 2

    assert capsys.readouterr().err.strip() != ""


def test_verify_report_refuses_missing_and_invalid_files(tmp_path, capsys):
    broken = tmp_path / "broken.json"
    broken.write_text("{not json", encoding="utf-8")

    assert main(["verify-report", str(tmp_path / "missing.json")]) == 2
    assert main(["verify-report", str(broken)]) == 2

    assert capsys.readouterr().err.count("report ") >= 2
