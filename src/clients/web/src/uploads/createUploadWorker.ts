import workerUrl from './worker/uploadWorker?worker&url';
import { createUploadWorkerPolicy, type UploadWorkerPolicy } from './workerPolicy';

let policy: UploadWorkerPolicy | undefined;

// Creates the module worker from its bundled URL through the `upload-worker` Trusted Types policy (research.md § R12).
export function createUploadWorker(): Worker {
  policy ??= createUploadWorkerPolicy(workerUrl);
  return new Worker(policy.createScriptURL(workerUrl), { type: 'module' });
}
