# Add Analyst Manager Registration Design

## Context

See `proposal.md` for motivation. `src/analysts` currently contains only an ownership README, while the executable .NET 10 solution is confined to `src/platform`. The authoritative Analyst Manager architecture defines a Generic Host worker, device-bound browser pairing, protected local state, independent registration and operating state machines, and an internal OCI adapter. The active persistence and identity changes establish prerequisites but explicitly do not implement Manager pairing; the durable analysis change owns future job, attempt, and completion contracts.

This increment crosses the Analyst Manager and platform IdentityAccess boundaries and handles DAT-001, DAT-002, and DAT-009. It therefore needs deterministic security tests and audit evidence while production keystore mechanisms, policy values, installers, and runtime adapters remain unresolved.

## Goals / Non-Goals

**Goals:**

- Establish a separately buildable and testable .NET 10 Manager solution under `src/analysts/manager`, using a Generic Host composition root with no UI dependency.
- Implement the complete pairing-to-revocation state model across Manager and platform, with server-authoritative registration state and protected Manager state.
- Make cryptographic time, challenge, replay, authorization, persistence, and transport behavior replaceable in tests without weakening production interfaces.
- Keep the OCI boundary sufficiently complete for preflight, future execution coordination, and reconciliation tests while shipping no production engine adapter.
- Produce platform spike evidence that can support later keystore decisions without treating a spike recommendation as approval.

**Non-Goals:**

- Selecting or shipping Windows, macOS, or Linux production protected-storage providers.
- Adding Avalonia, tray behavior, QR rendering, installers, autostart integration, signing, or updates.
- Pulling jobs, issuing NATS credentials, launching Analyst containers, running models, or integrating execution attempts.
- Choosing production pairing, token, replay-window, drain, retention, telemetry, or rate-limit values.

## Decisions

### 1. Use peer Manager projects with a headless Generic Host composition root

Create a Manager solution under `src/analysts/manager` with a worker entry point, an application/core boundary, platform-contract transport models, and focused unit/host/contract tests. Keep all UI concerns outside these projects. The root and analysts READMEs will document Manager-specific commands; the platform solution remains independently supported.

This preserves the approved first-level source areas and prevents a future Avalonia process from owning security or lifecycle logic. A single UI-coupled executable was rejected because it would make headless lifecycle and recovery tests dependent on desktop frameworks.

### 2. Make registration a server-authoritative aggregate and local state a protected projection

IdentityAccess owns pairing requests, registration records, challenge consumption, replay records, authorization decisions, and immutable registration audit events in its module schema. State transitions use optimistic concurrency and explicit transactions from `add-platform-persistence-foundation`. The Manager stores only endpoint, stamp, Manager identity, key reference, non-secret metadata, and last operating intent through a protected-state abstraction.

This separates authority from a potentially compromised host and allows immediate revocation without Manager cooperation. Treating local state as registration authority was rejected because it cannot enforce administrative revocation or stamp isolation.

### 3. Represent pairing and activation as explicit state machines

Platform states are Pairing, PendingApproval, Approved, Active, Rejected, Expired, Revoked, and Unregistered. Each command validates the current state, actor capability, expiry, correlation, and optimistic version. Pairing-code consumption and fresh activation-challenge consumption are atomic single-use transitions. A background expiry service and command-time checks use an injected clock so expiry remains correct if scheduled processing is delayed.

Approval and activation stay separate: approval records Club Admin intent, while activation verifies possession of the paired key. Issuing credentials at approval was rejected because browser authority alone would be sufficient to activate a device.

### 4. Keep browser and machine authentication on separate trust paths

The verification URL uses the human BFF session supplied by `add-club-identity-foundation`. The Manager receives only opaque pairing and polling material. Active machine clients use `private_key_jwt`; assertions have constrained audience and lifetime plus a persisted or bounded durable `jti` replay record. API access tokens carry Manager subject, stamp, machine scopes, and confirmation binding for DPoP. DPoP validation covers proof signature, `htm`, normalized `htu`, `iat`, `jti`, and token hash where required, with bounded replay storage.

Every protected Manager request rechecks authoritative Active state and stamp binding. Bearer-only machine tokens and long-lived client secrets were rejected because they weaken proof of possession and increase durable secret exposure.

### 5. Define secret-safe persistence and diagnostics at the boundary

Protected-state APIs accept typed records that cannot contain raw private keys, tokens, assertions, DPoP proofs, pairing codes, polling handles, or broker credentials. Structured logging and audit tests inspect captured fields for prohibited values. Platform audit writes occur in the same authoritative transaction as each state transition where feasible; denied operations append separate minimized security events through the audit boundary.

An encrypted general-purpose local file chosen by the application is not a supported fallback. Test doubles may hold keys in memory only inside test assemblies and must be impossible to select from production configuration.

### 6. Separate registration state from local operating coordination

The Manager uses a local coordinator for Running, Paused, RuntimeUnavailable, and Draining beneath Active registration. Startup restores only Running or Paused intent, then remains non-admitting until protected state, authoritative registration status, and runtime preflight pass. Pause suppresses admission but leaves status and revocation checks active. Drain uses cancellation and an injected timeout policy; safe exit preserves registration, while unregister requests server revocation and purges local stamp state.

Combining both state machines was rejected because pause or runtime failure must not mutate machine identity, and security revocation must override drain.

### 7. Introduce one runtime-neutral adapter contract and a deterministic test implementation

The adapter contract models probe, pull-by-digest, constrained container lifecycle, inspect, bounded logs, and labeled-resource reconciliation using runtime-neutral request and result types. Built-in implementations will be selected explicitly through configuration and dependency injection; directory scanning and arbitrary plugin loading are prohibited. This change supplies only a deterministic test adapter, so normal headless startup reports RuntimeUnavailable unless a supported adapter exists in a later change.

A Docker-specific interface was rejected because it would leak engine fields into Manager and platform contracts. A minimal probe-only interface was rejected because it would not test the boundary needed by later execution and recovery work.

### 8. Treat platform protected-storage work as evidence spikes

Create one evidence record each for Windows, macOS, and Linux. Every record identifies candidate OS facilities and versions, a repeatable harness, non-exportability results, state protection, process identity and ACL behavior, destruction, reboot/autostart access, unsupported configurations, packaging/signing implications, CI feasibility, residual risks, and a recommendation of supported, unsupported, or blocked. No provider enters production code in this change.

Likely facilities may be investigated, but names and results remain evidence rather than adopted architecture until a later decision. Selecting one mechanism now was rejected because the architecture explicitly requires cross-platform evidence and packaging context first.

### 9. Keep active-change contracts explicit

Implementation begins only after the persistence and club/identity prerequisites are applied or equivalent accepted behavior is available. IdentityAccess owns registration persistence and human authorization; the Manager consumes versioned HTTP/OAuth contracts and never reads platform schemas. The durable analysis change remains owner of jobs and attempts; this increment may use neutral fake active-work counts for drain tests but does not reference its transport contracts.

### 10. Synchronize architecture without prematurely promoting provisional decisions

Apply must update `docs/architecture/analyst-manager.md`, `docs/architecture/analyst-runtime-and-recovery.md`, and `docs/architecture/security-and-data-governance.md` to distinguish implemented development evidence from deferred production controls. Root and `src/analysts/README.md` must document executable scope and commands, and component-local guidance must describe supported contracts and test doubles.

Creating the first executable Manager and introducing proof-bound machine authentication are durable architecture changes. An ADR candidate should record the Manager/platform registration ownership and proof-bound credential model if implementation reveals a choice not already fully governed by the authoritative narratives. Keystore selection remains a later ADR candidate after spikes.

UX impact is limited to browser verification states and accessible semantic contracts; production desktop UX is unaffected. Client applications, Analyst capability implementations, model behavior, recording workflows, and deployment topology are unaffected.

## Risks / Trade-offs

- **[Risk] Active foundation changes are not yet applied** -> Gate implementation tasks on accepted equivalent persistence and identity behavior; do not duplicate their infrastructure or edit their artifacts.
- **[Risk] A complete pairing flow increases the first Manager increment's cross-module surface** -> Keep contracts narrow, versioned, and independently tested; split Manager host tests from IdentityAccess persistence and API tests.
- **[Risk] Replay stores can grow or lose effectiveness across restart** -> Persist bounded assertion, pairing, and activation consumption records with explicit expiry; define DPoP replay retention from configured token/proof windows and test restart behavior.
- **[Risk] URI normalization differences can reject valid DPoP proofs or accept mismatches** -> Define one canonical external-request URI calculation behind trusted forwarding configuration and cover method, URI, audience, and stamp mismatch tests.
- **[Risk] No production protected-store provider means real registration remains unavailable** -> Make unsupported status explicit, use test-only providers solely for automated evidence, and require a later approved provider change before production use.
- **[Risk] Audit atomicity for denied requests differs from successful transitions** -> Test successful state-event atomicity and durable best-effort denial evidence separately; document any audit-authority dependency as production-blocking.
- **[Risk] Generic Host controls could imply job execution support** -> Keep admission and drain coordination behind test work handles and report that no queue or Analyst execution exists.

## Migration Plan

1. Apply or verify equivalent accepted behavior for `add-platform-persistence-foundation` and `add-club-identity-foundation`.
2. Add platform registration schema and APIs in a disabled-by-default development configuration, then validate migrations, authorization, state transitions, audit, and cryptographic replay behavior.
3. Add the independent Manager solution, protected-state contracts, headless host, platform client, operating coordinator, and deterministic adapters.
4. Run contract and end-to-end tests with test-only protected key/state providers; keep production provider selection unavailable.
5. Produce all three platform spike evidence records and synchronize architecture and development guidance.
6. Enable the development registration surface only after independent verification and security audit tasks pass. Production promotion remains blocked on keystore, policy, audit-authority, deployment, and installer decisions.

Rollback disables the registration endpoints and Manager composition, revokes development registrations, removes test data through module-owned rollback guidance, and leaves existing platform capabilities unchanged. Schema rollback must preserve immutable audit evidence according to the applicable development policy rather than silently deleting it.

## Open Questions

- Exact production values for pairing, approval, activation, assertion, DPoP, token, replay, and drain windows remain configuration decisions governed before production promotion; tests use explicit non-production values.
- The production audit authority and retention mechanism remain unresolved; this increment defines and tests the event contract and module handoff without claiming production approval.
- The platform spikes will determine whether each operating system has a viable protected-store candidate and what later installer identity or entitlement work is required.
