from __future__ import annotations

import ast
import json
import shutil
import socket
from collections.abc import Callable
from pathlib import Path

import pytest
from socalytics_analyst import run

import reference_media_probe
from reference_media_probe.__main__ import analyse
from support.mounts import Mount

WINDOW = {"startMs": 0, "endMs": 5000}
SPANS = [{"mediaStartMs": 0, "mediaEndMs": 5000, "matchStartMs": 0}]
MEDIA_MARKER = b"MEDIA-CONTENT-MARKER"
MAX_STDERR_LINE = 200


def _mount(mount_factory: Callable[..., Mount], media: Path, **options: object) -> Mount:
    return mount_factory(
        media,
        nominal_window=WINDOW,
        requested_source_interval=WINDOW,
        encoded_interval=WINDOW,
        timeline_spans=SPANS,
        **options,
    )


def _run(mount: Mount, capsys: pytest.CaptureFixture[str]) -> tuple[int, str]:
    with pytest.raises(SystemExit) as exit_info:
        run(analyse, output_type="foundational-facts", root=mount.root)
    return exit_info.value.code or 0, capsys.readouterr().err


def _assert_failed_cleanly(mount: Mount, stderr: str, category: str) -> None:
    assert "manifest.json" not in mount.result_entries()
    lines = stderr.splitlines()
    assert len(lines) == 1
    assert len(lines[0]) <= MAX_STDERR_LINE
    assert category in lines[0]
    assert MEDIA_MARKER.decode() not in stderr


def test_job_without_segment_media_and_without_media_directory_exits_2(
    media_factory: Callable[..., Path], mount_factory: Callable[..., Mount], capsys: pytest.CaptureFixture[str]
) -> None:
    mount = _mount(mount_factory, media_factory(), with_segment_media=False)
    shutil.rmtree(mount.media_folder)

    code, stderr = _run(mount, capsys)

    assert code == 2
    _assert_failed_cleanly(mount, stderr, "segment-media-required")


def test_staged_media_differing_from_its_manifest_digest_exits_2(
    media_factory: Callable[..., Path], mount_factory: Callable[..., Mount], capsys: pytest.CaptureFixture[str]
) -> None:
    mount = _mount(mount_factory, media_factory())
    with mount.media_path.open("ab") as staged:
        staged.write(MEDIA_MARKER)

    code, stderr = _run(mount, capsys)

    assert code == 2
    _assert_failed_cleanly(mount, stderr, "segment-media-digest-mismatch")


def test_media_with_a_correct_digest_that_cannot_be_decoded_exits_1(
    tmp_path: Path, mount_factory: Callable[..., Mount], capsys: pytest.CaptureFixture[str]
) -> None:
    broken = tmp_path / "broken.mp4"
    broken.write_bytes(MEDIA_MARKER * 100)
    mount = _mount(mount_factory, broken)

    code, stderr = _run(mount, capsys)

    assert code == 1
    _assert_failed_cleanly(mount, stderr, "media-decode-failed")


def test_match_scoped_job_exits_2(
    media_factory: Callable[..., Path], mount_factory: Callable[..., Mount], capsys: pytest.CaptureFixture[str]
) -> None:
    mount = _mount(mount_factory, media_factory(), with_segment_media=False)
    shutil.rmtree(mount.media_folder)
    job = json.loads(mount.job_path.read_text(encoding="utf-8"))
    job["executionScope"] = "match"
    for field in ("segment", "segmentWindow", "segmentMedia"):
        job.pop(field, None)
    mount.job_path.write_text(json.dumps(job), encoding="utf-8")

    code, stderr = _run(mount, capsys)

    assert code == 2
    _assert_failed_cleanly(mount, stderr, "segment-media-required")


def test_successful_run_needs_no_socket_connection(
    media_factory: Callable[..., Path],
    mount_factory: Callable[..., Mount],
    capsys: pytest.CaptureFixture[str],
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    def refuse(*_args: object, **_kwargs: object) -> None:
        raise AssertionError("the capability must not open a network connection")

    for name in ("connect", "connect_ex"):
        monkeypatch.setattr(socket.socket, name, refuse)
    for name in ("create_connection", "getaddrinfo"):
        monkeypatch.setattr(socket, name, refuse)
    mount = _mount(mount_factory, media_factory())

    code, stderr = _run(mount, capsys)

    assert code == 0
    assert stderr == ""
    assert (mount.result / "manifest.json").is_file()


def test_no_module_reads_the_environment() -> None:
    package = Path(reference_media_probe.__file__).parent
    offenders: list[str] = []
    for source in sorted(package.rglob("*.py")):
        for node in ast.walk(ast.parse(source.read_text(encoding="utf-8"))):
            if isinstance(node, ast.Attribute) and node.attr in {"environ", "getenv", "environb", "getenvb"}:
                offenders.append(f"{source.name}:{node.lineno}")
            if isinstance(node, ast.ImportFrom) and node.module == "os":
                offenders.extend(f"{source.name}:{node.lineno}" for a in node.names if a.name in {"environ", "getenv"})
    assert offenders == []
