import { join } from 'node:path';

export const releaseVersions = { a: '2026.1015.100000', b: '2026.1015.110000' } as const;
export type ReleaseName = keyof typeof releaseVersions;

// The two prebuilt releases live in the ignored build cache.
export const releasesRoot = join(import.meta.dirname, '..', '..', '..', 'node_modules', '.cache', 'socalytics-releases');
export const releaseDirectory = (name: ReleaseName) => join(releasesRoot, name);

// The check period of the releases built for the update tests (the production period is 15 minutes).
export const releaseCheckPeriodMs = 3000;
export const releaseServerPort = 4177;
