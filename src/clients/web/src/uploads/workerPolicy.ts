export const uploadWorkerPolicyName = 'upload-worker';

interface TrustedTypePolicyFactory {
  createPolicy(name: string, options: { createScriptURL: (input: string) => string }): { createScriptURL(input: string): unknown };
}

export interface UploadWorkerPolicy {
  // Returns a TrustedScriptURL where Trusted Types exist, otherwise the URL itself; throws for any other URL.
  createScriptURL(url: string): string;
}

function absolute(url: string): string {
  return new URL(url, globalThis.location?.href ?? 'http://localhost/').href;
}

// The `upload-worker` Trusted Types policy (research.md § R12): it creates a script URL only for the bundled worker.
// The allowed URL is passed in so that this module never imports the worker module.
export function createUploadWorkerPolicy(allowedUrl: string, factory?: TrustedTypePolicyFactory): UploadWorkerPolicy {
  const allowed = absolute(allowedUrl);
  const check = (input: string): string => {
    let candidate: string | undefined;
    try {
      candidate = absolute(input);
    } catch {
      candidate = undefined;
    }
    if (candidate !== allowed) {
      throw new TypeError('Only the bundled upload worker may be loaded.');
    }
    return allowed;
  };

  const trustedTypes = factory ?? (globalThis as { trustedTypes?: TrustedTypePolicyFactory }).trustedTypes;
  if (!trustedTypes?.createPolicy) {
    return { createScriptURL: check };
  }

  const policy = trustedTypes.createPolicy(uploadWorkerPolicyName, { createScriptURL: check });
  return { createScriptURL: (url) => policy.createScriptURL(url) as string };
}
