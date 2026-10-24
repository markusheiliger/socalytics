# Contract: Media Origin, Security Policy, and Development Object Store

## Content Security Policy

`src/clients/web/vite.config.ts`, preview and development policies:

| Directive | Before | After |
| --- | --- | --- |
| `media-src` | `blob:` | `blob: <object-storage-origin>` when `SOCALYTICS_OBJECT_STORAGE_ORIGIN` is set; `blob:` otherwise |

Every other directive is unchanged. `tests/e2e/simulated/headers.spec.ts` asserts the new preview policy, and browser
tests keep failing on any `securitypolicyviolation` event.

## Development object store (AppHost, RustFS)

- `RUSTFS_CORS_ALLOWED_ORIGINS` stays the Web UI origin (and the e2e origins in e2e mode); no other origin is added.
- The platform browser test plays evidence media from the Web UI origin and checks that a request with another
  `Origin` receives no `Access-Control-Allow-Origin` for it.

## Fake object store (simulated browser tests)

`tests/e2e/support/fakeObjectStorage.mjs` gains:

- seeding of objects by key with bytes and content type (test support only);
- `GET` and `HEAD` for seeded objects with `Range` (`206`, `Content-Range`, `Accept-Ranges: bytes`), presigned-style
  expiry (`403` after the query's expiry), and CORS headers only for the configured origin;
- unchanged `PUT` behavior for uploads.

## Service worker

Requests to the object-store origin stay unhandled by the service worker (cross-origin); a unit test asserts it for a
media URL with a `Range` header.
