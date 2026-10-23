# Data Model: Web UI Analysis Run Comparison

All new types are application-owned, transient presentation models in the
existing Web UI. No persistent entity, migration, server contract, or
browser-storage schema is introduced.

## Comparison Selection

| Field | Meaning and validation |
| --- | --- |
| `matchId` | Route match identity, validated by a current authorized match read. |
| `leftRunId` | Explicit selected left identity; may be prefilled from review. |
| `rightRunId` | Explicit selected right identity. |

An incomplete selection is selectable, not an error or a successful comparison.
A complete selection requires different IDs and two known terminal runs.
Both statuses must return the requested run ID and the selected match/team.
Neither being current is required. Unknown states remain inspectable in
eligibility feedback but are not treated as terminal.

Candidate pages are separate memory-only selection state: items,
continuation token, loading/error, and exhaustion. They are not authoritative
comparison context. A selected ID is not replaced when it is absent from a
candidate page.

## Run Context

Retain existing `AnalysisRunStatus` identity, timestamps, state, workflow and
nodes. Extend its mapping with:

| Field | Source and behavior |
| --- | --- |
| `isCurrent` | Required explicit boolean from the status response. False does not itself mean superseded. |
| `supersededBy` | Nullable successor run ID, recording-set ID, and finalization timestamp. A non-null object must be complete. |
| `supersededAt` | Nullable supersession timestamp; do not invent a value. |
| `nodes[].inputCoverage` | Nullable/not-reported dependency coverage, distinct from a reported empty list. |

Each side joins the run's exact `recordingSetVersionId` to
`RecordingLineageAdapter.getSet(matchId, id)`. Require returned set ID,
match, and team to agree. Retain finalization time and ordered members'
recording-version IDs, content digests, timeline-mapping IDs/digests.
Missing required set metadata produces an explicit unavailable/incompatible
side rather than an empty recording set. The detailed-review link is always
`/matches/{matchId}/analysis-runs/{thatSideRunId}`.

Current and superseded labels are fresh metadata, not execution states.
There is also a valid non-current, non-superseded history state, especially
for runs of the same recording-set version.

## Node Input Coverage

Each reported dependency entry belongs to one run node:

| Field | Meaning |
| --- | --- |
| `upstreamNodeKey` | Pinned upstream node identity. |
| `coverageMode` | Reported dependency policy; retain raw unfamiliar values with an unsupported-policy explanation. |
| `minimumAcceptedFraction` | Optional reported fraction, finite and within zero to one when present. |
| `accepted`, `missing`, `failed`, `blocked`, `skipped` | Nonnegative safe integer execution-unit counts. |

Do not merge entries across dependencies or infer upstream identity from
aggregate counts. Show per-node input coverage independently on each side.
For aligned streams compare dependency entries only by their explicit
upstream node keys; changed/missing keys and policy differences stay visible.
Missing/null input coverage means not reported, not zero coverage.
Unchanged reports do not verify equal full input snapshots.

## Fact Stream and Pinned Provenance

Reuse `FactStreamSummary`, `CoverageCounts`, and `Provenance`:

- Within-run stream identity: unique `nodeKey`.
- Alignment key: tuple of `capabilityId` and `executionScope`.
- Contract fields: capability version, schema version, output type.
- Quantity: nonnegative safe integer `factCount`.
- Output coverage: separate accepted/missing/failed/blocked/skipped counts.
- Provenance: workflow ID/version, profile ID/digest, image digest, optional
  model digest.

Required metadata must exist. A missing optional model digest is "not
reported"; it is neither an error nor a fabricated digest. All full IDs and
digests remain inspectable as text. Stream provenance workflow must agree with
its containing run's workflow; conflicting identities are incompatible.
A stream's node identity must resolve to its run's node metadata; missing or
contradicting node/capability/scope metadata is incompatible, not silently
omitted.

## Stream Comparison Entry

Use a discriminated presentation type:

| Kind | Members | Meaning |
| --- | --- | --- |
| `aligned` | One left stream, one right stream, field differences | Exactly one stream for this capability/scope on each side. |
| `unmatched` | Side, one stream, reason | Other side has no stream, or either side has multiple candidates. |

Every source stream appears once on its own side. For an ambiguous group,
emit all left and right streams as unmatched; do not zip by order or pair a
convenient subset. No absent counterpart gets a synthetic count or provenance.
Differences are descriptive fields, never scores: node ID, capability
version, schema, output type, profile, image, optional model, and reported
input-coverage metadata. Recording-set and workflow differences belong to
the pair context rather than guessed stream equivalence.

## Result Availability

Let `expected = accepted + missing + failed + blocked + skipped`. These
reported output coverage counts remain visible regardless of the primary
description.

| Condition | Description |
| --- | --- |
| `expected === 0` and zero facts | No expected execution units reported; completeness is not established. |
| `accepted === 0`, `expected > 0` | No accepted results; explain missing/failed/blocked/skipped work separately. |
| `accepted > 0`, any other count positive | Accepted results with incomplete coverage, including zero-fact partial results. |
| `accepted > 0`, all other counts zero, zero facts | Accepted zero-fact results with complete reported coverage. |
| `accepted > 0`, all other counts zero, positive facts | Facts with complete reported coverage. |
| Positive facts with no accepted units, invalid counts, or contradictory required metadata | Incompatible metadata, never successful analysis. |

Skipped analysis remains explicitly labelled in every relevant row, including
all-skipped coverage. A zero-stream workflow is a valid distinct whole-run
case only when the stream collection is explicitly present and empty.

## Comparison Availability and Lifecycle

States: `selecting`, `invalidSelection`, `loading`, `ready`, `notFound`,
`notPermitted`, `offline`, `incompatible`, and `error`. Session end uses the
existing signed-out route handling. Loading/error may identify a side without
displaying its results as a completed comparison.

The active request identity contains member ID, match ID, ordered run IDs,
and generation. A ready model belongs to exactly that identity. Starting any
freshness cycle invalidates the ready model before asynchronous work.
Only all successful required reads for the current identity transition
`loading -> ready`.

Selection changes, unmount, offline, access loss, and session end retire the
generation and discard its data. Refresh/return/reconnect starts a new
generation without changing the chosen IDs. Late responses for retired
generations cannot restore results. No automatic terminal-run polling or
run mutation exists.
