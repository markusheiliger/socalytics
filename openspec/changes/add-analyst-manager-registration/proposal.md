# Add Analyst Manager Registration Proposal

## Why

The Analyst source area has no executable Manager, so a host cannot establish a device-bound stamp identity, restore registration safely, or exercise local operating controls independently of the future desktop UI. This change establishes the first testable Manager and registration slice before Analyst execution and packaging add broader runtime and operational risk.

## What Changes

- Add a .NET 10 Generic Host worker under `src/analysts/manager`, independent of Avalonia, with documented restore, build, test, and headless-run workflows.
- Add durable local registration and operating state behind protected-storage abstractions, with fail-closed behavior when no supported protected store exists and explicit restart restoration of stamp binding, Manager identity, key reference, and the last Running or Paused intent.
- Add an end-to-end browser pairing lifecycle: protected asymmetric device-key creation, short-lived single-use pairing codes, Registrar initiation, separate Club Admin approval or rejection, fresh device-key proof before activation, expiry, immutable minimized audit events, administrative revocation, and local unregister.
- Add machine authentication contracts using OAuth 2.0 client credentials with `private_key_jwt` and short-lived DPoP-bound API tokens, without exposing human credentials to the Manager or persisting registration-lifetime broker credentials.
- Add local pause, resume, drain, safe-exit, and unregister coordination that suppresses new work while preserving the distinction between registration state and operating state. No job dequeue or Analyst execution is added.
- Add a transport-neutral, dependency-injected OCI Runtime Adapter boundary and test adapter for runtime preflight and lifecycle coordination without shipping a production Docker, Podman, or containerd implementation.
- Add explicit Windows, macOS, and Linux protected-storage spikes covering key non-exportability, state protection, process-account access, destruction, restart/autostart behavior, unsupported-host failure, packaging/signing implications, and test automation. The spikes record evidence and recommendations but do not select production keystore implementations in this change.
- Add focused tests for registration and operating-state transitions, pairing and DPoP replay resistance, fresh key possession, Registrar/Admin authorization separation, approval and activation expiry, rejection, revocation, unregister/drain behavior, stamp binding, restart restoration, secret-safe diagnostics, and unsupported protected storage.
- Depend on `add-platform-persistence-foundation` for platform registration persistence and integration-test infrastructure, and on `add-club-identity-foundation` for authenticated human sessions plus Registrar and Club Admin authorization. Those changes must be applied first or equivalent accepted behavior must exist; their artifacts are not modified here.
- Integrate only with the contracts owned by `add-durable-analysis-workflow`: this change does not consume jobs, claim attempts, issue broker credentials, or implement heartbeat/completion behavior, and it does not modify that change's artifacts.
- Keep Avalonia production UI, Analyst containers, model execution, production OCI adapters, installers, autostart packaging, updates, QR rendering, and unresolved production policy values out of scope.
- Preserve protected-storage technologies, pairing and activation lifetimes, token lifetimes, rate limits, drain timeout, audit authority and retention, production OAuth issuer details, and broker-credential mechanisms as unresolved decisions unless already fixed by authoritative architecture. Approximate architecture values are not promoted to production defaults.

## Capabilities

### New Capabilities

- `analyst-manager-registration`: Defines browser pairing, authorization separation, device-key proof, durable stamp binding, machine authentication, expiry, audit, revocation, unregister, and restart restoration behavior.
- `analyst-manager-host`: Defines the independent .NET worker lifecycle, protected local state boundary, operating-state controls, fail-closed startup, and testable OCI Runtime Adapter boundary.

### Modified Capabilities

None.

## Impact

- Affected product areas: a new `src/analysts/manager` .NET 10 solution and worker/test projects; platform IdentityAccess registration APIs, persistence, authorization, token issuance and validation; API composition and OpenAPI; and focused platform integration and architecture tests.
- API and security impact: add TLS-only pairing, approval, activation, status, revocation, unregister, and OAuth token flows; enforce `private_key_jwt`, DPoP request binding and replay protection, authoritative active-registration checks, stamp-scoped machine scopes, indistinguishable pairing failures, and secret-free logs/audit evidence.
- Dependency impact: platform persistence and human authorization come from the two active foundation changes. Cryptographic, OAuth, DPoP, protected-storage, and durable-state libraries may be selected during implementation only when they satisfy the approved contracts and central package-management conventions.
- Documentation impact: synchronize `docs/architecture/analyst-manager.md`, `docs/architecture/analyst-runtime-and-recovery.md`, `docs/architecture/security-and-data-governance.md`, root and analysts development guidance, and component-local executable contracts with implemented evidence while preserving provisional production decisions.
- Governance impact: supplies development evidence for BND-002, CTL-003, CTL-009, DAT-001, DAT-002, DAT-009, POL-001, POL-002, and THR-003. The production audit authority, retention, credential lifetimes, keystore choices, installer trust, and accountable approvals remain unresolved and production-blocking.
- UX impact is limited to browser-facing verification and approval contracts plus headless status/control behavior. Production Avalonia interaction and accessibility work remain unaffected and deferred.
