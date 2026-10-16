interface TrustedTypePolicyOptions {
  createHTML?: (input: string) => string;
  createScript?: (input: string) => string;
  createScriptURL?: (input: string) => string;
}

interface TrustedTypesWindow {
  trustedTypes?: { createPolicy(name: string, options: TrustedTypePolicyOptions): unknown };
}

// The `default` Trusted Types policy: script creation is always rejected, so no string can become executable code.
// HTML is passed through because React and MUI create it from their own trusted templates only.
const trustedTypes = (globalThis as TrustedTypesWindow).trustedTypes;

if (trustedTypes?.createPolicy) {
  try {
    trustedTypes.createPolicy('default', {
      createHTML: (input: string) => input,
      createScript: () => {
        throw new TypeError('Script creation is not allowed.');
      },
      createScriptURL: () => {
        throw new TypeError('Script URL creation is not allowed.');
      },
    });
  } catch {
    // The policy already exists (hot reload); keep the registered one.
  }
}

// The `service-worker` policy creates a script URL for exactly `/sw.js`; registration.ts is its only user.
const checkServiceWorkerUrl = (input: string): string => {
  if (input !== '/sw.js') {
    throw new TypeError('Only the service worker may be registered.');
  }
  return input;
};

let serviceWorkerPolicy: { createScriptURL(input: string): unknown } | undefined;

if (trustedTypes?.createPolicy) {
  try {
    serviceWorkerPolicy = trustedTypes.createPolicy('service-worker', { createScriptURL: checkServiceWorkerUrl }) as typeof serviceWorkerPolicy;
  } catch {
    // The policy already exists (hot reload); registration then falls back to the plain check.
  }
}

export function serviceWorkerScriptUrl(url: string): string {
  return (serviceWorkerPolicy ? serviceWorkerPolicy.createScriptURL(url) : checkServiceWorkerUrl(url)) as string;
}
