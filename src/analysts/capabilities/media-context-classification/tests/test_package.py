import json
from pathlib import Path

import socalytics_analyst

import media_context_classification

MANIFEST_PATH = Path(__file__).resolve().parent.parent / "analyst-manifest.json"


def test_package_constants_equal_manifest():
    manifest = json.loads(MANIFEST_PATH.read_text(encoding="utf-8"))
    assert media_context_classification.CAPABILITY_ID == manifest["capability"]["id"]
    assert media_context_classification.CAPABILITY_VERSION == manifest["capability"]["version"]


def test_package_identity():
    assert media_context_classification.CAPABILITY_ID == "media-context-classification"
    assert media_context_classification.CAPABILITY_VERSION == "1.0.0"
    assert media_context_classification.FACT_KIND == "shot-boundary"


def test_sdk_is_importable():
    assert socalytics_analyst is not None
