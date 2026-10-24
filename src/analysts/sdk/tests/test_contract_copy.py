import importlib.util
from pathlib import Path
from types import ModuleType

SCRIPT = Path(__file__).resolve().parents[1] / "scripts" / "sync_contracts.py"


def load_sync_module() -> ModuleType:
    spec = importlib.util.spec_from_file_location("sync_contracts", SCRIPT)
    assert spec is not None and spec.loader is not None
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def test_copy_matches_canonical_contracts() -> None:
    sync = load_sync_module()
    differences = sync.find_differences()
    assert not differences, (
        "The SDK schema copy is out of date:\n  "
        + "\n  ".join(differences)
        + f"\nRefresh it with: {sync.REFRESH_COMMAND}"
    )


def test_copy_includes_every_implemented_contract() -> None:
    sync = load_sync_module()
    copied = {path.as_posix() for path in sync.copied_files()}
    assert "common/v1/common.schema.json" in copied
    for name in sync.ANALYSIS_CONTRACTS:
        assert f"analysis/{name}/v1/{name}.schema.json" in copied
    assert "analysis/analyst-manifest/v1/releases/1.2.0.schema.json" in copied
    for name in sync.SEGMENT_CONTRACTS:
        assert f"segments/{name}/v1/{name}.schema.json" in copied
        assert f"segments/{name}/v1/releases/1.0.0.schema.json" in copied


def test_check_reports_a_differing_copy(tmp_path: Path, monkeypatch, capsys) -> None:
    sync = load_sync_module()
    monkeypatch.setattr(sync, "COPY_ROOT", tmp_path)
    assert sync.main(["--check"]) == 1
    assert sync.REFRESH_COMMAND in capsys.readouterr().err
    assert sync.main([]) == 0
    assert sync.main(["--check"]) == 0
