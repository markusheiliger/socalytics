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
