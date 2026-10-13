import react from '@vitejs/plugin-react';
import { defineConfig } from 'vite';

// Aspire injects the API endpoint as services__api__http__0 (or https) and the Web UI port as PORT.
const apiTarget = process.env.services__api__https__0 ?? process.env.services__api__http__0;

const proxy = apiTarget
  ? {
      '/api': { target: apiTarget, changeOrigin: false, secure: false },
      '/alive': { target: apiTarget, changeOrigin: false, secure: false },
    }
  : undefined;

const port = process.env.PORT ? Number(process.env.PORT) : undefined;

export default defineConfig({
  plugins: [react()],
  server: { port, proxy },
  preview: { port, proxy },
});
