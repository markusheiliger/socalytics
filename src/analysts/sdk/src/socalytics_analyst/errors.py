"""SDK errors. Messages never carry payloads, file contents, or job fields beyond contract identifiers."""

from __future__ import annotations

INPUT_MISSING = "input-missing"
INPUT_INVALID = "input-invalid"
UNSUPPORTED_CONTRACT_VERSION = "unsupported-contract-version"
JOB_CONTEXT_INCOMPLETE = "job-context-incomplete"
INPUT_STAGING_MISMATCH = "input-staging-mismatch"
INPUT_ARTIFACT_MISMATCH = "input-artifact-mismatch"
FACT_OUT_OF_WINDOW = "fact-out-of-window"
FACT_INVALID = "fact-invalid"
ARTIFACT_INVALID = "artifact-invalid"
VALIDITY_WINDOW_MISSING = "validity-window-missing"
VALIDITY_WINDOW_INVALID = "validity-window-invalid"
CONFIDENCE_MISSING = "confidence-missing"
RESULT_VALUE_INVALID = "result-value-invalid"
RESULT_UNEXPECTED_FILE = "result-unexpected-file"
RESULT_ALREADY_FINALIZED = "result-already-finalized"
OUTPUT_INVALID = "output-invalid"
ANALYST_FAILED = "analyst-failed"

INPUT_EXIT_CODE = 2
RESULT_EXIT_CODE = 1


class AnalystSdkError(Exception):
    """Base of all SDK errors; `category` is one of the constants of this module."""

    exit_code = RESULT_EXIT_CODE

    def __init__(self, category: str, message: str) -> None:
        super().__init__(f"{category}: {message}")
        self.category = category
        self.detail = message


class InputError(AnalystSdkError):
    """The job inputs are missing, invalid, or unsupported (exit 2)."""

    exit_code = INPUT_EXIT_CODE


class ResultError(AnalystSdkError):
    """The result cannot be written or the Analyst failed (exit 1)."""

    exit_code = RESULT_EXIT_CODE
