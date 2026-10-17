import { readFile } from 'node:fs/promises';
import { createServer, type Server } from 'node:http';
import { extname, join, normalize } from 'node:path';
import config from '../../../vite.config';
import { releaseDirectory, releaseServerPort, type ReleaseName } from './releases';

const contentTypes: Record<string, string> = {
  '.html': 'text/html; charset=utf-8',
  '.js': 'text/javascript; charset=utf-8',
  '.css': 'text/css; charset=utf-8',
  '.json': 'application/json',
  '.webmanifest': 'application/manifest+json',
  '.png': 'image/png',
  '.woff': 'font/woff',
  '.woff2': 'font/woff2',
};

// The same security headers as `vite preview`.
const previewHeaders = (config as { preview: { headers: Record<string, string> } }).preview.headers;

/** Serves one of two prebuilt releases with the preview security headers; `use` switches between them. */
export class ReleaseServer {
  readonly origin = `http://127.0.0.1:${releaseServerPort}`;
  private current: ReleaseName = 'a';
  private unavailable = false;
  private server?: Server;

  async start(release: ReleaseName = 'a'): Promise<void> {
    this.current = release;
    this.server = createServer(async (req, res) => {
      const path = normalize(decodeURIComponent(new URL(req.url ?? '/', this.origin).pathname));
      if (this.unavailable || path.includes('..')) {
        res.writeHead(503).end();
        return;
      }
      const root = releaseDirectory(this.current);
      let file = join(root, path === '/' ? 'index.html' : path);
      let body: Buffer;
      try {
        body = await readFile(file);
      } catch {
        file = join(root, 'index.html');
        body = await readFile(file);
      }
      res.writeHead(200, {
        ...previewHeaders,
        'Content-Type': contentTypes[extname(file)] ?? 'application/octet-stream',
        'Cache-Control': 'no-cache',
      });
      res.end(body);
    });
    await new Promise<void>((resolve) => this.server!.listen(releaseServerPort, '127.0.0.1', resolve));
  }

  use(release: ReleaseName): void {
    this.current = release;
  }

  /** While unavailable every request fails with 503, so no newer worker can arrive. */
  setUnavailable(unavailable: boolean): void {
    this.unavailable = unavailable;
  }

  async close(): Promise<void> {
    await new Promise((resolve) => this.server?.close(resolve));
  }
}
