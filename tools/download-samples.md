# download-samples

[`download-samples.mjs`](download-samples.mjs) brings a local folder in line with one version of the sample videos
that [upload-samples](upload-samples.md) uploaded, by default the latest. It downloads new and changed videos, keeps
the ones that already match, removes videos the version no longer has, and verifies every file.

```text
node tools/download-samples.mjs [<reference>] [--package <name>] [--output <folder>]
```

## Prerequisites

The same as for uploading: Node.js 24, the [GitHub CLI](https://cli.github.com) logged in with `gh auth login`, and
[ORAS](https://oras.land/docs/installation). Downloading needs only the scope `read:packages`; the script asks gh to add
it when it is missing. You also need read access to the private package (its owner grants it under **Manage access**).

## Options

| Option | Meaning |
| --- | --- |
| `<reference>` | Optional. `ghcr.io/<owner>/<package>@sha256:<digest>` for exactly that version, or `ghcr.io/<owner>/<package>:<tag>`, which is resolved to its digest first. Without it: `latest` of your own package. |
| `--package <name>` | Without a reference: the package under your account. Default: `socalytics-samples`. |
| `--output <folder>` | Where the videos go. Default: `.samples` in the current folder (ignored by Git). |
| `--help` | Prints the usage. |

## The output folder

The script writes `.samples-reference` (the digest it downloaded) into the folder. A folder with that file, an empty
folder, or a folder that does not exist yet can be brought up to date; any other folder is refused, so the script never
deletes videos it did not download. Inside its folder it:

- keeps every video whose content already matches (no download), and copies a video that moved from its old local path
  instead of downloading it;
- downloads new and changed videos, each into a `.partial` file that replaces the old one only after its digest is
  verified;
- removes videos that the version no longer has (removed or moved), and folders that become empty;
- leaves files that are not videos alone.

## Examples

### Get the latest videos

```powershell
node tools/download-samples.mjs
```

```text
ghcr.io/octocat/socalytics-samples:latest is ghcr.io/octocat/socalytics-samples@sha256:3f1c…9a2e
Bringing D:\socalytics\.samples in line with 4 file(s) ...
  + kickoff.mp4
  + match-2026-10-12/half-1.mkv
  + match-2026-10-12/half-2.mkv
  + training/drills.mov
D:\socalytics\.samples matches ghcr.io/octocat/socalytics-samples@sha256:3f1c…9a2e: 4 downloaded, 0 moved locally, 0 unchanged, 0 removed.
```

### Update after a new upload

Someone added a match and moved the training video:

```text
ghcr.io/octocat/socalytics-samples:latest is ghcr.io/octocat/socalytics-samples@sha256:8b20…41d7
Bringing D:\socalytics\.samples in line with 5 file(s) ...
  + match-2026-10-19/half-1.mkv
  > training/2026/drills.mov (from training/drills.mov)
  - training/drills.mov
D:\socalytics\.samples matches ghcr.io/octocat/socalytics-samples@sha256:8b20…41d7: 1 downloaded, 1 moved locally, 3 unchanged, 1 removed.
```

### Exactly one version

For results you want to repeat (a test, an evaluation report, CI), pin the digest the upload or an earlier download
printed:

```powershell
node tools/download-samples.mjs ghcr.io/octocat/socalytics-samples@sha256:3f1c…9a2e --output D:\videos\pinned
```

### Someone else's package

```powershell
node tools/download-samples.mjs ghcr.io/octocat/socalytics-samples:latest
```

## What it checks

- The artifact is a sample artifact (`application/vnd.socalytics.samples.v1`).
- Every file path is relative and stays inside the output folder.
- Every downloaded file matches its digest before it replaces anything.

The token stays out of command lines and your ORAS configuration: the script logs ORAS in with gh's token through
stdin and a temporary configuration that it deletes afterwards.

## Exit codes

| Code | Meaning |
| --- | --- |
| 0 | The folder matches the version. |
| 2 | Refused: wrong usage, a folder the script did not fill, no access, or a file that failed verification. |
| 1 | An unexpected error. |
