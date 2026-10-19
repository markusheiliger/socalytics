// contracts/security-headers.md: the single source of the production security headers, shared by
// preview mode in vite.config.ts and the production server configuration.

// connect-src is the only directive that depends on the deployment; it is rendered by contentSecurityPolicy().
export const productionContentSecurityPolicyDirectives = [
  "default-src 'self'",
  "script-src 'self'",
  "style-src 'self' 'unsafe-inline'",
  "img-src 'self' data:",
  "font-src 'self'",
  'media-src blob:',
  "connect-src 'self'",
  "worker-src 'self'",
  "manifest-src 'self'",
  "object-src 'none'",
  "base-uri 'none'",
  "frame-ancestors 'none'",
  "form-action 'self'",
  "require-trusted-types-for 'script'",
  'trusted-types default react-dom upload-worker service-worker',
];

export const commonHeaders = {
  'X-Content-Type-Options': 'nosniff',
  'Referrer-Policy': 'no-referrer',
  'Permissions-Policy': 'camera=(), microphone=(), geolocation=()',
};

// Renders the production policy; the optional object storage origin is added to connect-src.
export function contentSecurityPolicy(objectStorageOrigin?: string): string {
  return productionContentSecurityPolicyDirectives
    .map((directive) => (directive === "connect-src 'self'" && objectStorageOrigin ? `connect-src 'self' ${objectStorageOrigin}` : directive))
    .join('; ');
}
