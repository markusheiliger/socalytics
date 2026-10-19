import { fileURLToPath } from 'node:url';
import react from '@vitejs/plugin-react';
import { sri } from 'vite-plugin-sri3';
import { defineConfig } from 'vite';
import { pwaShell } from './build/pwaShell';
import { commonHeaders, contentSecurityPolicy } from './build/securityHeaders';

// Aspire injects the API endpoint as services__api__http__0 (or https) and the Web UI port as PORT.
const apiTarget = process.env.services__api__https__0 ?? process.env.services__api__http__0;

const proxy = apiTarget
  ? {
      '/api': { target: apiTarget, changeOrigin: false, secure: false },
      '/alive': { target: apiTarget, changeOrigin: false, secure: false },
    }
  : undefined;

const port = process.env.PORT ? Number(process.env.PORT) : undefined;

// The development object storage origin (research.md § R11); without it uploads can only reach 'self'.
const objectStorageOrigin = process.env.SOCALYTICS_OBJECT_STORAGE_ORIGIN
  ? new URL(process.env.SOCALYTICS_OBJECT_STORAGE_ORIGIN).origin
  : undefined;
const connectSources = objectStorageOrigin ? `'self' ${objectStorageOrigin}` : "'self'";

// Hot reload needs inline scripts, eval, and a WebSocket; this policy is never used to measure CSP.
const developmentContentSecurityPolicy = [
  "default-src 'self'",
  "script-src 'self' 'unsafe-inline' 'unsafe-eval'",
  "style-src 'self' 'unsafe-inline'",
  "img-src 'self' data:",
  "font-src 'self'",
  'media-src blob:',
  `connect-src ${connectSources} ws: wss:`,
  "worker-src 'self'",
  "manifest-src 'self'",
  "object-src 'none'",
  "base-uri 'none'",
  "form-action 'self'",
].join('; ');

export default defineConfig({
  plugins: [react(), sri(), pwaShell()],
  // Font files must stay separate files because font-src does not allow data: URLs.
  // The service worker is a separate, self-contained entry at the fixed URL /sw.js.
  build: {
    assetsInlineLimit: 0,
    rollupOptions: {
      input: { index: fileURLToPath(new URL('index.html', import.meta.url)), sw: fileURLToPath(new URL('src/serviceWorker/sw.ts', import.meta.url)) },
      output: { entryFileNames: (chunk) => (chunk.name === 'sw' ? 'sw.js' : 'assets/[name]-[hash].js') },
    },
  },
  server: { port, proxy, headers: { ...commonHeaders, 'Content-Security-Policy': developmentContentSecurityPolicy } },
  preview: { port, proxy, headers: { ...commonHeaders, 'Content-Security-Policy': contentSecurityPolicy(objectStorageOrigin) } },
});
