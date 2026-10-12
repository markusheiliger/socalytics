# Contract: Registry API

All operations are under `/api/v1/registry`. They use `MapMemberApi()` (an
active member session) and `SessionAntiforgeryFilter` on state changes, and are
authorized for Club Admins (`ClubPermission.Administer`). Problems are
`urn:socalytics:problem:<code>`.

| Operation | Request | Success | Refusals |
| --- | --- | --- | --- |
| `POST /analyst-images` | `{ "reference": "ghcr.io/<repo>@sha256:<hex>" }` | `201` with the declaration; `200` when already registered | The codes in [analyst-manifest-and-packaging.md](analyst-manifest-and-packaging.md) (`400`, `404`, `422`, `502`/`503`) |
| `GET /analyst-images` | Query `capabilityId?` | `200` list (digest, capability, registered at) | – |
| `GET /analyst-images/{digest}` | – | `200` declaration | `404` |
| `POST /analyst-profiles` | `{ profileId, imageDigest, content }` (version assigned as the next for `profileId`) | `201` draft with `profileDigest`; `200` when the same content already exists as a draft or approved version | `422` `profile-contradicts-declaration` (with details), `404` `image-not-registered` |
| `GET /analyst-profiles` | Query `state?`, `capabilityId?` | `200` list | – |
| `GET /analyst-profiles/{profileId}/versions/{version}` | – | `200` with `ETag` | `404` |
| `POST /analyst-profiles/{profileId}/versions/{version}/approval` | `If-Match` | `200` approved | `409` `profile-not-draft`, `412` stale, `428` |
| `POST /analyst-profiles/{profileId}/versions/{version}/retirement` | `If-Match` | `200` retired | `409` `profile-in-default-workflow`, `409` `profile-already-retired`, `412`, `428` |
| `POST /workflows` | `{ workflowId, workflowVersion, requestedCapabilities, completionPolicy, segmentationPolicy, nodes, edges }` | `201` with `definitionDigest` | `422` `workflow-invalid` (`profile-not-approved`, `missing-upstream`, `schema-incompatible`, `cycle`, `unknown-completion-policy`, `invalid-segmentation-policy`), `409` `workflow-version-exists` |
| `GET /workflows` | – | `200` list | – |
| `GET /workflows/{workflowId}/versions/{version}` | – | `200` | `404` |
| `GET /default-workflow` | – | `200` with `ETag`; `404` when none | – |
| `PUT /default-workflow` | `{ workflowId, workflowVersion }` plus `If-Match` (or none when no default exists) | `200` | `422` `workflow-not-resolvable`, `412`, `428` |

Every state-changing operation, including refused registrations, records the
audit events in research.md § R8. The operations never return registry
credentials.
