from __future__ import annotations

import hashlib
import re

import pytest

from analyst_evaluation_harness.canonical import (
    canonical_bytes,
    canonical_text,
    identity_of,
    identity_of_bytes,
    round_floats,
    round_metric,
)


def test_key_order_does_not_change_bytes() -> None:
    first = {"b": 1, "a": {"y": [1, 2], "x": "v"}}
    second = {"a": {"x": "v", "y": [1, 2]}, "b": 1}
    assert canonical_bytes(first) == canonical_bytes(second)
    assert canonical_text(first) == '{"a":{"x":"v","y":[1,2]},"b":1}'


def test_list_order_is_significant() -> None:
    assert canonical_bytes([1, 2]) != canonical_bytes([2, 1])


def test_non_ascii_is_kept_as_utf8() -> None:
    assert canonical_bytes({"k": "é"}) == '{"k":"é"}'.encode("utf-8")


@pytest.mark.parametrize(
    ("value", "expected"),
    [
        (0.1234565, 0.123456),
        (0.1234575, 0.123458),
        (0.5, 0.5),
        (1, 1.0),
        (0.0000004, 0.0),
        (0.0000006, 0.000001),
        (-0.0000004, 0.0),
    ],
)
def test_round_metric_half_even_six_decimals(value: float, expected: float) -> None:
    assert round_metric(value) == expected


def test_round_metric_normalizes_negative_zero() -> None:
    assert canonical_text(round_metric(-0.0000004)) == "0.0"


@pytest.mark.parametrize("value", [float("nan"), float("inf"), float("-inf")])
def test_round_metric_refuses_non_finite(value: float) -> None:
    with pytest.raises(ValueError):
        round_metric(value)


@pytest.mark.parametrize("value", [True, "0.5", None])
def test_round_metric_refuses_non_numbers(value: object) -> None:
    with pytest.raises(TypeError):
        round_metric(value)  # type: ignore[arg-type]


def test_round_floats_rounds_nested_floats_only() -> None:
    document = {"a": [0.1234565, {"b": 0.0000006}], "c": 3, "d": True, "e": "x"}
    assert round_floats(document) == {
        "a": [0.123456, {"b": 0.000001}],
        "c": 3,
        "d": True,
        "e": "x",
    }


@pytest.mark.parametrize("value", [float("nan"), float("inf")])
def test_canonical_bytes_refuses_non_finite(value: float) -> None:
    with pytest.raises(ValueError):
        canonical_bytes({"metric": value})


def test_identity_is_stable_and_prefixed() -> None:
    document = {"b": 2, "a": 1}
    identity = identity_of(document)
    assert re.fullmatch(r"sha-256:[0-9a-f]{64}", identity)
    assert identity == identity_of({"a": 1, "b": 2})
    assert identity == "sha-256:" + hashlib.sha256(b'{"a":1,"b":2}').hexdigest()
    assert identity == identity_of_bytes(b'{"a":1,"b":2}')


def test_identity_changes_with_content() -> None:
    assert identity_of({"a": 1}) != identity_of({"a": 2})


def test_identity_excludes_named_top_level_fields() -> None:
    base = {"a": 1}
    with_identity = {"a": 1, "identity": "sha-256:" + "0" * 64}
    assert identity_of(with_identity, exclude=["identity"]) == identity_of(base)
    assert identity_of(with_identity) != identity_of(base)
