# Analyst Manager Local-Control Contract

This is the host-local operating interface of the Analyst Manager
(FR-024). Message shapes are authoritative in
[local-control.schema.json](local-control.schema.json); this page defines the
transport, the command-line surface, and the behavioral guarantees. At
implementation the schema moves with its owner to the Manager component and is
validated by the Manager tests (component-local executable contract).

## Transport

- Unix domain socket `control.sock` in the Manager state directory
  (`Manager:StateDirectory`), on Windows, Linux, and macOS.
- The state directory is created with an owner-only DACL on Windows and mode
  `0700` elsewhere; the socket is `0600` where the OS applies modes. Only the
  Manager process account can connect. No network listener exists.
- One request per connection: the client writes one UTF-8 JSON line, the host
  writes one JSON line and closes. Requests larger than 16 KiB are refused with
  `invalid-request`.
- `protocolVersion` is `1`; other versions receive
  `unsupported-protocol-version`.

## Command-line surface

The executable is `socalytics-manager`. `run` hosts the Manager; every other
command is a client of a running host.

| Command | Request | Effect |
| --- | --- | --- |
| `socalytics-manager run` | none | Starts the Generic Host: restore, platform confirmation, preflight, local-control listener. `Ctrl+C` requests safe exit. |
| `socalytics-manager register --platform URL [--stamp-id ID] [--host-label TEXT] [--no-wait]` | `register` | Creates a fresh non-exportable device key, requests pairing, prints the verification address and pairing code, then (unless `--no-wait`) follows `status` until `active`, rejection, or expiry. Refused with `no-supported-key-store` when no supported protected store exists. |
| `socalytics-manager status [--json]` | `status` | Prints registration state, stamp, club, connectivity, operating state, uptime, latest preflight result, and diagnostics. |
| `socalytics-manager pause` | `pause` | Stops new work admission immediately; records intent `paused`. |
| `socalytics-manager resume` | `resume` | Returns to `running` only when Active and the latest preflight passed; otherwise records intent `running` and stays `runtime-unavailable`. |
| `socalytics-manager exit` | `exit` | Safe exit: drain, keep registration and last intent, stop the host. |
| `socalytics-manager unregister [--discard-local]` | `unregister` | Drain, start and complete platform unregistration, delete local stamp state and the device key, return to `unregistered`. `--discard-local` only in `restore-failed`. |

Exit codes: `0` success, `1` refused by the Manager (error code printed), `2`
usage error, `3` Manager not running or socket not accessible.

## Behavioral guarantees

- Commands other than `status` and `register` are refused with
  `invalid-registration-state` unless the registration is `active`
  (FR-023); `register` is refused unless the state is `unregistered`.
- `pause`, `resume`, and `exit` never create, activate, revoke, or rebind a
  registration.
- Drain obeys `Operating:DrainTimeout`, `Operating:DrainTimeoutPolicy`, and
  `Operating:CleanupBound`; `status` reports the drain deadline.
- No response, log entry, or diagnostic contains human credentials, access
  tokens, client assertions, DPoP or activation proofs, polling handles,
  challenges, broker credentials, or private key material. The pairing code
  appears only in the `register` result (FR-034).
- `restore-failed` diagnostics use bounded codes (`state-missing`,
  `state-unreadable`, `state-integrity`, `key-missing`, `key-inaccessible`,
  `key-mismatch`, `stamp-mismatch`) and never include protected values.
- Preflight check identifiers are listed in [data-model.md](../data-model.md#preflightresult).
