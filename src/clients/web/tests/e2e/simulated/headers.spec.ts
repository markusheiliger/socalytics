import { expect, test } from '../support/fixtures';

const policy = [
  "default-src 'self'",
  "script-src 'self'",
  "style-src 'self' 'unsafe-inline'",
  "img-src 'self' data:",
  "font-src 'self'",
  'media-src blob:',
  "connect-src 'self' http://127.0.0.1:4176",
  "worker-src 'self'",
  "object-src 'none'",
  "base-uri 'none'",
  "frame-ancestors 'none'",
  "form-action 'self'",
  "require-trusted-types-for 'script'",
  'trusted-types default react-dom upload-worker',
].join('; ');

test('sends the security headers', async ({ request }) => {
  const response = await request.get('/');
  const headers = response.headers();
  expect(headers['content-security-policy']).toBe(policy);
  expect(headers['x-content-type-options']).toBe('nosniff');
  expect(headers['referrer-policy']).toBe('no-referrer');
  expect(headers['permissions-policy']).toBe('camera=(), microphone=(), geolocation=()');
});

test('rejects script creation through the default Trusted Types policy', async ({ page }) => {
  await page.goto('/');
  await expect(page.getByRole('heading', { name: 'SocAlytics' })).toBeVisible();
  const outcome = await page.evaluate(() => {
    try {
      (window as unknown as { trustedTypes: { defaultPolicy: { createScript(s: string): unknown } } }).trustedTypes.defaultPolicy.createScript('1');
      return 'created';
    } catch {
      return 'rejected';
    }
  });
  expect(outcome).toBe('rejected');
});

test('loads without third-party origins', async ({ page }) => {
  const origins = new Set<string>();
  page.on('request', (request) => origins.add(new URL(request.url()).origin));
  await page.goto('/');
  await expect(page.getByRole('heading', { name: 'SocAlytics' })).toBeVisible();
  expect([...origins]).toEqual(['http://127.0.0.1:4173']);
});
