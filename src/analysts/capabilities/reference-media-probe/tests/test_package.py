import socalytics_analyst

import reference_media_probe


def test_package_identity():
    assert reference_media_probe.CAPABILITY_ID == "reference-media-probe"
    assert reference_media_probe.CAPABILITY_VERSION == "1.0.0"
    assert reference_media_probe.FACT_KIND == "frame-second-probe"


def test_sdk_is_importable():
    assert socalytics_analyst is not None
