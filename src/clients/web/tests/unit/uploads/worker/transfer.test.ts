import { describe, expect, it, vi } from 'vitest';
import { File } from './nodeFile';
import {
  createTransferManager,
  sessionLockName,
  slotLockName,
  type LockManagerLike,
  type UploadRequest,
} from '../../../../src/uploads/worker/transfer';
import type { Journal } from '../../../../src/uploads/worker/journal';
import type { UploadDeclaration, UploadGrant, WorkerToMainMessage } from '../../../../src/uploads/worker/protocol';

class FakeRequest implements UploadRequest {
  upload: UploadRequest['upload'] = { onprogress: null };
  status = 0;
  responseText = '';
  onload: (() => void) | null = null;
  onerror: (() => void) | null = null;
  ontimeout: (() => void) | null = null;
  onabort: (() => void) | null = null;
  headers: Record<string, string> = {};
  url = '';
  body: Blob | undefined;
  aborted = false;
  open(_method: string, url: string) {
    this.url = url;
  }
  setRequestHeader(name: string, value: string) {
    this.headers[name] = value;
  }
  send(body: Blob) {
    this.body = body;
  }
  abort() {
    this.aborted = true;
    this.onabort?.();
  }
  respond(status: number, text = '') {
    this.status = status;
    this.responseText = text;
    this.onload?.();
  }
}

class FakeLocks implements LockManagerLike {
  held = new Set<string>();
  requested: string[] = [];
  private queue = new Map<string, Array<() => void>>();
  request(name: string, options: { ifAvailable?: boolean; signal?: AbortSignal }, callback: (lock: unknown) => Promise<unknown>) {
    this.requested.push(name);
    const run = async () => {
      this.held.add(name);
      try {
        return await callback({ name });
      } finally {
        this.held.delete(name);
        this.queue.get(name)?.shift()?.();
      }
    };
    if (!this.held.has(name)) {
      return run();
    }
    if (options.ifAvailable) {
      return callback(null);
    }
    return new Promise((resolve, reject) => {
      const waiter = () => void run().then(resolve, reject);
      this.queue.set(name, [...(this.queue.get(name) ?? []), waiter]);
      options.signal?.addEventListener('abort', () => {
        this.queue.set(name, (this.queue.get(name) ?? []).filter((w) => w !== waiter));
        reject(new DOMException('aborted', 'AbortError'));
      });
    });
  }
}

const flush = () => new Promise((resolve) => setTimeout(resolve, 0));
const future = () => new Date(Date.now() + 10 * 60_000).toISOString();

function declaration(parts: number): UploadDeclaration {
  return {
    totalSizeBytes: parts * 10,
    partSizeBytes: 10,
    partCount: parts,
    partDigests: Array.from({ length: parts }, () => 'sha-256:' + '0'.repeat(64)),
    contentDigest: 'x',
  };
}

function grant(partNumber: number): UploadGrant {
  return {
    partNumber,
    url: `https://storage.test/p${partNumber}`,
    requiredHeaders: { 'X-Amz-Checksum-Sha256': `sum${partNumber}`, 'content-type': 'x' },
    expiresAt: future(),
  };
}

function setup(parts: number, confirmed: number[] = [], withLocks = true) {
  const posted: WorkerToMainMessage[] = [];
  const requests: FakeRequest[] = [];
  const timers: Array<{ callback: () => void; delay: number }> = [];
  const locks = new FakeLocks();
  const journal = { confirmParts: vi.fn().mockResolvedValue(true) } as unknown as Journal;
  const manager = createTransferManager({
    post: (m) => posted.push(m),
    journal,
    locks: withLocks ? locks : undefined,
    createRequest: () => {
      const r = new FakeRequest();
      requests.push(r);
      return r;
    },
    setTimer: (callback, delay) => {
      timers.push({ callback, delay });
      return timers.length;
    },
    clearTimer: () => undefined,
    random: () => 0.5,
  });
  const file = new File([new Uint8Array(parts * 10)], 'a.mp4', { type: 'video/mp4' });
  const start = async (jobId = 'j1', sessionId = 's1') => {
    await manager.transfer({ type: 'transfer', jobId, uploadSessionId: sessionId, file, declaration: declaration(parts), confirmedParts: confirmed });
    await flush();
  };
  return { posted, requests, timers, locks, journal, manager, start, file };
}

describe('transfer', () => {
  it('holds both locks, asks for grants, and sends four parts in parallel with only the checksum header', async () => {
    const t = setup(6);
    await t.start();
    expect(t.locks.requested).toEqual([sessionLockName('s1'), slotLockName]);
    expect(t.posted).toContainEqual({ type: 'needGrants', jobId: 'j1', partNumbers: [1, 2, 3, 4, 5, 6] });

    t.manager.grants('j1', [1, 2, 3, 4, 5, 6].map(grant));
    expect(t.requests).toHaveLength(4);
    expect(t.requests[0].headers).toEqual({ 'x-amz-checksum-sha256': 'sum1' });
    expect(t.requests[0].url).toBe('https://storage.test/p1');
    expect(t.requests[0].body?.type).toBe('');
    expect(t.requests[0].body?.size).toBe(10);
  });

  it('reports progress, confirms parts in the journal, and finishes when every part is stored', async () => {
    const t = setup(2);
    await t.start();
    t.manager.grants('j1', [grant(1), grant(2)]);
    t.requests[0].upload.onprogress?.({ loaded: 4 });
    expect(t.posted.at(-1)).toMatchObject({ type: 'transferProgress', bytesInFlight: 4, bytesConfirmed: 0 });
    t.requests[0].respond(200);
    t.requests[1].respond(204);
    await flush();
    expect(t.journal.confirmParts).toHaveBeenLastCalledWith('s1', [1, 2]);
    expect(t.posted).toContainEqual({ type: 'transferred', jobId: 'j1' });
    expect(t.locks.held.size).toBe(0);
  });

  it('skips parts that are already confirmed', async () => {
    const t = setup(3, [1, 2]);
    await t.start();
    expect(t.posted).toContainEqual({ type: 'needGrants', jobId: 'j1', partNumbers: [3] });
  });

  it('requests at most 32 grants ahead', async () => {
    const t = setup(40);
    await t.start();
    const need = t.posted.find((m) => m.type === 'needGrants');
    expect(need?.type === 'needGrants' && need.partNumbers).toHaveLength(32);
  });

  it('retries a retryable failure after the policy delay and then pauses when retries are exhausted', async () => {
    const t = setup(1);
    await t.start();
    t.manager.grants('j1', [grant(1)]);
    for (let attempt = 1; attempt <= 5; attempt++) {
      t.requests.at(-1)!.respond(503);
      expect(t.posted.at(-1)).toMatchObject({ type: 'partFailed', attempt, retryable: true, status: 503 });
      t.timers.at(-1)!.callback();
    }
    t.requests.at(-1)!.respond(503);
    await flush();
    expect(t.posted).toContainEqual({ type: 'partFailed', jobId: 'j1', partNumber: 1, status: 503, retryable: false, attempt: 6 });
    expect(t.posted.at(-1)).toEqual({ type: 'paused', jobId: 'j1', reason: 'retriesExhausted' });
    expect(t.timers.map((x) => x.delay)).toEqual([1000, 2000, 4000, 8000, 16000]);
    expect(t.locks.held.has(slotLockName)).toBe(false);
  });

  it('treats a network error as retryable', async () => {
    const t = setup(1);
    await t.start();
    t.manager.grants('j1', [grant(1)]);
    t.requests[0].onerror?.();
    expect(t.posted.at(-1)).toMatchObject({ type: 'partFailed', status: 0, retryable: true });
  });

  it('asks for a new grant once after 403 and does not count a failure', async () => {
    const t = setup(1);
    await t.start();
    t.manager.grants('j1', [grant(1)]);
    t.requests[0].respond(403);
    expect(t.posted.filter((m) => m.type === 'partFailed')).toHaveLength(0);
    expect(t.posted.filter((m) => m.type === 'needGrants')).toHaveLength(2);
    t.manager.grants('j1', [grant(1)]);
    t.requests[1].respond(403);
    expect(t.posted.find((m) => m.type === 'partFailed')).toMatchObject({ status: 403, retryable: false });
  });

  it('fails with fileChanged on BadDigest and when the size differs', async () => {
    const t = setup(1);
    await t.start();
    t.manager.grants('j1', [grant(1)]);
    t.requests[0].respond(400, '<Code>BadDigest</Code>');
    expect(t.posted.at(-1)).toEqual({ type: 'failed', jobId: 'j1', reason: 'fileChanged' });

    const posted: WorkerToMainMessage[] = [];
    const m = createTransferManager({ post: (x) => posted.push(x), journal: {} as Journal });
    await m.transfer({
      type: 'transfer', jobId: 'j2', uploadSessionId: 's2', file: new File([new Uint8Array(3)], 'a'), declaration: declaration(1), confirmedParts: [],
    });
    await flush();
    expect(posted).toEqual([{ type: 'failed', jobId: 'j2', reason: 'fileChanged' }]);
  });

  it('pauses by aborting in-flight requests, releases the slot, and resumes', async () => {
    const t = setup(2);
    await t.start();
    t.manager.grants('j1', [grant(1), grant(2)]);
    t.manager.pause('j1');
    await flush();
    expect(t.requests.every((r) => r.aborted)).toBe(true);
    expect(t.locks.held.has(slotLockName)).toBe(false);
    expect(t.locks.held.has(sessionLockName('s1'))).toBe(true);
    t.manager.resume('j1');
    await flush();
    expect(t.requests).toHaveLength(4);
    expect(t.requests.slice(2).every((r) => !r.aborted)).toBe(true);
    expect(t.locks.held.has(slotLockName)).toBe(true);
  });

  it('abort releases both locks and drops the job', async () => {
    const t = setup(2);
    await t.start();
    t.manager.grants('j1', [grant(1), grant(2)]);
    t.manager.abort('j1');
    await flush();
    expect(t.requests.every((r) => r.aborted)).toBe(true);
    expect(t.locks.held.size).toBe(0);
    t.manager.grants('j1', [grant(1)]);
    expect(t.requests).toHaveLength(2);
  });

  it('reports runningElsewhere when another tab holds the session lock', async () => {
    const t = setup(1);
    t.locks.held.add(sessionLockName('s1'));
    await t.start();
    expect(t.posted).toContainEqual({ type: 'paused', jobId: 'j1', reason: 'runningElsewhere' });
  });

  it('lets one file transfer at a time through the slot lock', async () => {
    const t = setup(1);
    await t.start('j1', 's1');
    await t.start('j2', 's2');
    expect(t.posted.filter((m) => m.type === 'needGrants' && m.jobId === 'j2')).toHaveLength(0);
    t.manager.abort('j1');
    await flush();
    expect(t.posted).toContainEqual({ type: 'needGrants', jobId: 'j2', partNumbers: [1] });
  });

  it('pauses with storageUnavailable when the journal cannot be written', async () => {
    const t = setup(2);
    (t.journal.confirmParts as ReturnType<typeof vi.fn>).mockRejectedValue(new Error('full'));
    await t.start();
    t.manager.grants('j1', [grant(1), grant(2)]);
    t.requests[0].respond(200);
    await flush();
    expect(t.posted.at(-1)).toEqual({ type: 'paused', jobId: 'j1', reason: 'storageUnavailable' });
  });

  it('aborts the transfers of the given sessions', async () => {
    const t = setup(1);
    await t.start();
    t.manager.grants('j1', [grant(1)]);
    t.manager.abortSessions(['s1']);
    expect(t.requests[0].aborted).toBe(true);
  });
});
