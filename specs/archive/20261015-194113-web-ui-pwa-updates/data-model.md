# Data Model: Installable Web UI with Service Worker Updates

No platform storage and no migration.

## Release Version

- Shape `YYYY.MMDD.HHMMSS` (UTC build time) or `SOCALYTICS_WEB_VERSION` with
  the same shape; compared as three integers.
- Compiled into the app, stamped into `/sw.js`, and listed in
  `/shell-manifest.json`.

## Shell Manifest (`/shell-manifest.json`, build output)

| Field | Type | Rules |
| --- | --- | --- |
| `version` | string | release version |
| `files` | array of `{ url, integrity }` | every file of `dist/` except `sw.js` and `shell-manifest.json`; `url` root-relative; `integrity` `sha384-…` |

## Application Shell Cache (browser Cache Storage)

- Name `socalytics-shell-<version>`; only shell files of that release.
- Created on install (fails as a whole on any integrity mismatch); older
  `socalytics-shell-*` caches deleted on activation.
- Never contains `/api/**`, `/alive`, object storage, or any non-shell URL.

## Minimum Web UI Version (platform configuration)

| Key | Values | Default | Validation |
| --- | --- | --- | --- |
| `WebUi:MinimumVersion` | empty or `YYYY.MMDD.HHMMSS` | empty (no minimum) | start-up validation of the shape |

Sent as response header `SocAlytics-Minimum-Web-Version` on `/api/**`
responses when set.

## Update State (Web UI, in memory)

| State | Meaning | Shell shows |
| --- | --- | --- |
| `current` | no waiting worker | nothing |
| `available` | a newer worker is waiting | non-blocking "Update available" with "Update" |
| `required` | the platform's minimum exceeds the running version | blocking "Updating SocAlytics"; requests fail fast |
| `reload-required` | required, but no newer worker within 30 seconds | blocking "Reload required" |
