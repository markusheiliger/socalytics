# Research: Web UI Analyst Manager Fleet and Registration Approval

All decisions build on Web UI Foundation
([plan](../20261012-150657-web-ui-foundation/plan.md),
[research](../20261012-150657-web-ui-foundation/research.md)): Kiota client
behind adapters, in-memory session with `X-CSRF-Token`, TanStack Query, the
`simulated` and `platform` Playwright projects, the committed OpenAPI snapshot,
and the AppHost `e2e` mode. The registration operations come from
`specs/archive/20261005-130704-analyst-manager-registration`; the status and
connectivity fields from `specs/20261012-153308-manager-fleet-status`.

## R1. Verification address in local development

- **Finding**: The platform builds the verification address as
  `Stamp:PublicBaseUri` + `Registry:Pairing:VerificationPath`
  (`/analyst-managers/pair`). `Stamp:PublicBaseUri` is also the base of every
  machine-facing URI (token, activation, self, DPoP `htu`), and the Analyst
  Manager refuses non-HTTPS platform addresses. In the AppHost it is the API's
  HTTPS endpoint, while the Web UI is served on its own (HTTP) origin.
- **Decision**:
  - Add an optional `Registry:Pairing:VerificationBaseUri`, an absolute
    origin that is HTTPS outside Development, validated at start.
  - `StampOptions.VerificationUri` uses it when set and otherwise
    `Stamp:PublicBaseUri`, as before.
  - The AppHost sets it to the Web UI endpoint in the default and `e2e` modes.
- **Rationale**: Managers keep their HTTPS machine endpoints, and only the
  human-facing address moves to the Web UI. In a production stamp served from
  one origin, the option stays unset (FR-017).
- **Alternatives considered**:
  - Setting `Stamp:PublicBaseUri` to the Web UI origin breaks real Managers,
    which require HTTPS, and moves the DPoP `htu` base.
  - Serving the Web UI over HTTPS in development needs certificates for the
    Vite server, a change to the foundation's composition.
  - A redirect at `/analyst-managers/pair` on the API origin adds a page route
    to the API.

## R2. Typed OpenAPI contract for the registration operations

- **Finding**: The six registration operations (`submitAnalystManagerPairingCode`,
  `listAnalystManagerRegistrations`, `getAnalystManagerRegistration`,
  `approveAnalystManagerRegistration`, `rejectAnalystManagerRegistration`,
  `revokeAnalystManagerRegistration`) read their bodies by hand and return
  anonymous objects. The OpenAPI document therefore has no request or response
  schemas for them, and the Kiota client cannot type them.
- **Decision**:
  - Introduce internal DTO records with exactly the existing JSON shape:
    `AnalystManagerRegistrationDto` and its nested records, the page, and the
    request bodies `PairingCodeSubmissionBody` and `ReasonRequest` (already
    records). The DTO also carries the `status` and `connectivity` fields added
    by the fleet read API.
  - Declare `.Accepts<>`, `.Produces<>`, and `.ProducesProblem` (`400`, `401`,
    `403`, `404`, `409`, `429`) on each operation.
  - Regenerate the committed OpenAPI snapshot.
  - Existing registration tests keep passing unchanged. A new Host test
    compares one response per operation with the previous property names.
- **Rationale**: This is the minimal platform change that makes the existing
  operations consumable through the generated client (FR-016). No behavior
  changes.

## R3. Fleet filters and the pending count

- **Decision**:
  - **"Current" (default) and "ended"**: the Web UI reads every page of the
    unfiltered list (`pageSize` at its maximum) and filters on the client.
    Clubs have a small number of Managers.
  - **Ordering**: pending requests first, then by host label (case-insensitive),
    then by `createdAt`.
  - **A single state**: uses the API's `state` filter.
  - **"All"**: the unfiltered list.
  - **Pending count**: reads `state=pending_approval` and counts the items
    across pages.
- **Rationale**: This uses only existing filters; a multi-state filter would be a
  new platform behavior.
- **State names on the wire**: `pairing`, `pending_approval`, `approved`,
  `active`, `unregistering`, `unregistered`, `rejected`, `expired`, `revoked`.

## R4. Refresh

- **Decision**: TanStack Query `refetchInterval`.
  - The fleet list and the registration detail refresh every 30 s, only while
    visible.
  - The navigation's pending count refreshes every 60 s while a Club Admin is
    signed in, through a query shared by the navigation.
  - Decisions invalidate the list, the detail, and the count at once.

## R5. Fingerprint confirmation for approval

- **Decision**:
  - Normalize both the request's `deviceFingerprint` (for example `THNN-PKJR`,
    8 characters) and the entered text by removing everything except letters
    and digits and upper-casing.
  - Approval is enabled when the entered text equals the last 6 characters of
    the normalized fingerprint.
  - The comparison happens only in the Web UI. It is a deliberate human check,
    not an authorization rule, and the platform's decision rules stay
    authoritative.
- **Revocation**: the same normalization compares the typed host label (exact,
  trimmed, case-sensitive) or, without a host label, the last 6 fingerprint
  characters (FR-012).

## R6. Returning to the pairing page after sign-in

- **Decision**: The sign-in route accepts a `returnTo` path. A guarded route
  passes its own path. After sign-in, the Web UI navigates there only when the
  path starts with a single `/` and matches a known route; otherwise it goes
  home.
- **Rationale**: It prevents open redirects. The pairing code and fingerprint
  are never part of the address (FR-005).

## R7. Refusals and unknown values

- **Decision**: The pairing page maps `400 analyst-manager-pairing-code-invalid`
  and any `404` to one generic message (FR-004). It maps
  `429 analyst-manager-rate-limited` to "too many attempts", with the wait taken
  from `Retry-After`.
  - **Decision conflicts**: `409 invalid-state-transition` and
    `409 analyst-manager-registration-expired` show the refusal and reload the
    registration (FR-009, FR-012).
  - **Required reasons**: the `reason` field (1–500 characters) is checked in
    the form and shown from the platform's field error.
  - **Unknown values**: unknown registration states, operating states, key
    protections, and connectivity values map to `unknown` in the Web UI's
    models (FR-014).

## R8. Test strategy

- **Unit tests (Vitest)**: the adapter and its mapping, fingerprint
  normalization, filters and ordering, the refresh configuration, the
  `returnTo` validation, and the unknown-value mapping.
- **`simulated` project**: every acceptance scenario against routed fixtures
  typed from the generated models. Includes axe checks, CSP, and the check that
  no code or fingerprint is in storage or addresses (SC-007).
- **`platform` project**: a **scripted Manager**,
  `tests/e2e/support/scriptedManager.ts`, written in TypeScript with Node's
  `crypto` and an ES256 P-256 key in memory. It starts a pairing with
  `claimedKeyProtection: software`, which the AppHost allows with
  `Registry__AllowSoftwareKeys=true`.
  - **Routing**: it sends every request to the Web UI origin, which forwards
    `/api`. The proof claims (`aud`, `htu`) use the URIs from the platform's
    stamp discovery, because the platform checks them against
    `Stamp:PublicBaseUri` and not against the request's host. The test then
    needs no trust in the API's development certificate.
  - **Flow**: the test enters the code and fingerprint on the pairing page as a
    Registrar, then approves as the Club Admin through the fingerprint
    confirmation.
  - **Manager side**: the scripted Manager then polls, activates with a signed
    challenge, obtains a DPoP-bound token, and sends one status report through
    the self registration check.
  - **Assertions**: the test sees the Manager active and online with its
    operating state in the fleet, then revokes it.
  - **Proof format**: unit tests check the scripted Manager's proofs against
    the golden fixtures in `contracts/analyst-manager/registration/v1/`.
- **Alternatives considered**:
  - Running the real Analyst Manager CLI: it needs HTTPS, a PKCS#11 token on
    Linux, and a long-running process.
  - A test-only platform endpoint: it would be a security risk.
