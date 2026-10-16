import { execFileSync } from 'node:child_process';
import { rmSync } from 'node:fs';
import { releaseCheckPeriodMs, releaseDirectory, releaseVersions, releasesRoot, type ReleaseName } from './releases';

// Playwright global setup: builds release A and release B with distinct versions for the update specs.
export default function buildReleases(): void {
  rmSync(releasesRoot, { recursive: true, force: true });
  for (const name of Object.keys(releaseVersions) as ReleaseName[]) {
    execFileSync('npx', ['vite', 'build', '--outDir', releaseDirectory(name), '--emptyOutDir'], {
      stdio: 'inherit',
      shell: process.platform === 'win32',
      env: {
        ...process.env,
        SOCALYTICS_WEB_VERSION: releaseVersions[name],
        VITE_SOCALYTICS_UPDATE_CHECK_MS: String(releaseCheckPeriodMs),
      },
    });
  }
}
