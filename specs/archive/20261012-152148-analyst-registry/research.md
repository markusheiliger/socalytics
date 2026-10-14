# Research: Analyst Registry

## R1. Reading the manifest from an OCI image

- **Decision**: A minimal OCI Distribution client (`HttpClient`, GET only):
  1. Resolve the manifest by digest, accepting the OCI image manifest and the
     Docker v2 manifest. For an OCI index, pick the `linux/amd64` entry and
     record the index digest as the registered digest.
  2. Fetch the configuration blob and read the `io.socalytics.*` labels.
  3. Fetch only the **last layer**, which by packaging convention contains only
     `analyst-manifest.json` (`contracts/analyst-manifest-and-packaging.md`).
     Extract it with `System.Formats.Tar` (gzip or uncompressed; other media
     types are refused).

  Every fetched object's SHA-256 must equal its referenced digest.
- **Rationale**: This meets FR-002 and SC-004 without downloading model
  layers. The convention costs Analyst authors one `COPY` as the last
  Dockerfile instruction.
- **Alternatives considered**:
  - **Labels only**: labels cannot carry the full manifest reliably (size,
    escaping).
  - **Scanning all layers**: downloads model weights.
  - **An OCI referrer artifact**: the architecture says the manifest is
    copied into the image.

## R2. Labels

- **Decision**: Required labels:
  - `io.socalytics.analyst-manifest.version` (contract version);
  - `io.socalytics.analyst-manifest.sha256` (digest of the file);
  - `io.socalytics.capability.id` and `io.socalytics.capability.version`;
  - `io.socalytics.model.digest`.

  The import refuses when any label is missing or differs from the file
  (FR-003).
- **Rationale**: Labels make the essentials visible to standard tooling and
  bind the file to the image configuration.

## R3. Image registry access

- **Decision**: `Registry:Images` options:
  - `RegistryHost` (for example `ghcr.io`);
  - `RepositoryPrefix` (for example `markusheiliger/socalytics-analysts/`);
  - optional `Username` and `Token` as protected configuration.

  The client follows the `WWW-Authenticate: Bearer` challenge (anonymous or
  with basic credentials) to obtain a pull token. References outside the host
  and prefix are refused. HTTPS is required outside Development and Test, where
  the disposable test registry runs over HTTP.

## R4. Profile digest

- **Decision**: The profile digest is the SHA-256 of the RFC 8785 (JSON
  Canonicalization Scheme) serialization of the profile content: capability,
  image and model digests, requirements, video policy, and schema versions,
  excluding state and audit fields. It is implemented in-house (sorted keys,
  ECMAScript number formatting, no insignificant whitespace), with tests
  against the RFC's examples.
- **Rationale**: Equal content gives an equal digest, which supports idempotent
  creation and stable run snapshots.

## R5. Immutability

- **Decision**:
  - The declaration and workflow-definition tables have triggers that refuse
    `UPDATE` and `DELETE`.
  - The profile table's trigger refuses changes to content columns once
    `state <> 'draft'`, and refuses state transitions other than
    draft→approved, draft→retired, and approved→retired.
  - The default-workflow row is versioned (`attach_version_trigger`) and
    changed with `If-Match`.
- **Rationale**: Defense in depth for SC-002 beyond application checks.

## R6. Workflow validation

- **Decision**: The domain validates:
  - every node references an approved profile;
  - every declared required upstream capability and schema version of a node
    is satisfied by an edge from a node providing it;
  - conditional edges use the existing `ConditionalPredicate`;
  - no cycles (topological sort);
  - requested capabilities exist as nodes;
  - the completion policy is a known value;
  - the segmentation policy has a positive duration and a digest.

  The stored definition and its resolution produce exactly the existing
  `ResolvedWorkflowDefinition`.

## R7. Resolver replacement and seeding

- **Decision**:
  - `RegistryWorkflowDefinitionResolver` is registered as
    `IWorkflowDefinitionResolver`. It reads the default workflow in one read
    transaction, so a concurrent default change gives the old or the new one
    entirely. It returns `NotConfigured` when there is no default and
    `InvalidDefinition` when a referenced profile is retired or missing.
  - `ConfiguredWorkflowSeeder` (hosted, Development and Test only) seeds an
    empty Registry from `ConfiguredWorkflowOptions`, inserting declarations
    marked `source = seed` and approved profiles, without any image registry
    access.
  - Tests that wrap the resolver (`CompletionTests`, `WorkflowAvailabilityTests`)
    keep working through the same interface. Their wrappers resolve the
    registered implementation and are adjusted where they construct it
    directly.

## R8. Authorization and audit

- **Decision**:
  - All operations use `MapMemberApi()`, `SessionAntiforgeryFilter`, and
    `IAccessAuthorizer.AuthorizeClubAsync(ClubPermission.Administer)`.
  - Audit events: `registry.analyst-image.registered` and `…refused`,
    `registry.analyst-profile.created`, `…approved` (with `selfApproval`),
    `…retired`, `registry.workflow.defined`, and
    `registry.default-workflow.changed`. Each records the operator, digests,
    and outcome (FR-015, SC-005).

## R9. Testing the OCI path

- **Decision**: `OciRegistryContainerFixture` starts `registry:2`.
  `TestImagePublisher` builds a minimal image: a filler layer standing in for
  model weights, then the manifest layer as the last layer, a configuration
  with labels, and the manifest. It pushes them with the Distribution API
  (monolithic blob upload, manifest `PUT`). A counting HTTP handler proves that
  only the manifest, the configuration, and the last layer are fetched
  (SC-004).
