# Contract: SDK Author Interface

The public, internal-only Python interface of `socalytics_analyst` (distribution `socalytics-analyst-sdk` 1.x). Names
are the plan's; implementation may add private helpers but not change these semantics.

## Entry point

```python
from socalytics_analyst import run, AnalystContext, Fact

def analyse(context: AnalystContext) -> None:
    for detection in detect(context.scratch):          # author code
        context.result.add_fact(Fact(kind="player-detected", start_ms=..., end_ms=...,
                                     confidence=0.9, payload={"trackId": "t-7"}))
    context.result.set_confidence(0.9)

if __name__ == "__main__":
    run(analyse, output_type="foundational-facts")
```

| Call | Behavior |
| --- | --- |
| `run(analyst, *, output_type, root=Path("/socalytics"))` | Loads and verifies inputs, calls `analyst(context)`, finalizes, prints the summary line, exits 0; exits 2 for input errors and 1 otherwise (research R9). `root` is for tests only. |

## `AnalystContext`

| Member | Type | Notes |
| --- | --- | --- |
| `job` | `LogicalJob` | typed, frozen |
| `attempt` | `AttemptContext` | typed, frozen |
| `inputs` | `StagedInputs` | empty for jobs without dependencies; `inputs.dependency(capability_id)` |
| `result` | `ResultWriter` | the only mutable member |
| `scratch` | `pathlib.Path` | `/socalytics/scratch` |

## `ResultWriter`

| Method | Effect | Refusal |
| --- | --- | --- |
| `add_fact(fact)` | appends to `facts.json` | `fact-out-of-window`, `fact-invalid`, `result-already-finalized` |
| `add_artifact(name, media_type, data: bytes)` | stages a supporting artifact | `artifact-invalid` (name pattern, `manifest.json`, `facts.json`, duplicate, empty) |
| `set_validity_window(start_ms, end_ms)` | narrows (segment) or states (match) the window | `validity-window-invalid` |
| `set_confidence(value)` | sets the overall confidence (0..1) | `result-value-invalid` (out of range) |
| `add_evidence_reference(identifier)` | appends an evidence identifier | `result-value-invalid` (not an identifier) |
| `finalize()` | called by `run`; authors do not call it | data-model finalization checks |

Confidence rule: the author MUST call `set_confidence` when at least one fact was added; with no facts the
confidence defaults to `0`. Finalization with facts but without a confidence fails `confidence-missing`.

## Errors

All SDK errors derive from `AnalystSdkError` with a `category` from the data model; `InputError` (exit 2) and
`ResultError` (exit 1) are its two subclasses. Messages never contain payloads, file contents, or job fields beyond
contract identifiers.
