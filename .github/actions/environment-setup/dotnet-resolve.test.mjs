import assert from 'node:assert/strict';
import { describe, it } from 'node:test';

import * as resolver from './dotnet-resolve.mjs';

const { parseRequirement, satisfies, acceptable, decide, formatFailure, formatSummary } = resolver;

export const PLATFORM_FILE = 'src/platform/global.json';
export const MANAGER_FILE = 'src/analysts/manager/global.json';

// Contents of a global.json file; omitted options are left out like in a real file.
export function globalJson({ version = '10.0.400', rollForward, allowPrerelease } = {}) {
  const sdk = { version };
  if (rollForward !== undefined) sdk.rollForward = rollForward;
  if (allowPrerelease !== undefined) sdk.allowPrerelease = allowPrerelease;
  return JSON.stringify({ sdk });
}

// The requirement of one pinned file, parsed through the helper under test.
export function requirement(file = PLATFORM_FILE, options = {}) {
  return parseRequirement(file, globalJson(options));
}

// Both pinned files of the repository (latestFeature, no prerelease).
export function repositoryRequirements(version = '10.0.400') {
  const options = { version, rollForward: 'latestFeature', allowPrerelease: false };
  return [requirement(PLATFORM_FILE, options), requirement(MANAGER_FILE, options)];
}

// Simulated source adapters: every adapter records its calls so tests can assert what was asked.
// outcome: 'success' (offers `version`), 'transient' (fails until `succeedOnAttempt`),
// 'failure' (always fails), 'unavailable' (nothing to offer).
export function simulatedSource(id, { outcome = 'success', version = null, succeedOnAttempt = 2, cause = null } = {}) {
  const calls = [];
  const adapter = {
    id,
    calls,
    async attempt(number) {
      calls.push(number);
      const failure = (message) => ({ source: id, number, outcome: 'failure', version: null, cause: cause ?? message });
      switch (outcome) {
        case 'success':
          return { source: id, number, outcome: 'success', version, cause: null };
        case 'transient':
          return number >= succeedOnAttempt
            ? { source: id, number, outcome: 'success', version, cause: null }
            : failure('simulated transient failure');
        case 'failure':
          return failure('simulated persistent failure');
        case 'unavailable':
          return failure('simulated unavailable source');
        default:
          throw new Error(`Unknown simulated outcome ${outcome}`);
      }
    },
  };
  return adapter;
}

describe('dotnet-resolve test fixtures', () => {
  it('imports the helper exports without side effects', () => {
    for (const fn of [parseRequirement, satisfies, acceptable, decide, formatFailure, formatSummary]) {
      assert.equal(typeof fn, 'function');
    }
    assert.equal(typeof resolver.main, 'function');
  });

  it('builds global.json contents with only the given options', () => {
    assert.deepEqual(JSON.parse(globalJson()), { sdk: { version: '10.0.400' } });
    assert.deepEqual(JSON.parse(globalJson({ version: '9.0.100', rollForward: 'major', allowPrerelease: true })), {
      sdk: { version: '9.0.100', rollForward: 'major', allowPrerelease: true },
    });
  });

  it('simulates success, transient, persistent, and unavailable sources', async () => {
    const success = simulatedSource('primary', { version: '10.0.401' });
    assert.deepEqual(await success.attempt(1), { source: 'primary', number: 1, outcome: 'success', version: '10.0.401', cause: null });

    const transient = simulatedSource('primary', { outcome: 'transient', version: '10.0.401' });
    assert.equal((await transient.attempt(1)).outcome, 'failure');
    assert.equal((await transient.attempt(2)).outcome, 'success');
    assert.deepEqual(transient.calls, [1, 2]);

    const persistent = simulatedSource('primary', { outcome: 'failure' });
    assert.equal((await persistent.attempt(1)).outcome, 'failure');

    const unavailable = simulatedSource('alternative', { outcome: 'unavailable' });
    const result = await unavailable.attempt(1);
    assert.equal(result.outcome, 'failure');
    assert.equal(result.version, null);
  });
});
