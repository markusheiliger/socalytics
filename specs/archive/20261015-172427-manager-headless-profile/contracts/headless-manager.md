# Contract: Headless Manager, Pairing Profile Claim, and Software Key Policy

## 1. Pairing request (`POST /api/v1/analyst-manager/pairings`, registration v1, additive)

`deviceMetadata` gains the optional `claimedApplicationProfile`:

```json
{
  "deviceMetadata": {
    "os": "linux",
    "osVersion": "fixture-os",
    "architecture": "x64",
    "managerVersion": "1.0.0",
    "claimedKeyProtection": "pkcs11-token",
    "claimedApplicationProfile": "headless-server",
    "hostLabel": "gpu-rack-1"
  }
}
```

| Case | Result |
| --- | --- |
| `claimedApplicationProfile` not `tray` or `headless-server` | `400` validation problem, field `deviceMetadata.claimedApplicationProfile` |
| `headless-server` with `os` ≠ `linux` | `400` validation problem, same field |
| `claimedKeyProtection` = `software`, policy does not accept the claimed profile (absent counts as `tray`) | `409` `software-key-not-allowed`, detail "This stamp requires hardware-backed key storage for this Manager profile." |
| Otherwise | unchanged behavior |

Fixtures: `contracts/analyst-manager/registration/v1/pairing.request.json` gains
the field; new negative fixtures for an unknown profile and a non-Linux
`headless-server` claim; the Contracts tests and the Manager golden fixture
tests validate them.

## 2. Stamp discovery (`GET /api/v1/analyst-manager/stamp`, additive)

```json
{
  "stampId": "fixture-stamp",
  "softwareKeysAllowed": false,
  "softwareKeysAllowedFor": ["headless-server"],
  "pairingEndpoint": "…",
  "activationEndpoint": "…",
  "tokenEndpoint": "…"
}
```

`softwareKeysAllowed` keeps its meaning (true only when the policy is `all`);
Managers of this feature read `softwareKeysAllowedFor` and fall back to
`softwareKeysAllowed` when it is absent.

## 3. Registration reads (Registrar requests, Club Admin list, detail, fleet)

Every response that already contains `deviceMetadata` returns
`claimedApplicationProfile` when it was claimed and omits it otherwise. The
OpenAPI snapshot `src/platform/SocAlytics.Platform.Api/openapi/v1.json` and the
generated Web UI client change accordingly. The Web UI shows
"Profile (claimed)": `Headless server`, `Tray`, or `Not reported`.

## 4. Platform configuration

| Key | Values | Default | Validation |
| --- | --- | --- | --- |
| `Registry:SoftwareKeys` | `refused`, `headless-server-only`, `all` | `refused` | `all` outside Development and Test fails start-up |

`Registry:AllowSoftwareKeys` is removed; the AppHost and test hosts use
`Registry:SoftwareKeys=all`.

## 5. Headless executable (`socalytics-manager-server`)

- Without arguments: starts the Core worker in the headless profile (Linux
  only; on Windows and macOS exits with code 1 and "The headless server profile
  is available only on Linux; use socalytics-manager (tray profile)."). A second
  start hands over to the running instance and exits like the tray.
- With a command: the same commands, options, outputs, and exit codes as
  `socalytics-manager` (`status [--json]`, `register`, `pause`, `resume`,
  `exit`, `unregister`, `autostart [on|off]`, `concurrency [n]`).
- `register` without `--host-label` in a non-interactive context: exits with
  code 2 and "Pass --host-label in a non-interactive session." Interactive:
  prompts for the host label, then prints the verification URL, pairing code,
  expiry, and device fingerprint.
- Logs go to standard output and standard error (captured by the journal).
- `SIGTERM`: safe exit (drain within `Operating:DrainTimeout`).

## 6. Local-control `status` (Manager `contracts/local-control.schema.json`, additive)

`status` responses gain `applicationProfile` (`tray` | `headless-server`) and,
in the headless profile, `lingering` (`on` | `off` | `unknown`). Text output
adds `Profile:` and `Lingering:` lines; when lingering is off it adds the hint
`Run 'sudo loginctl enable-linger <user>' so the Manager keeps running without a session.`

## 7. Systemd user unit (`autostart on` in the headless profile)

`$XDG_CONFIG_HOME/systemd/user/socalytics-analyst-manager.service`:

```ini
[Unit]
Description=socAlytics Analyst Manager (headless server profile)
After=network-online.target

[Service]
Type=exec
ExecStart=<absolute path of socalytics-manager-server>
Restart=on-failure
RestartSec=10s
KillSignal=SIGTERM
TimeoutStopSec=<Operating:DrainTimeout + 30 s>

[Install]
WantedBy=default.target
```

`autostart on` writes the file and runs `systemctl --user daemon-reload` and
`systemctl --user enable socalytics-analyst-manager.service`; `autostart off`
runs `disable` and removes the file; `autostart` shows the state. Failures of
`systemctl` are reported with its message; the file stays as written.

## 8. Account rule (preflight check `account-isolation`)

Detail: "A rootful container runtime reached through group membership requires
a dedicated non-login service account." Remediation: "Run the Manager under a
dedicated account without a login shell, or use a rootless runtime (see the
Manager README, Headless Server Profile)." Shown in `status`, reported as the
runtime-unavailable reason in the status report, and shown in the fleet.
