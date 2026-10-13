import { defineConfig, devices } from '@playwright/test';

const previewUrl = 'http://127.0.0.1:4173';

const platformUrl = 'http://127.0.0.1:4174';
const simulatedOnly = process.argv.some((arg, i, all) => (arg === '--project' && all[i + 1] === 'simulated') || arg === '--project=simulated');
const adminPassword = process.env.SOCALYTICS_E2E_ADMIN_PASSWORD ?? 'E2e-Admin-Password-0001';
export default defineConfig({
  testDir: './tests/e2e',
  fullyParallel: true,
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
    {
      name: 'platform',
      testDir: './tests/e2e/platform',
      use: { ...devices['Desktop Chrome'], baseURL: platformUrl },
    },
  ],
  webServer: [
    {
      command: 'npm run preview -- --host 127.0.0.1 --port 4173 --strictPort',
      url: previewUrl,
      reuseExistingServer: !process.env.CI,
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
