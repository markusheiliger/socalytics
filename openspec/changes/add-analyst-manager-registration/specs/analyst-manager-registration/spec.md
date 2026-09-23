# Analyst Manager Registration Specification

## Purpose

Defines the device-bound registration, browser approval, machine authentication, durable stamp binding, audit, revocation, and unregister behavior required before an Analyst Manager can receive operational authority.

## ADDED Requirements

### Requirement: Pairing starts with protected device-key possession

An unregistered Manager SHALL create or reference a non-exportable asymmetric device key in supported operating-system-protected storage before requesting a pairing code. The request SHALL bind the public key, target stamp, a fresh nonce, an expiry, and minimized device metadata. The Manager SHALL refuse pairing when protected key storage is unavailable or unsupported and SHALL NOT fall back to an exportable key file.

#### Scenario: Protected storage is supported

- **WHEN** an unregistered Manager begins pairing on a host with a supported protected store
- **THEN** it creates or references a non-exportable device key and requests a key-bound pairing code
- **THEN** neither the private key nor a human credential is exposed to the Manager workflow

#### Scenario: Protected storage is unsupported

- **WHEN** an unregistered Manager begins pairing without a supported protected store
- **THEN** pairing is refused with an actionable unsupported-storage result
- **THEN** no pairing code, exportable private-key fallback, or partially active registration is created

### Requirement: Pairing codes are short-lived, single-use, and opaque

The platform SHALL issue high-entropy pairing codes and polling handles that are narrowly scoped, rate-limited, short-lived, and single-use. Responses to invalid, expired, consumed, or unknown pairing material SHALL NOT reveal whether a registration request exists.

#### Scenario: Pairing code is consumed once

- **WHEN** a Registrar submits a valid unexpired pairing code for the target stamp and club
- **THEN** the platform creates one key-bound pending approval request
- **THEN** any replay of the code is rejected without revealing the existing request

#### Scenario: Pairing code expires

- **WHEN** a pairing code is submitted after its configured expiry
- **THEN** no pending approval request is created
- **THEN** retry requires a newly generated code, nonce, and audit correlation

### Requirement: Human authorization uses separate initiation and decision operations

The platform SHALL require an authenticated user with Registrar capability to initiate a pending request and a separately authorized Club Admin operation to approve or reject it. A Club Admin MAY perform both operations, but each operation SHALL be independently authorized and audited. Human credentials and human session tokens SHALL remain in the browser and platform boundary and SHALL NOT pass through the Manager.

#### Scenario: Registrar initiates and Club Admin approves

- **WHEN** a current Registrar submits a valid pairing code and a current Club Admin later approves the resulting request
- **THEN** the request advances from pairing to pending approval to approved
- **THEN** initiation and approval produce distinct immutable audit events with their respective actors

#### Scenario: Registrar attempts approval

- **WHEN** a Registrar without Club Admin authority attempts to approve or reject a pending request
- **THEN** the decision is forbidden and the request remains pending
- **THEN** the denied authorization is audited without recording pairing secrets

#### Scenario: Club Admin initiates and approves the same request

- **WHEN** one current Club Admin performs both authorized operations
- **THEN** the platform permits both operations
- **THEN** it preserves separate authorization decisions, timestamps, and audit events

### Requirement: Activation requires a fresh key-possession proof

Approval SHALL NOT grant operational access. The platform SHALL activate an approved, unexpired request only after the waiting Manager signs a fresh, request-bound platform challenge with the paired private key. Challenges SHALL be short-lived and single-use, and failed, stale, mismatched, or replayed proofs SHALL NOT activate a registration.

#### Scenario: Approved Manager proves key possession

- **WHEN** an approved Manager signs a fresh challenge with the paired private key before activation expiry
- **THEN** the platform assigns a stable Manager identity, binds it to the target stamp and public-key thumbprint, and marks the registration Active
- **THEN** operational machine-token issuance becomes eligible

#### Scenario: Approval alone is polled

- **WHEN** a Manager observes that its request is Approved but has not completed a valid fresh proof
- **THEN** the platform issues no operational access token or broker credential

#### Scenario: Activation proof is replayed or uses another key

- **WHEN** an activation proof reuses a consumed challenge or is signed by a key other than the paired key
- **THEN** activation is rejected and the registration does not become Active

### Requirement: Terminal and expiry states cannot be activated

Pending approval and approved requests SHALL expire according to configured windows. Rejected, expired, revoked, and unregistered requests or registrations SHALL NOT be activated or restored to Active; retry SHALL create a new request and audit trail.

#### Scenario: Club Admin rejects a request

- **WHEN** a current Club Admin rejects a pending request with a reason
- **THEN** the request becomes Rejected and cannot be approved or activated later

#### Scenario: Approval window or activation window expires

- **WHEN** the configured pending-approval or approved-activation window elapses
- **THEN** the request becomes Expired and later approval or activation is rejected

### Requirement: Machine authentication is proof-bound and stamp-scoped

An Active Manager SHALL authenticate token requests using OAuth 2.0 client credentials with `private_key_jwt`. Issuance SHALL validate the registered key, assertion audience, short expiry, and unique identifier and SHALL issue only short-lived API tokens bound to a DPoP key and the registered stamp and Manager subject. Each protected API request SHALL validate token audience and scope, DPoP method and URI binding, proof freshness and unique identifier, token-to-proof binding, stamp binding, and authoritative Active registration state.

#### Scenario: Active Manager requests and uses a token

- **WHEN** an Active Manager presents a valid private-key assertion and then a valid DPoP proof for an allowed machine API operation
- **THEN** the platform issues and accepts only the short-lived stamp- and subject-scoped authority required by that operation
- **THEN** the token cannot authorize a human or administrative API

#### Scenario: Assertion or DPoP proof is replayed

- **WHEN** a previously accepted assertion identifier or DPoP proof identifier is presented again within the replay window
- **THEN** the platform rejects the replay without extending registration or token authority

#### Scenario: Stamp binding is mismatched

- **WHEN** a token, proof, local configuration, or request payload targets a stamp other than the registered stamp
- **THEN** authorization fails and no cross-stamp operation occurs

### Requirement: Durable registration restores without durable bearer secrets

The Manager SHALL durably protect only the Manager identity, endpoint, stamp binding, device-key reference, and required non-secret registration metadata. On restart it SHALL restore those values, re-establish authority using fresh proof-bound credentials, and verify authoritative registration status before any operational action. It SHALL NOT persist human credentials, private-key material outside the protected key provider, access tokens, assertions, DPoP proofs, pairing secrets, or registration-lifetime broker secrets.

#### Scenario: Active Manager restarts

- **WHEN** a Manager with protected Active registration state restarts
- **THEN** it restores the same Manager identity and stamp binding
- **THEN** it obtains fresh short-lived credentials only after proving key possession and confirming Active status

#### Scenario: Protected state is missing or unreadable

- **WHEN** the durable state or referenced protected key cannot be read after restart
- **THEN** the Manager does not recreate, recover, or silently rebind the old identity
- **THEN** operational authentication remains disabled until a fresh registration is completed

### Requirement: Revocation and unregister remove authority

A Club Admin SHALL be able to revoke an Active registration immediately. Server authorization SHALL fail closed from the authoritative revocation regardless of Manager connectivity. A local unregister SHALL first pause new work, enter draining, and request revocation after the drain policy completes; it SHALL then remove stamp credentials and cached stamp data and return to Unregistered. Rebinding SHALL require revocation or unregister followed by fresh pairing.

#### Scenario: Club Admin revokes a Manager

- **WHEN** a current Club Admin revokes an Active Manager
- **THEN** API authorization and credential renewal are rejected immediately
- **THEN** the Manager clears local stamp authority when it observes revocation and cannot restore the revoked identity after restart

#### Scenario: Local unregister completes

- **WHEN** a local unregister drain reaches its completion or configured timeout outcome
- **THEN** the platform registration is revoked, protected stamp state is removed, and the Manager becomes Unregistered
- **THEN** registering with the same or another stamp requires a new request and approval lifecycle

### Requirement: Registration security events are immutable and minimized

The platform SHALL append immutable audit events for pairing creation and consumption, initiation, authorization denial, approval, rejection, expiry, activation proof outcome, activation, token lifecycle decisions, revocation, and unregister. Events SHALL identify the Manager or request, stamp, public-key thumbprint, state transition, actor or workload, timestamps, reason where applicable, and correlation identifier without storing credentials or secret proof material.

#### Scenario: Registration lifecycle is reviewed

- **WHEN** an authorized reviewer inspects the audit history for a registration
- **THEN** every security-relevant transition and authorization actor is attributable and ordered
- **THEN** tokens, assertions, DPoP proofs, private keys, pairing secrets, polling handles, and broker credentials are absent
