import { describe, expect, it, vi } from 'vitest';
import { createUploadWorkerPolicy, uploadWorkerPolicyName } from '../../../src/uploads/workerPolicy';

const workerUrl = '/assets/uploadWorker-abc123.js';

describe('upload-worker Trusted Types policy', () => {
  it('returns the bundled worker URL', () => {
    const policy = createUploadWorkerPolicy(workerUrl);
    expect(policy.createScriptURL(workerUrl)).toBe(new URL(workerUrl, globalThis.location.href).href);
  });

  it.each([
    '/assets/other.js',
    'https://evil.example/assets/uploadWorker-abc123.js',
    'data:text/javascript,1',
    'blob:http://localhost/1',
    '',
  ])('throws for %s', (url) => {
    const policy = createUploadWorkerPolicy(workerUrl);
    expect(() => policy.createScriptURL(url)).toThrow(TypeError);
  });

  it('registers the policy under its name and enforces the check through the factory', () => {
    const createPolicy = vi.fn((_name: string, options: { createScriptURL: (input: string) => string }) => ({
      createScriptURL: (input: string) => options.createScriptURL(input),
    }));
    const policy = createUploadWorkerPolicy(workerUrl, { createPolicy });
    expect(createPolicy).toHaveBeenCalledWith(uploadWorkerPolicyName, expect.any(Object));
    expect(uploadWorkerPolicyName).toBe('upload-worker');
    expect(policy.createScriptURL(workerUrl)).toContain('uploadWorker-abc123.js');
    expect(() => policy.createScriptURL('/assets/x.js')).toThrow(TypeError);
  });
});
