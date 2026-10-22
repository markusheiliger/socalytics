# Comparison UI Contract

## Routes and Entry Points

| Route | Contract |
| --- | --- |
| `/matches/:matchId/analysis-comparison` | Two-run selection for this match. |
| `/matches/:matchId/analysis-comparison?left=:runId` | Selection with the review-origin run preserved on the left. |
| `/matches/:matchId/analysis-comparison?left=:runId&right=:runId` | Validate and display exactly this ordered pair. |
| `/matches/:matchId/analysis-runs/:runId` | Existing detail route; unchanged, linked once per side. |

Match entry is a read-only "Compare analysis runs" link in the existing run
area. Review entry is offered for a known terminal state and retains its ID.
For nonterminal or unsupported review states, show why comparison is not
eligible rather than silently selecting another run.

Exactly two distinct selected runs are required. Empty selection presents
the form. Malformed IDs, duplicate query parameters, unsupported parameter
combinations, repeated run IDs, mismatched response identities, cross-match
runs, and nonterminal/unknown states cannot produce a comparison. An absent
left ID with a right ID is an invalid selection, not permission to swap sides.
No data/provenance goes in the URL, only match/run selection identifiers.

Candidate reads are paginated with an explicit load-more control and error
feedback. Do not describe fewer than two runs as the exhaustive match history
while continuation remains. Candidates show execution state and recognizable
run/recording-set/workflow context without preselecting the newest pair.
Selection never creates, reruns, or cancels work.

## Roles and Disclosure

Require a signed-in active member. Club Admins and Coaches/Viewers with read
access to the match's team can compare. Registrar or unrelated-team membership
alone grants no access. Navigation controls are not the authority: every
completed load depends on current authorized API responses.

A concealed 404 uses existing not-found handling without confirming an
unreadable run. A disclosed 403 uses existing not-permitted handling.
Either removes both sides' detail and reusable comparison state. Session end
uses existing middleware/session handling. A refused pair never leaves the
other side's successful results visible.

## Ready Presentation

Use explicit left/right labelled run sections and stream groups that keep
side attribution at narrow widths and without color. Show:

- Exact run ID, terminal execution state, current flag, and authoritative
  superseding run/recording-set metadata when reported. False current status
  alone is not labelled superseded.
- Recording-set ID and finalization context, workflow ID/version, and a link
  to this side's review.
- Every stream's node identity, capability ID/version, schema, output type,
  scope, fact count, five separate output coverage counts, provenance, and
  reported node input coverage or an explicit not-reported explanation.
- Recording-set/workflow differences and a permanent explanation that these
  reads do not establish equal full input snapshots or semantic equivalence.
- Explicit contract/provenance/input-coverage field differences on uniquely
  aligned streams. An aligned row is a navigation aid, not an equivalence
  claim. Unknown metadata values stay raw with an explanation where unsupported.

Use the alignment and availability rules in [data-model.md](../data-model.md).
Unmatched and ambiguous streams retain every item and explain why they have
no counterpart. No synthetic zero side. Equal values describe reported
metadata only; no "better", "improved", "more accurate", or quality ordering.
No fact payload browser, charts, export, media, sharing, or run control.

## Loading, Refresh, and Failure

Entry, selection change, manual refresh/retry, route/history return, visible
window return, and reconnect invalidate the completed display before loading.
Do not use stale data or a `/alive` success to satisfy authorization.
All required reads must succeed for the active generation before ready
content appears; a partial load never looks like a completed comparison.
Refresh re-reads current/superseded labels without changing selected IDs.

Offline shows connection-required and no run/stream/result detail, even for a
previously successful pair. Reconnect performs the full authorized load.
Not found, disclosed forbidden, incompatible metadata, and recoverable
failures are distinct. A recoverable error has a labelled retry action;
failed reads are never rendered as empty streams or zero facts.

## Accessibility, Localization, and Storage

Extend `src/i18n/en/analysis.json`; reuse existing common/lineage wording
where appropriate. Use semantic headings, labels for both selectors and
detail links, announced loading/failure updates, visible focus, keyboard
selection/navigation, and fully inspectable long IDs/digests. Announcements
must not depend on color or duplicate the whole stream list on every update.
Automated axe WCAG 2.2 AA checks cover each rendered state, complemented by
keyboard scenarios.

Results, coverage, lineage, and provenance remain in memory only. Remove
comparison-specific cached data on refusal, offline, and unmount; session
end also uses the existing global clear. No local/session storage, IndexedDB,
Cache Storage, service-worker API responses, or persisted query state.
URL identifiers are selection only, not a share/export feature.
