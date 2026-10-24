from __future__ import annotations

import importlib
import tomllib
from importlib import metadata
from pathlib import Path

LOCK = Path(__file__).resolve().parents[1] / "uv.lock"


def _locked_version(package: str) -> str:
    packages = tomllib.loads(LOCK.read_text(encoding="utf-8"))["package"]
    return next(entry["version"] for entry in packages if entry["name"] == package)


def test_locked_modules_import() -> None:
    for module in ("cv2", "numpy", "socalytics_analyst"):
        assert importlib.import_module(module) is not None


def test_numpy_matches_lock() -> None:
    import numpy

    assert numpy.__version__ == _locked_version("numpy")
    assert metadata.version("numpy") == _locked_version("numpy")


def test_opencv_matches_lock() -> None:
    import cv2

    locked = _locked_version("opencv-python-headless")
    assert metadata.version("opencv-python-headless") == locked
    # The wheel version appends a build number to the library version cv2 reports.
    assert locked.startswith(f"{cv2.__version__}.")
