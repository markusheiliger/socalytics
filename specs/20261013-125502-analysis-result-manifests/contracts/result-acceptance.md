# Contract: Result Acceptance in Completion

Extends the existing `POST /api/v1/analysis/attempts/{attemptId}/completion`
(attempt-completion contract `1.0.0`, unchanged on the wire).

## Order of checks for `outcome: succeeded`

1. Existing checks unchanged: contract shape, current fencing token,
   idempotency, body digests and declared upstream (`409 completion-rejected`
   as today).
2. Manifest verification (research.md § R3), outside the unit of work, in this
   order; the first failing check determines the reason:

   | # | Check | Reason code |
   | --- | --- | --- |
   | 1 | `manifestReference` = `<resultLocation>manifest.json`, declared | `result-manifest-missing` |
   | 2 | Object exists | `result-manifest-missing` |
   | 3 | Size ≤ `MaxManifestBytes` | `result-manifest-too-large` |
   | 4 | Storage-attested SHA-256 = reported `manifestChecksum` = declared digest | `result-manifest-checksum-mismatch` |
   | 5 | `contractVersion` supported | `result-manifest-version-unsupported` |
   | 6 | Valid against the schema | `result-manifest-schema-invalid` |
   | 7 | Scope and attempt equal the job, run, and attempt | `result-scope-mismatch` |
   | 8 | Provenance equals the pinned snapshot | `result-provenance-mismatch` |
   | 9 | Upstream lineage equals the job's recorded upstream | `result-lineage-mismatch` |
   | 10 | Artifacts declared and storage-attested | `result-artifact-invalid` |
   | 11 | Validity window within the job window; `factCount` ≥ 0 | `result-validity-invalid` |

3. Unit of work: re-check that the attempt is current (else `409 attempt-obsolete`).
   - All checks passed: accepted result plus manifest record; response as today.
   - A check failed: completion outcome `failed` with the reason, the existing
     failed-attempt transition, audit `analysis.result-rejected`; response
     `200` with `outcome: "failed"` and `reason`, so the Manager ends the
     attempt without retrying it.
4. Storage unavailable at any read: `503 object-storage-unavailable`, no state
   change; the Manager retries the identical completion.

Replays with the same idempotency key return the stored outcome without
re-verification.
