import { createServer, type Server } from 'node:http';
import type { Page } from '@playwright/test';
import { expect, test } from '../support/fixtures';
import { FAKE_STORAGE_ORIGIN, match, team } from '../support/simulatedApi';
import { MiB, writeTestVideo } from '../support/testFiles';

const previewUrl = 'http://127.0.0.1:4173';
const tamperedPort = 4178;

interface ShellManifest {
  version: string;
  files: { url: string; integrity: string }[];
}

async function readManifest(page: Page): Promise<ShellManifest> {
  const response = await page.request.get('/shell-manifest.json');
  return (await response.json()) as ShellManifest;
}

async function waitForActiveWorker(page: Page) {
  await page.evaluate(async () => {
    await navigator.serviceWorker.ready;
  });
}

async function cachedUrls(page: Page): Promise<string[]> {
  return page.evaluate(async () => {
    const urls: string[] = [];
    for (const name of await caches.keys()) {
      const cache = await caches.open(name);
      urls.push(...(await cache.keys()).map((request) => new URL(request.url).pathname));
    }
    return urls;
  });
}

async function cachedHrefs(page: Page): Promise<string[]> {
  return page.evaluate(async () => {
    const urls: string[] = [];
    for (const name of await caches.keys()) {
      const cache = await caches.open(name);
      urls.push(...(await cache.keys()).map((request) => request.url));
    }
    return urls;
  });
}

async function start(page: Page) {
  await page.goto('/');
  await expect(page.getByRole('heading', { name: 'SocAlytics' })).toBeVisible();
  await waitForActiveWorker(page);
}

test('is installable and the manifest has the required fields', async ({ page }) => {
  await start(page);
  const session = await page.context().newCDPSession(page);
  await expect.poll(async () => (await session.send('Page.getInstallabilityErrors')).installabilityErrors).toEqual([]);

  const manifest = await (await page.request.get('/manifest.webmanifest')).json();
  expect(manifest).toMatchObject({
    name: 'SocAlytics',
    short_name: 'SocAlytics',
    start_url: '/',
    scope: '/',
    display: 'standalone',
    theme_color: '#0b5cad',
  });
  expect(manifest.icons.map((icon: { sizes: string; purpose: string }) => `${icon.sizes}:${icon.purpose}`)).toEqual(['192x192:any', '512x512:any', '512x512:maskable']);
});

test('a repeat start with an unchanged release loads no shell file from the network (SC-002)', async ({ page }) => {
  await start(page);
  await page.reload();
  await expect(page.getByRole('heading', { name: 'SocAlytics' })).toBeVisible();

  const manifest = await readManifest(page);
  const listed = new Set(manifest.files.map((file) => file.url));
  const fromNetwork: string[] = [];
  page.on('response', (response) => {
    const path = new URL(response.url()).pathname;
    if (listed.has(path) && !response.fromServiceWorker()) fromNetwork.push(path);
  });
  const served: string[] = [];
  page.on('response', (response) => {
    if (response.fromServiceWorker()) served.push(new URL(response.url()).pathname);
  });

  const started = Date.now();
  await page.goto('/');
  await expect(page.getByRole('heading', { name: 'SocAlytics' })).toBeVisible({ timeout: 2000 });
  expect(Date.now() - started).toBeLessThan(2000);
  expect(served.length).toBeGreaterThan(0);
  expect(fromNetwork).toEqual([]);
});

test('after the caches are cleared the next start downloads the shell again and works', async ({ page }) => {
  await start(page);
  await page.reload();
  await page.evaluate(async () => {
    for (const name of await caches.keys()) await caches.delete(name);
  });
  const downloaded: string[] = [];
  page.on('requestfinished', (request) => {
    if (new URL(request.url()).pathname.startsWith('/assets/')) downloaded.push(request.url());
  });
  await page.reload();
  await expect(page.getByRole('heading', { name: 'SocAlytics' })).toBeVisible();
  expect(downloaded.length).toBeGreaterThan(0);
});

test('the caches hold only URLs listed in the shell manifest after signing in, browsing, and uploading a recording', async ({ page, api }, testInfo) => {
  test.setTimeout(90_000);
  await fetch(`${FAKE_STORAGE_ORIGIN}/__control/reset`, { method: 'POST', body: '{}' });
  api.on('GET', `/teams/${team.id}`, (route) =>
    route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(team) }),
  );
  api.signInAs('Coach');
  await start(page);
  await page.reload();
  await page.goto(`/matches/${match.id}`);
  await expect(page.getByText('No recordings have been uploaded yet.')).toBeVisible();

  const file = await writeTestVideo(testInfo.outputPath('files'), 3 * MiB);
  await page.getByLabel('Video files').setInputFiles(file.path);
  await page.getByLabel(`Display name for ${file.name}`).fill('Main camera');
  await page.getByRole('button', { name: 'Add to upload queue' }).click();
  const upload = page.getByRole('list', { name: 'Upload queue' }).getByRole('listitem').filter({ hasText: 'Main camera' });
  await upload.getByLabel(/^Media end/).fill('01:00');
  await upload.getByLabel(/^Match time at media start/).fill('10:00');
  await upload.getByRole('button', { name: 'Save mapping for Main camera' }).click();
  await expect(upload.getByRole('status')).toHaveText(/Completed/, { timeout: 30_000 });
  expect(api.uploadSessions.length).toBeGreaterThan(0);

  const listed = new Set((await readManifest(page)).files.map((file) => file.url));
  const urls = await cachedUrls(page);
  expect(urls.length).toBeGreaterThan(0);
  expect(urls.filter((url) => !listed.has(url))).toEqual([]);
  const hrefs = await cachedHrefs(page);
  expect(hrefs.filter((href) => href.startsWith(FAKE_STORAGE_ORIGIN) || /\/api\/|\/bucket\/|partNumber|X-Amz-/.test(href))).toEqual([]);
});

test.describe('a tampered shell file', () => {
  let proxy: Server;

  test.beforeAll(async () => {
    proxy = createServer(async (req, res) => {
      const upstream = await fetch(`${previewUrl}${req.url}`);
      let body = Buffer.from(await upstream.arrayBuffer());
      // The .woff variants are never loaded by Chromium, so the page works while the install must fail.
      if (req.url?.endsWith('.woff')) body = Buffer.concat([body, Buffer.from('x')]);
      const headers: Record<string, string> = {};
      upstream.headers.forEach((value, key) => {
        if (!['content-encoding', 'content-length', 'transfer-encoding'].includes(key)) headers[key] = value;
      });
      res.writeHead(upstream.status, headers);
      res.end(body);
    });
    await new Promise<void>((resolve) => proxy.listen(tamperedPort, '127.0.0.1', resolve));
  });

  test.afterAll(async () => {
    await new Promise((resolve) => proxy.close(resolve));
  });

  test('fails the install and the page still works from the network', async ({ page }) => {
    await page.goto(`http://127.0.0.1:${tamperedPort}/`);
    await expect(page.getByRole('heading', { name: 'SocAlytics' })).toBeVisible();
    await expect
      .poll(() =>
        page.evaluate(async () => {
          const registration = await navigator.serviceWorker.getRegistration();
          return { active: !!registration?.active, cached: (await caches.keys()).length };
        }),
      )
      .toEqual({ active: false, cached: 0 });
    await page.reload();
    await expect(page.getByRole('heading', { name: 'SocAlytics' })).toBeVisible();
  });
});
