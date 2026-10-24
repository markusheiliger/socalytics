export interface FakeObjectStorage {
  server: import('node:http').Server;
  port: number;
  origin: string;
  /** Seeds (or replaces) an object by key, for in-process tests; separate processes use `POST /__control/objects`. */
  seedObject(key: string, bytes: Uint8Array, contentType?: string): void;
  close(): Promise<void>;
}

export function startFakeObjectStorage(options?: {
  port?: number;
  allowedOrigin?: string;
  host?: string;
}): Promise<FakeObjectStorage>;
