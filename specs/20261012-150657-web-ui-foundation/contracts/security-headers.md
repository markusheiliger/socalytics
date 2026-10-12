# Contract: Security Headers and Asset Integrity

## Content Security Policy (preview mode and production-shaped builds)

```text
default-src 'self';
script-src 'self';
style-src 'self' 'unsafe-inline';
img-src 'self' data:;
font-src 'self';
connect-src 'self';
object-src 'none';
base-uri 'none';
frame-ancestors 'none';
form-action 'self';
require-trusted-types-for 'script';
trusted-types default react-dom
```

- No third-party origins. Fonts and icons are bundled.
- `style-src 'unsafe-inline'` is an accepted, recorded trade-off for MUI's
  Emotion runtime styles (research.md § R4). Scripts are fully locked down.
- The development server with hot reload uses a relaxed policy and is never
  used by tests that measure CSP.

## Other headers

`X-Content-Type-Options: nosniff`, `Referrer-Policy: no-referrer`, and
`Permissions-Policy: camera=(), microphone=(), geolocation=()`, set by the
preview server configuration. The production server configuration follows
with the container image (out of scope).

## Subresource Integrity

Every emitted `<script>` and `<link rel="stylesheet">` in `dist/index.html`
carries an `integrity` attribute (SHA-384) and `crossorigin`. A unit test of
the build output checks this.

## Verification

- Browser tests fail on any `securitypolicyviolation` event (SC-006).
- A browser test reads the response headers of the Web UI origin and asserts
  the policy above.
