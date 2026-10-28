# download-samples

[`download-samples.mjs`](download-samples.mjs) downloads a sample video artifact that
[upload-samples](upload-samples.md) pushed, restores its nested folders, and verifies every file against its digest.

```text
node tools/download-samples.mjs <ghcr.io/<owner>/<package>@sha256:<digest>> [--output <folder>]
```

## Prerequisites

The same as for uploading: Node.js 24, the [GitHub CLI](https://cli.github.com) logged in with `gh auth login`, and
[ORAS](https://oras.land/docs/installation). Downloading needs only the scope `read:packages`; the script asks gh to add
it when it is missing. You also need read access to the private package (its owner grants it under **Manage access**).

## Options

| Option | Meaning |
| --- | --- |
| `<reference>` | The reference from the committed manifest, pinned by digest. Tags are refused, so you always get exactly what was uploaded. |
| `--output <folder>` | Where to put the videos. Default: `.samples` in the current folder (ignored by Git). The folder must be empty or not exist. |
| `--help` | Prints the usage. |

## Example

```powershell
node tools/download-samples.mjs ghcr.io/octocat/socalytics-samples@sha256:3f1c…9a2e
```

```text
Downloading 4 file(s) from ghcr.io/octocat/socalytics-samples@sha256:3f1c…9a2e into D:\socalytics\.samples ...
  kickoff.mp4
  match-2026-10-12/half-1.mkv
  match-2026-10-12/half-2.mkv
  training/drills.mov
Downloaded and verified 4 file(s) into D:\socalytics\.samples.
```

With the reference from a committed manifest:

```powershell
node tools/download-samples.mjs (Get-Content samples.json | ConvertFrom-Json).reference --output D:\videos\restored
```

## What it checks

- The reference is pinned by digest, and the artifact is a sample artifact
  (`application/vnd.socalytics.samples.v1`).
- Every file path is relative and stays inside the output folder.
- Every downloaded file matches its layer digest (ORAS checks while downloading; the script checks again).

The token stays out of command lines and your ORAS configuration: the script logs ORAS in with gh's token through
stdin and a temporary configuration that it deletes afterwards.

## Exit codes

| Code | Meaning |
| --- | --- |
| 0 | Downloaded and verified. |
| 2 | Refused: wrong usage, a tag instead of a digest, a non-empty output folder, no access, or a file that failed verification. |
| 1 | An unexpected error. |
