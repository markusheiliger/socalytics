# Quickstart: Validating Resilient .NET Setup

Run from the repository root. See [contracts/dotnet-resolve-interface.md](contracts/dotnet-resolve-interface.md) for the interface and [data-model.md](data-model.md) for the shapes.

## Prerequisites

- Node.js 24 (as in the Spec Kit workflows). No network, Docker, or .NET SDK is needed for the offline tests.

## 1. Offline decision logic (SC-001 to SC-004, SC-007)

```bash
node --test .github/actions/environment-setup/dotnet-resolve.test.mjs
node --test .github/actions/environment-setup/toolchain.test.mjs .github/actions/environment-verify/analyst-projects.test.mjs
```

Expected: all tests pass without network access. Coverage: primary success and transient success, persistent primary failure with runner-toolchain and alternative-feed fallback, primary success preferred over a satisfying runner SDK, versions below, at, newer-patch, newer-feature-band, and prerelease against both pinned files, disagreeing or unreadable pin files, total failure with every source named and one recommended action, bounded attempts and pauses, and the summary and output contents.

## 2. Repository checks for the changed files

```bash
node .github/scripts/check-markdown.mjs --files .github/actions/environment-setup/README.md
node --test .github/scripts/dependency-cache.test.mjs
```

Expected: no Markdown findings; the dependency cache tests (which read `action.yml`) still pass, showing the `dotnet-version` consumer is unchanged.

## 3. Pins unchanged

`git diff --stat` shows changes only under `.github/actions/environment-setup/`, `.github/actions/environment-verify/action.yml`, and this spec folder; `src/platform/global.json` and `src/analysts/manager/global.json` are untouched.

## 4. On GitHub (after the environment spec is merged; manual evidence)

1. Run an implementation or verification workflow; in the run summary the `.NET SDK` table shows `Source` `primary`, `Fallback used` `no`, and the resolved version, and the NuGet cache key step receives that version.
2. For the fallback evidence, a person reruns with the primary source failing (or a temporary branch that makes the first attempt fail) and reads `Fallback used` `yes` with the source and version. This is the only evidence that the alternative feed serves the pinned band at that time; it is not required to pass the offline tests. Also check manually that `ci.dot.net/public` is an origin independent of the one `setup-dotnet`'s install script uses; if the origins overlap, the fallback adds no resilience against a primary-origin outage and the README residual risk applies.
3. Twin #279's implementation (pull request #309) starts its first task without a manual setup workaround (SC-006).
