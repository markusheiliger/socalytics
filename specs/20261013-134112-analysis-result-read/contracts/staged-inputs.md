# Contract: Staged Inputs for Analysts

The layout the Analyst Manager writes into an attempt's input mount before
launch (research.md § R5). Analysts read only from here; they receive no
credentials, grants, or platform network access.

```text
inputs/
  {capabilityId}/
    coverage.json                        # { "upstream": {...}, "coverage": [ { segmentId | match, state } ] }
    {acceptedResultId}/
      manifest.json                      # result-manifest v1, verified against manifestChecksum
      {artifact name}                    # verified against the manifest's artifact digest
```

- `coverage.json` validates against the `coverage` and `upstream` definitions of
  `contracts/analysis/upstream-results/v1/` (one file per upstream dependency).
- Only the job's declared upstream dependencies appear; for a job without
  upstream dependencies, `inputs/` is absent.
- The whole attempt directory, including `inputs/`, is deleted when the attempt
  ends and at Manager start.
