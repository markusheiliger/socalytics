import media_context_classification as capability


def test_capability_identity() -> None:
    assert capability.CAPABILITY_ID == "media-context-classification"
    assert capability.CAPABILITY_VERSION == "1.0.0"
    assert capability.FACT_KIND == "shot-boundary"
