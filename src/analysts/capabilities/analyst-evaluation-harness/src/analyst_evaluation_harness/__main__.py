"""Command line of the Analyst evaluation harness (contracts/cli.md)."""

from __future__ import annotations

import argparse
import json
import sys
from pathlib import Path

from .config import ConfigRefusal
from .corpus import CorpusValidationError, load_corpus
from .evaluate import Subject, evaluate
from .report import verify_report

EXIT_OK = 0
EXIT_UNEXPECTED = 1
EXIT_REFUSED = 2
EXIT_CLIP_FAILED = 3


class _Parser(argparse.ArgumentParser):
    """Reports an unusable command line as a refusal (exit code 2) on standard error."""

    def error(self, message: str):
        self.print_usage(sys.stderr)
        print(f"{self.prog}: error: {message}", file=sys.stderr)
        sys.exit(EXIT_REFUSED)


def _build_parser() -> argparse.ArgumentParser:
    parser = _Parser(prog="analyst_evaluation_harness", description="Evaluate an Analyst capability on a corpus.")
    commands = parser.add_subparsers(dest="command", required=True, parser_class=_Parser)

    evaluate_parser = commands.add_parser("evaluate", help="run the subject on every clip and write the report")
    evaluate_parser.add_argument("--corpus", required=True, type=Path, help="corpus directory")
    evaluate_parser.add_argument("--config", required=True, type=Path, help="evaluation configuration file")
    evaluate_parser.add_argument("--output", required=True, type=Path, help="output directory")
    evaluate_parser.add_argument("--subject", required=True, help="image digest reference or local command line")
    evaluate_parser.add_argument("--capability-id", required=True)
    evaluate_parser.add_argument("--capability-version", required=True)
    evaluate_parser.add_argument("--output-type", required=True)

    validate_parser = commands.add_parser("validate-corpus", help="validate the manifest and every clip digest")
    validate_parser.add_argument("--corpus", required=True, type=Path, help="corpus directory")

    verify_parser = commands.add_parser("verify-report", help="recompute the report identity and validate the schema")
    verify_parser.add_argument("report", type=Path, help="report.json to verify")
    return parser


def _refuse(problems: list[str]) -> int:
    for problem in problems:
        print(problem, file=sys.stderr)
    return EXIT_REFUSED


def _manifest_path(corpus: Path) -> Path:
    return corpus if corpus.is_file() else corpus / "corpus-manifest.json"


def _run_evaluate(arguments: argparse.Namespace) -> int:
    subject = Subject(
        reference=arguments.subject,
        capability_id=arguments.capability_id,
        capability_version=arguments.capability_version,
        output_type=arguments.output_type,
    )
    try:
        result = evaluate(arguments.corpus, arguments.config, arguments.output, subject)
    except CorpusValidationError as error:
        return _refuse([str(problem) for problem in error.problems])
    except ConfigRefusal as error:
        return _refuse(error.problems)
    print(f"report: {result.report_path}")
    print(f"identity: {result.identity}")
    for gate, status in result.report["gates"].items():
        print(f"{gate}: {status}")
    return EXIT_CLIP_FAILED if result.any_failed else EXIT_OK


def _run_validate_corpus(arguments: argparse.Namespace) -> int:
    try:
        corpus = load_corpus(_manifest_path(arguments.corpus))
    except CorpusValidationError as error:
        return _refuse([str(problem) for problem in error.problems])
    print(f"corpus valid: {corpus.corpus_id} {corpus.corpus_version} {corpus.identity}")
    return EXIT_OK


def _run_verify_report(arguments: argparse.Namespace) -> int:
    try:
        report = json.loads(arguments.report.read_text(encoding="utf-8"))
    except (OSError, UnicodeDecodeError) as error:
        return _refuse([f"report {arguments.report}: cannot be read ({error})"])
    except json.JSONDecodeError as error:
        return _refuse([f"report {arguments.report}: not valid JSON ({error})"])
    problems = verify_report(report)
    if problems:
        return _refuse([f"report {arguments.report}: {problem}" for problem in problems])
    print(f"report: {arguments.report}")
    print(f"identity: {report['identity']}")
    return EXIT_OK


_COMMANDS = {
    "evaluate": _run_evaluate,
    "validate-corpus": _run_validate_corpus,
    "verify-report": _run_verify_report,
}


def main(argv: list[str] | None = None) -> int:
    arguments = _build_parser().parse_args(argv)
    try:
        return _COMMANDS[arguments.command](arguments)
    except Exception as error:  # noqa: BLE001 - the contract maps every unexpected failure to exit code 1
        print(f"unexpected error: {type(error).__name__}: {error}", file=sys.stderr)
        return EXIT_UNEXPECTED


if __name__ == "__main__":
    sys.exit(main())
