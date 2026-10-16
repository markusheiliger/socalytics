# Contract: Web UI Installation, Service Worker, and Minimum Version

## 1. Platform response header

| Header | On | Value |
| --- | --- | --- |
| `SocAlytics-Minimum-Web-Version` | every `/api/**` response, when `WebUi:MinimumVersion` is set | `YYYY.MMDD.HHMMSS` |

The platform never refuses a request because of the client's version. The
OpenAPI snapshot documents the header as a common response header where the
document already lists shared headers; otherwise the Host test is its
contract.

## 2. Static files served by the Web UI

| Path | Content | Caching by the worker |
| --- | --- | --- |
| `/manifest.webmanifest` | name, short name, start URL `/`, scope `/`, `standalone`, theme and background color, icons 192, 512, 512 maskable | shell |
| `/icons/*.png` | the three icons | shell |
| `/sw.js` | the service worker with the release version | never cached by itself; fetched with `updateViaCache: 'none'` |
| `/shell-manifest.json` | `{ "version", "files": [{ "url", "integrity" }] }` | read on install only |
| `/index.html`, `/assets/**`, fonts, translations | the application shell | shell |

## 3. Service worker behavior

- **install**: fetch `/shell-manifest.json`, then `cache.addAll` every file as
  `new Request(url, { integrity })` into `socalytics-shell-<version>`; any
  failure fails the install (the previous release keeps serving).
- **activate**: delete other `socalytics-shell-*` caches; no `clients.claim()`
  on first install beyond the standard first-load control.
- **fetch**: navigations → cached `/index.html` (network if missing);
  same-origin shell URLs listed in the manifest → cache first; everything else
  (including `/api/**`, `/alive`, and other origins) → not handled.
- **message** `SKIP_WAITING` → `skipWaiting()`.

## 4. Content Security Policy (preview and production-shaped builds)

The current policy plus:

- `manifest-src 'self'`
- `trusted-types default react-dom upload-worker service-worker`

## 5. Web UI notices (English strings in `src/clients/web/src/i18n/en/shell.json`)

| State | Text |
| --- | --- |
| `available` | "A new version of SocAlytics is available." with the action "Update" |
| `required` | "SocAlytics is updating to the version this server requires. Unsaved input on this screen is lost; uploads continue after the update." |
| `reload-required` | "Reload SocAlytics to continue. This server requires a newer version." with the action "Reload" |
| offline | the existing connection-required state |
