# Contract: Registry Routes and Roles

Extends the Web UI's route table (`src/clients/web/src/router.tsx`) and
navigation (`src/clients/web/src/shell/navItems.ts`). Every route below is
wrapped in the existing `clubAdminOnly` guard (`RequireRole allow={isClubAdmin}`):
members without the Club Admin role get the not-permitted view and no Registry
request is sent (FR-001). The platform stays authoritative: a `403` from any
Registry operation also shows the not-permitted view, a `404` on a record read
shows the not-found view (FR-002).

## Navigation

| Key | Target | Shown when | Position |
| --- | --- | --- | --- |
| `registry` | `/registry` | `isClubAdmin(member)` | after `analystManagers` |

The label comes from `shell.json` (`nav.registry`, English "Analyst Registry").

## Routes

| Path | View | Purpose | Spec |
| --- | --- | --- | --- |
| `/registry` | `RegistryOverviewPage` | Current default workflow (or "no default"), links to the three lists | US1, FR-003 |
| `/registry/images` | `ImagesPage` | Image list; search parameter `capabilityId` | FR-004 |
| `/registry/images/register` | `RegisterImagePage` | Register by digest reference | US2, FR-011, FR-012 |
| `/registry/images/:digest` | `ImagePage` | Declaration, manifest text, "New profile version" | FR-005 |
| `/registry/profiles` | `ProfilesPage` | Versions grouped by profile id; search parameters `state`, `capabilityId` | FR-006 |
| `/registry/profiles/new` | `ProfileCreatePage` | Profile form; search parameter `image` (digest) required, otherwise an image picker | US3, FR-013 |
| `/registry/profiles/:profileId/versions/:version` | `ProfileVersionPage` | Details, Approve, Retire | FR-007, FR-014, FR-017 |
| `/registry/workflows` | `WorkflowsPage` | Workflow versions with default marker | FR-008 |
| `/registry/workflows/new` | `WorkflowCreatePage` | Definition form; optional search parameters `fromId` and `fromVersion` | US4, FR-015 |
| `/registry/workflows/:workflowId/versions/:version` | `WorkflowVersionPage` | Definition with node and edge lists, "Make default", "New version from this" | FR-009, FR-016, FR-017 |

Path parameters are URL-encoded; `:version` of a profile is a positive integer,
otherwise the not-found view is shown without a request. Digests in addresses
are encoded with `encodeURIComponent`.

## Confirmations

| Action | Dialog | Must state |
| --- | --- | --- |
| Approve profile version | `ApproveProfileDialog` | profile id and version, image and model digests, that approved versions never change; self-approval note when `createdBy` is the signed-in member |
| Retire profile version | `RetireProfileDialog` | profile id and version, image and model digests, that retired versions cannot be used in new workflow versions while existing workflow versions and analysis runs keep them |
| Make workflow version the default | `SetDefaultWorkflowDialog` | the version, the current default (or "none"), that only analysis runs created afterwards use it and existing runs keep their snapshot; when no default exists, that new analysis runs become possible |

Cancelling a dialog sends nothing. Confirming sends exactly one request; the
dialog shows the outcome or the refusal and never repeats the request.
