"""The entry point in a subprocess: exit status, summary line, and failure line (FR-020, FR-021)."""

from __future__ import annotations

import re
import subprocess
import sys
import textwrap
from collections.abc import Callable
from pathlib import Path

from fixtures.mounts import MATCH_JOB_EXAMPLE, Mount, validate_written_json

EXAMPLE = Path(__file__).resolve().parents[1] / "examples" / "minimal_analyst.py"

# The container mount root is fixed, so the driver points the entry point at a temporary root and then runs the
# Analyst script as `__main__`, exactly as the container does.
DRIVER = textwrap.dedent(
    """
    import runpy
    import sys
    from pathlib import Path

    from socalytics_analyst import entrypoint

    entrypoint.run.__kwdefaults__["root"] = Path(sys.argv[2])
    runpy.run_path(sys.argv[1], run_name="__main__")
    """
)

SECRET = "payload-secret-marker"
SUMMARY = re.compile(r"^socalytics-analyst: finalized facts=\d+ artifacts=\d+$")
FAILURE = re.compile(r"^socalytics-analyst: failed [a-z-]+$")


def run_script(script: Path, mount: Mount) -> subprocess.CompletedProcess[str]:
    return subprocess.run(
        [sys.executable, "-c", DRIVER, str(script), str(mount.root)],
        capture_output=True,
        text=True,
        timeout=120,
        check=False,
    )


def write_analyst(tmp_path: Path, body: str) -> Path:
    script = tmp_path / "analyst.py"
    script.write_text(
        "from socalytics_analyst import Fact, run\n\n\n"
        + textwrap.dedent(body)
        + '\n\nif __name__ == "__main__":\n    run(analyse, output_type="foundational-facts")\n',
        encoding="utf-8",
    )
    return script


def assert_failure(completed: subprocess.CompletedProcess[str], code: int) -> None:
    assert completed.returncode == code
    assert completed.stdout == ""
    lines = completed.stderr.strip().splitlines()
    assert len(lines) == 1 and FAILURE.match(lines[0]), completed.stderr
    assert SECRET not in completed.stderr


def test_minimal_analyst_example_writes_a_segment_result(segment_mount: Mount) -> None:
    completed = run_script(EXAMPLE, segment_mount)

    assert completed.returncode == 0, completed.stderr
    assert completed.stderr == ""
    assert SUMMARY.match(completed.stdout.strip()), completed.stdout
    manifest = validate_written_json("result-manifest", segment_mount.result / "manifest.json")
    assert "segment" in manifest["scope"]
    assert "facts.json" in {entry["name"] for entry in manifest["artifacts"]}


def test_minimal_analyst_example_writes_a_match_result(mount_factory: Callable[..., Mount]) -> None:
    match_mount = mount_factory(job_example_name=MATCH_JOB_EXAMPLE, adjust_job=lambda job: job.update(inputs=[]))
    completed = run_script(EXAMPLE, match_mount)

    assert completed.returncode == 0, completed.stderr
    assert SUMMARY.match(completed.stdout.strip()), completed.stdout
    manifest = validate_written_json("result-manifest", match_mount.result / "manifest.json")
    assert manifest["scope"]["match"] is True


def test_analyst_exception_exits_1_without_manifest_or_payload(segment_mount: Mount, tmp_path: Path) -> None:
    script = write_analyst(
        tmp_path,
        f"""
        def analyse(context):
            raise RuntimeError("{SECRET}")
        """,
    )

    completed = run_script(script, segment_mount)

    assert_failure(completed, 1)
    assert "analyst-failed" in completed.stderr
    assert segment_mount.result_entries() == []


def test_analyst_exception_after_a_fact_leaves_no_result(segment_mount: Mount, tmp_path: Path) -> None:
    script = write_analyst(
        tmp_path,
        f"""
        def analyse(context):
            window = context.job.segment_window
            context.result.add_fact(
                Fact(kind="example-seen", start_ms=window.start_ms, end_ms=window.end_ms, confidence=1.0,
                     payload={{"note": "{SECRET}"}})
            )
            context.result.set_confidence(1.0)
            raise ValueError("{SECRET}")
        """,
    )

    completed = run_script(script, segment_mount)

    assert_failure(completed, 1)
    assert segment_mount.result_entries() == []


def test_refused_fact_exits_1_without_manifest_or_payload(segment_mount: Mount, tmp_path: Path) -> None:
    script = write_analyst(
        tmp_path,
        f"""
        def analyse(context):
            window = context.job.segment_window
            context.result.add_fact(
                Fact(kind="example-seen", start_ms=window.start_ms, end_ms=window.end_ms + 1, confidence=1.0,
                     payload={{"note": "{SECRET}"}})
            )
        """,
    )

    completed = run_script(script, segment_mount)

    assert_failure(completed, 1)
    assert "fact-out-of-window" in completed.stderr
    assert segment_mount.result_entries() == []


def test_missing_confidence_exits_1_without_manifest(segment_mount: Mount, tmp_path: Path) -> None:
    script = write_analyst(
        tmp_path,
        """
        def analyse(context):
            window = context.job.segment_window
            context.result.add_fact(
                Fact(kind="example-seen", start_ms=window.start_ms, end_ms=window.end_ms, confidence=1.0, payload={})
            )
        """,
    )

    completed = run_script(script, segment_mount)

    assert_failure(completed, 1)
    assert segment_mount.result_entries() == []


def test_missing_job_exits_2_and_does_not_call_the_analyst(
    mount_factory: Callable[..., Mount], tmp_path: Path
) -> None:
    mount = mount_factory()
    mount.job_path.unlink()
    marker = tmp_path / "called"
    script = write_analyst(
        tmp_path,
        f"""
        def analyse(context):
            open({str(marker)!r}, "w").close()
        """,
    )

    completed = run_script(script, mount)

    assert_failure(completed, 2)
    assert "input-missing" in completed.stderr
    assert not marker.exists()
    assert mount.result_entries() == []


def test_invalid_job_exits_2(mount_factory: Callable[..., Mount]) -> None:
    mount = mount_factory()
    mount.job_path.write_text(f'{{"secret": "{SECRET}"', encoding="utf-8")

    completed = run_script(EXAMPLE, mount)

    assert_failure(completed, 2)
    assert "input-invalid" in completed.stderr
    assert mount.result_entries() == []


def test_unsupported_contract_version_exits_2(mount_factory: Callable[..., Mount]) -> None:
    mount = mount_factory(adjust_attempt=lambda attempt: attempt.update(contractVersion="2.0.0"))

    completed = run_script(EXAMPLE, mount)

    assert_failure(completed, 2)
    assert mount.result_entries() == []
