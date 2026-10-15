import { defineConfig, devices } from '@playwright/test';

const previewUrl = 'http://127.0.0.1:4173';

const fakeStoragePort = 4175;
const fakeStorageUrl = `http://127.0.0.1:${fakeStoragePort}`;
const platformUrl = 'http://127.0.0.1:4174';
const simulatedOnly = process.argv.some((arg, i, all) => (arg === '--project' && all[i + 1] === 'simulated') || arg === '--project=simulated');
const adminPassword = process.env.SOCALYTICS_E2E_ADMIN_PASSWORD ?? 'E2e-Admin-Password-0001';
export default defineConfig({
  testDir: './tests/e2e',
  fullyParallel: true,
  // The simulated specs share one fake object store and its fault state, so spec files must not run concurrently.
  workers: 1,
  forbidOnly: !!process.env.CI,
  reporter: process.env.CI ? [['list'], ['html', { open: 'never' }]] : 'list',
  use: {
    baseURL: previewUrl,
    trace: 'retain-on-failure',
  },
  projects: [
    {
      name: 'simulated',
      testDir: './tests/e2e/simulated',
      use: { ...devices['Desktop Chrome'] },
    },
    // The platform specs share one platform instance, the Club Admin's changed password, and the club's single active
    // season, so they run strictly one after another through chained projects. `--project platform` runs the whole chain.
    {
      name: 'platform-session',
      testDir: './tests/e2e/platform',
      testMatch: /(session|origin)\.spec\.ts$/,
      fullyParallel: false,
      use: { ...devices['Desktop Chrome'], baseURL: platformUrl },
    },
    {
      name: 'platform-navigation',
      testDir: './tests/e2e/platform',
      testMatch: /navigation\.spec\.ts$/,
      fullyParallel: false,
      dependencies: ['platform-session'],
      use: { ...devices['Desktop Chrome'], baseURL: platformUrl },
    },
    {
      name: 'platform-club-structure',
      testDir: './tests/e2e/platform',
      testMatch: /club-structure\.spec\.ts$/,
      fullyParallel: false,
      dependencies: ['platform-navigation'],
      use: { ...devices['Desktop Chrome'], baseURL: platformUrl },
    },
    {
      name: 'platform-members',
      testDir: './tests/e2e/platform',
      testMatch: /members\.spec\.ts$/,
      fullyParallel: false,
      dependencies: ['platform-club-structure'],
      use: { ...devices['Desktop Chrome'], baseURL: platformUrl },
    },
    {
      name: 'platform-matches',
      testDir: './tests/e2e/platform',
      testMatch: /matches\.spec\.ts$/,
      fullyParallel: false,
      dependencies: ['platform-members'],
      use: { ...devices['Desktop Chrome'], baseURL: platformUrl },
    },
    {
      name: 'platform-upload',
      testDir: './tests/e2e/platform',
      testMatch: /upload-flow\.spec\.ts$/,
      fullyParallel: false,
      dependencies: ['platform-matches'],
      use: { ...devices['Desktop Chrome'], baseURL: platformUrl },
    },
    {
      name: 'platform-recovery',
      testDir: './tests/e2e/platform',
      testMatch: /recovery\.spec\.ts$/,
      fullyParallel: false,
      dependencies: ['platform-upload'],
      use: { ...devices['Desktop Chrome'], baseURL: platformUrl },
    },
    {
      name: 'platform',
      testDir: './tests/e2e/platform',
      testMatch: /manager-fleet\.spec\.ts$/,
      fullyParallel: false,
      dependencies: ['platform-recovery'],
      use: { ...devices['Desktop Chrome'], baseURL: platformUrl },
    },
  ],
  webServer: [
    {
      command: 'npm run preview -- --host 127.0.0.1 --port 4173 --strictPort',
      url: previewUrl,
      reuseExistingServer: !process.env.CI,
      // The preview policy allows the fake object store in connect-src (contracts/development-storage.md).
      env: { SOCALYTICS_OBJECT_STORAGE_ORIGIN: fakeStorageUrl },
    },
    // Fake S3-compatible store for the simulated upload tests; it answers CORS preflights for the preview origin only.
    {
      command: 'node tests/e2e/support/fakeObjectStorage.mjs',
      url: `${fakeStorageUrl}/__control/health`,
      reuseExistingServer: !process.env.CI,
      env: { FAKE_STORAGE_PORT: String(fakeStoragePort), FAKE_STORAGE_ALLOWED_ORIGIN: previewUrl },
    },
    // The AppHost needs Docker, so it is skipped when only the simulated project runs.
    ...(simulatedOnly ? [] : [{
      // The AppHost in e2e mode builds nothing itself, so the Web UI is built first.
      command: 'npm run build && dotnet run --project ../../platform/SocAlytics.Platform.AppHost',
      url: platformUrl,
      timeout: 10 * 60 * 1000,
      reuseExistingServer: !process.env.CI,
      env: {
        SOCALYTICS_APPHOST_MODE: 'e2e',
        SOCALYTICS_E2E_ADMIN_PASSWORD: adminPassword,
      },
    }]),
  ],
});
