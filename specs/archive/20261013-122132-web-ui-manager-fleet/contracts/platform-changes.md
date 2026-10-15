# Contract: Platform Changes

Two minimal platform changes; no behavior of the registration operations
changes.

## Verification base address (research.md § R1)

| Setting | Type | Default | Validation |
| --- | --- | --- | --- |
| `Registry:Pairing:VerificationBaseUri` | Absolute origin (no path, query, or fragment) | unset → `Stamp:PublicBaseUri` | `https` outside Development; `http` or `https` in Development |

- `StampOptions.VerificationUri(RegistryOptions)` builds
  `VerificationBaseUri ?? PublicBaseUri` + `VerificationPath`.
- The pairing response's `verificationUri` reflects it; every machine-facing URI
  (token, activation, self) and the DPoP `htu` base stay on
  `Stamp:PublicBaseUri`.
- AppHost (`src/platform/SocAlytics.Platform.AppHost/Program.cs`):
  `Registry__Pairing__VerificationBaseUri` = the Web UI endpoint's origin, in the
  default and the `e2e` mode.
- Tests: options validation (invalid values refused at start), the pairing
  response uses the base when set and `PublicBaseUri` otherwise, and the Host
  test asserts the AppHost setting.

## Typed registration operations (research.md § R2)

| Operation id | Request body | Responses |
| --- | --- | --- |
| `submitAnalystManagerPairingCode` | `PairingCodeSubmissionBody` (`pairingCode`, `deviceFingerprint`) | `201 AnalystManagerRegistrationDto`; `400`, `401`, `403`, `429` problems |
| `listAnalystManagerRegistrations` | – (query `state`, `continuationToken`) | `200 AnalystManagerRegistrationPageDto` (`items`, `continuationToken`); `400`, `401`, `403` |
| `getAnalystManagerRegistration` | – | `200 AnalystManagerRegistrationDto`; `401`, `403`, `404` |
| `approveAnalystManagerRegistration` | – | `200 AnalystManagerRegistrationDto`; `401`, `403`, `404`, `409` |
| `rejectAnalystManagerRegistration` | `ReasonRequest` (`reason`) | `200 AnalystManagerRegistrationDto`; `400`, `401`, `403`, `404`, `409` |
| `revokeAnalystManagerRegistration` | `ReasonRequest` (`reason`) | `200 AnalystManagerRegistrationDto`; `400`, `401`, `403`, `404`, `409` |

- `AnalystManagerRegistrationDto` has exactly the properties the anonymous object
  had (including the `status` and `connectivity` fields of the fleet read API),
  with the same names, nullability, and serialization; the existing registration
  tests pass unchanged.
- The status codes listed are those the operations already return; the
  implementation confirms them from the handlers and the existing tests before
  declaring them.
- The committed OpenAPI snapshot
  (`src/platform/SocAlytics.Platform.Api/openapi/v1.json`) is regenerated in the
  same change.
