export interface FakeObjectStorage {
  server: import('node:http').Server;
  port: number;
  origin: string;
  close(): Promise<void>;
}

export function startFakeObjectStorage(options?: {
  port?: number;
  allowedOrigin?: string;
  host?: string;
}): Promise<FakeObjectStorage>;
