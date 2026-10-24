// contracts/security-headers.md: the single source of the production security headers, shared by
// preview mode in vite.config.ts and the production server configuration.

// connect-src and media-src are the only directives that depend on the deployment; they are rendered by contentSecurityPolicy().
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

// Renders the production policy; the optional object storage origin is added to connect-src and media-src.
export function contentSecurityPolicy(objectStorageOrigin?: string): string {
  return productionContentSecurityPolicyDirectives
    .map((directive) => {
      if (!objectStorageOrigin) return directive;
      if (directive === "connect-src 'self'") return `connect-src 'self' ${objectStorageOrigin}`;
      if (directive === 'media-src blob:') return `media-src blob: ${objectStorageOrigin}`;
      return directive;
    })
    .join('; ');
}
