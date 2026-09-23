# Analyst Manager Host Specification

## Purpose

Defines the first headless Analyst Manager host, its durable local operating controls, protected-storage failure behavior, and the runtime-neutral adapter contract that keeps coordination independent of a future Avalonia UI.

## ADDED Requirements

### Requirement: Manager lifecycle is executable without a production UI

The repository SHALL provide a documented .NET 10 restore, build, test, and headless-run workflow for the Analyst Manager. Its registration lifecycle, status, and controls SHALL operate without Avalonia or another production desktop UI.

#### Scenario: Contributor validates the Manager

- **WHEN** a contributor follows the documented Manager workflow from a clean checkout with the supported .NET 10 SDK
- **THEN** dependencies restore, the Manager builds, and its automated tests pass
- **THEN** the headless host starts without requiring a production UI, OCI runtime, Analyst image, or model

### Requirement: Registration state and operating state remain distinct

The Manager SHALL model registration authority independently from local operating state. An Active registration MAY be Running, Paused, RuntimeUnavailable, or Draining; changing local operating state SHALL NOT activate, revoke, or rebind registration.

#### Scenario: Active Manager is paused and resumed

- **WHEN** a local operator pauses an Active Running Manager
- **THEN** new work admission is suppressed immediately while registration status coordination remains enabled
- **WHEN** the operator resumes and runtime preflight is valid
- **THEN** the Manager returns to Running without changing its registration identity

#### Scenario: Runtime preflight is unavailable

- **WHEN** the configured runtime adapter reports an unavailable or unsupported profile
- **THEN** an Active Manager enters RuntimeUnavailable, advertises no unavailable capability, and admits no work
- **THEN** registration status and revocation observation remain enabled

### Requirement: Pause intent survives restart

The Manager SHALL durably protect the last Running or Paused intent separately from transient process state. Startup SHALL restore a deliberate pause and SHALL NOT admit work until registration status and runtime preflight have been revalidated.

#### Scenario: Paused Manager restarts

- **WHEN** a Manager is deliberately Paused and the process restarts
- **THEN** it restores Paused rather than automatically entering Running
- **THEN** no new work is admitted before an explicit resume

#### Scenario: Running Manager restarts

- **WHEN** a Manager previously intended to run and restarts
- **THEN** it remains non-admitting until protected state, Active registration, and runtime preflight are valid
- **THEN** it enters Running only after those checks succeed

### Requirement: Drain coordinates safe exit and local unregister

Drain SHALL suppress new work immediately and allow active work to complete until a configured timeout. Safe exit SHALL preserve registration and the last Running or Paused intent; local unregister SHALL revoke registration and clear stamp state after the drain outcome. Timeout policy SHALL expose a deterministic cancel-or-cleanup outcome rather than wait indefinitely.

#### Scenario: Safe exit drains successfully

- **WHEN** safe exit is requested while work is active and all active work finishes before timeout
- **THEN** the host exits without revoking registration
- **THEN** the prior Running or Paused intent is available for the next start

#### Scenario: Drain timeout is reached

- **WHEN** active work remains at the configured drain timeout
- **THEN** the configured timeout policy yields either cancellation of the operation or cleanup of remaining Manager-owned work
- **THEN** the host does not remain indefinitely blocked in Draining

### Requirement: Protected local state fails closed

Registration and operating state SHALL be accessed only through a protected-storage boundary that reports supported, unavailable, corrupt, and inaccessible outcomes explicitly. Unsupported or inaccessible protection SHALL keep the Manager non-admitting and SHALL NOT be bypassed with plaintext or exportable-file storage.

#### Scenario: Protected state becomes inaccessible

- **WHEN** startup cannot access the protected local state using the process identity
- **THEN** the Manager reports a bounded diagnostic state and admits no work
- **THEN** logs and status do not expose protected values

### Requirement: OCI runtime operations use a testable neutral boundary

The Manager SHALL depend on a transport-neutral OCI Runtime Adapter contract selected through configuration and dependency injection. The contract SHALL support runtime probe, immutable-digest pull, constrained create/start/inspect/stop/remove operations, bounded logs and exit status, and reconciliation of Manager-owned resources. The first increment SHALL provide deterministic test behavior without requiring or claiming support for a production runtime adapter.

#### Scenario: Host uses a configured test adapter

- **WHEN** automated tests configure the deterministic adapter
- **THEN** they can drive healthy, unavailable, failure, cancellation, and reconciliation outcomes without a local OCI engine
- **THEN** the host responds through the same runtime-neutral contract used by future built-in adapters

#### Scenario: No supported production adapter is configured

- **WHEN** the headless host starts without a supported production runtime profile
- **THEN** it remains RuntimeUnavailable and does not admit work
- **THEN** it does not discover or load an arbitrary external plugin

### Requirement: Protected-storage support requires platform evidence

A protected-storage provider SHALL NOT be declared supported until a platform spike records evidence for its target operating system. Windows, macOS, and Linux evidence SHALL each cover non-exportable key behavior, protected state confidentiality, process-account access, key and state destruction, restart and autostart access, unsupported-host failure, packaging and signing implications, and repeatable automated-test strategy.

#### Scenario: Platform spike satisfies every criterion

- **WHEN** a Windows, macOS, or Linux candidate has evidence for every required criterion and no unresolved security blocker
- **THEN** the spike may recommend a provider for a later approval and implementation change
- **THEN** the evidence does not by itself mark the provider production-approved

#### Scenario: Platform spike cannot prove required protection

- **WHEN** a platform candidate permits unintended key export, cannot restrict process access, cannot destroy protected material, or lacks a repeatable validation path
- **THEN** the candidate is recorded as unsupported or blocked
- **THEN** the Manager continues to fail closed on that platform
