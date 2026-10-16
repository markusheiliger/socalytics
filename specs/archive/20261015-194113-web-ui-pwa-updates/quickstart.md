# Quickstart: Installable Web UI with Service Worker Updates

Contracts: [contracts/web-ui-pwa.md](contracts/web-ui-pwa.md); states:
[data-model.md](data-model.md).

## Automated validation

From `src/clients/web/` (Node 24, Kiota, Playwright Chromium installed):

```powershell
npm ci
npm run build
npm test
npm run test:e2e
```

From the repository root:

```powershell
dotnet build src/platform/SocAlytics.Platform.slnx
dotnet test src/platform/Tests/SocAlytics.Platform.Host.Tests --no-build
node .github/scripts/check-markdown.mjs
```

Expected:

- The build emits `manifest.webmanifest`, the icons, `sw.js` with the release
  version, and `shell-manifest.json` with an integrity for every shell file.
- In Chromium the Web UI has no installability errors; a repeat start loads no
  shell file from the network; `caches` contain only shell URLs.
- Offline, the app opens with the connection-required state.
- Switching the release server from release A to B shows the update notice;
  "Update" runs B in every window.
- With `WebUi:MinimumVersion` above A's version, A sends no further request,
  shows "Updating SocAlytics", and runs B within 30 seconds.
- The header, Trusted Types, SRI, and third-party-origin tests pass with the
  two added CSP directives.

## Manual validation (Windows)

1. Run the AppHost in end-to-end mode (`dotnet run --project src/platform/SocAlytics.Platform.AppHost`)
   and open the Web UI in Edge and in Chrome.
2. Install it from the address bar; it opens in its own window with the
   SocAlytics icon in the taskbar.
3. Rebuild the Web UI, reload the platform's Web UI, and see the update notice
   in the installed window.
4. Disconnect the network and open the installed app: the shell shows the
   connection-required state.
