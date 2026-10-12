# Analyst Manager Local Controls

The host operator controls the Manager from its **tray menu**, or from its
**status window** where the desktop has no tray area (FR-024). This page
defines those controls, the single-instance hand-off, and the optional
scripting CLI that uses the same local-control protocol. Message shapes are
authoritative in [local-control.schema.json](local-control.schema.json); at
implementation the schema moves to `src/analysts/manager/contracts/` and the
Manager tests validate every message against it.

## Tray menu and status window

| Item | Enabled when | Effect |
| --- | --- | --- |
| Status | always | Opens the status window: registration state, stamp, club, connectivity, operating state, uptime, latest preflight with failed checks and remediation, remaining pause time, diagnostics |
| Register… | registration `unregistered` | Platform endpoint input; creates a fresh non-exportable key (a software-backed key only if the stamp allows software keys), shows the verification address, the pairing code, and the device fingerprint with the instruction to give both to the Registrar in person, follows the pairing to `active`, rejection, or expiry |
| Pause | `active`, not draining | Stops new admission immediately; intent `paused` |
| Pause for ▸ 30 min, 1 h, 2 h, 4 h, 8 h, Custom… | `active`, not draining | Timed pause (FR-036); intent `paused-until` |
| Resume | intent `paused` or `paused-until` | Ends any pause; returns to `running` only when Active and the latest preflight passed |
| Safe exit | always | Drain, keep registration and intent, quit |
| Unregister… | `active` | Confirmation, then drain; only after a completed drain, platform unregistration and local state and key removal. A drain cancelled by the timeout policy cancels the unregister: the registration stays `active` and the previous intent returns |
| Discard local registration… | `restore-failed` | Deletes unusable remnants; tells the operator a Club Admin must revoke the old registration |
| Start at sign-in (check mark) | always | Enables or disables per-user autostart; the check mark shows the read-back state |

On Linux without a StatusNotifierItem host, and whenever the tray icon cannot
be created, the status window opens at start and offers every item above.

## Transport and single instance

- One Unix domain socket per user, `manager.sock`, in an owner-only run
  directory: Windows `%LOCALAPPDATA%\SocAlytics\AnalystManager\run\` (protected
  DACL for the current user), Linux `$XDG_RUNTIME_DIR/socalytics/` (fallback
  `~/.local/state/socalytics/run/`), macOS
  `~/Library/Application Support/SocAlytics/AnalystManager/run/` (fallback
  `$TMPDIR/socalytics/` when the path exceeds 104 bytes). Directory `0700` and
  socket `0600` where the OS applies modes. No network listener exists.
- Every connection is checked against the owning user before a request is
  read: Linux `SO_PEERCRED` uid, Windows `SIO_AF_UNIX_GETPEERPID` then the
  process-token SID, macOS `getpeereid` uid. Other peers are disconnected.
- One request per connection: the client writes one UTF-8 JSON line, the server
  writes one JSON line and closes. Requests larger than 16 KiB are refused with
  `invalid-request`; `protocolVersion` other than `1` gets
  `unsupported-protocol-version`.
- Single instance: a starting app binds the socket. If the file exists it
  connects; when a live instance answers, it sends `show-status` and exits;
  otherwise it removes the stale file and binds.

## Scripting CLI

The tray executable `socalytics-manager` acts as a client when started with a
command and never starts the UI then. On Windows it attaches to the parent
console. Without a command (or with `--autostart`) it starts the tray app.

| Command | Request |
| --- | --- |
| `socalytics-manager status [--json]` | `status` |
| `socalytics-manager register --platform URL [--stamp-id ID] [--host-label TEXT] [--no-wait]` | `register` |
| `socalytics-manager pause [--for MINUTES \| --until RFC3339]` | `pause` |
| `socalytics-manager resume` | `resume` |
| `socalytics-manager exit` | `exit` |
| `socalytics-manager unregister [--discard-local]` | `unregister` |
| `socalytics-manager autostart [on \| off]` | `autostart` |

Exit codes: `0` success, `1` refused by the Manager (error code printed), `2`
usage error, `3` Manager not running or socket not accessible.

## Behavioral guarantees

- Tray, status window, and CLI call the same worker operations; none creates,
  activates, revokes, or rebinds a registration except register and unregister
  (FR-023).
- `pause`, `resume`, and `exit` are refused with `invalid-registration-state`
  unless the registration is `active`; `register` is refused unless it is
  `unregistered`.
- Timed pauses outside 1 minute to `Operating:MaxTimedPause` are refused with
  `invalid-pause-duration`.
- Drain obeys `Operating:DrainTimeout`, `Operating:DrainTimeoutPolicy`, and
  `Operating:CleanupBound` (session end: `Operating:SessionEndDrainBound`);
  status reports the drain deadline.
- No response, log entry, window, or diagnostic contains human credentials,
  access tokens, client assertions, DPoP or activation proofs, polling handles,
  challenges, broker credentials, the PKCS#11 PIN, or private key material. The
  pairing code appears only in the register dialog and the `register` result
  (FR-034). The device fingerprint is not a secret and is shown in the register
  dialog, the status window, and `status` (FR-040).
- `restore-failed` diagnostics use bounded codes (`state-missing`,
  `state-unreadable`, `state-permissions`, `state-signature`, `key-missing`,
  `key-inaccessible`, `key-mismatch`, `stamp-mismatch`) and never include
  protected values.
- Preflight check identifiers are listed in [data-model.md](../data-model.md#preflightresult).
