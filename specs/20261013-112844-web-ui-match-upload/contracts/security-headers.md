# Contract: Security Headers (changes)

Changes the foundation's
[security-headers.md](../../archive/20261012-150657-web-ui-foundation/contracts/security-headers.md).
Everything not listed here is unchanged.

## Content Security Policy (preview mode and production-shaped builds)

```text
default-src 'self';
script-src 'self';
style-src 'self' 'unsafe-inline';
img-src 'self' data:;
font-src 'self';
media-src blob:;
connect-src 'self' <object-storage-origin>;
worker-src 'self';
object-src 'none';
base-uri 'none';
frame-ancestors 'none';
form-action 'self';
require-trusted-types-for 'script';
trusted-types default react-dom upload-worker
```

| Change | Reason |
| --- | --- |
| `connect-src` adds `<object-storage-origin>` | Part transfers go directly to object storage (FR-007, FR-026). The origin comes from `SOCALYTICS_OBJECT_STORAGE_ORIGIN`; without it the directive stays `'self'` and uploads show a storage-unavailable message |
| `media-src blob:` | Reading the local file's duration through a detached video element (research.md § R9); no playback |
| `worker-src 'self'` | Explicit for the bundled upload worker (previously implied by `script-src`) |
| `trusted-types … upload-worker` | The named policy that creates only the bundled worker's script URL (research.md § R12) |

The production server's policy and the production storage origin follow with
the container image and deployment (out of scope).

## Verification

- `tests/e2e/simulated/headers.spec.ts` asserts the policy above with the fake
  storage origin.
- Browser tests still fail on any `securitypolicyviolation` event (SC-008).
- A unit test asserts that `upload-worker` refuses any URL other than the
  bundled worker.
