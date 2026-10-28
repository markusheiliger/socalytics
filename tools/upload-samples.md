# upload-samples

[`upload-samples.mjs`](upload-samples.mjs) packages every video file below a folder, including nested folders, as one
**private** OCI artifact in the GitHub Container Registry (GHCR). The videos never enter the repository: you commit
only the small manifest the script writes, and [download-samples](download-samples.md) restores the videos from it.

```text
node tools/upload-samples.mjs <folder> [--package <name>] [--manifest <file>] [--dry-run]
```

## Prerequisites

- Node.js 24 (the repository's tooling version).
- The [GitHub CLI](https://cli.github.com), logged in with `gh auth login`. The script uses gh's token for everything;
  you never handle a token yourself.
- [ORAS](https://oras.land/docs/installation), for example `winget install oras` or `brew install oras`.

The script needs the token scopes `write:packages`, `read:packages`, and `delete:packages` (the last one to remove a
version that fails the privacy check). When one is missing it runs
`gh auth refresh -h github.com -s write:packages,read:packages,delete:packages` for you, which asks you to approve in
the browser once.

## Options

| Option | Meaning |
| --- | --- |
| `<folder>` | The folder with the videos. Every file with the extension `.mp4`, `.m4v`, `.mov`, `.mkv`, `.webm`, `.avi`, or `.ts` (in any case) below it is packaged, also in nested folders. Other files are listed as skipped. |
| `--package <name>` | The GHCR package, under your GitHub account: `ghcr.io/<you>/<name>`. Default: `socalytics-samples`. |
| `--manifest <file>` | Writes the sample manifest (the reference with its digest and every file) to this file, for committing. Without it, the manifest is printed. |
| `--dry-run` | Finds the videos and runs every check, but pushes nothing. |
| `--help` | Prints the usage. |

Every upload is a new version of the package, tagged with the UTC time (`yyyyMMdd-HHmmss`). Refer to it by its
**digest** (`…@sha256:…`), which never changes; the download script refuses tags.

## Privacy

The package must stay private: the repository is public, and the videos must never be. The script makes sure of it and
fails closed:

1. **Before pushing**, it reads the package from the GitHub API. A package that exists and is not private, or is
   linked to a repository (whose access it may inherit), is refused, and nothing is pushed. A package that does not
   exist yet is created private by GHCR.
2. **While pushing**, it never sets the `org.opencontainers.image.source` annotation, which would link the package to
   a repository. It passes gh's token to ORAS through stdin and a temporary registry configuration that is deleted
   afterwards, so the token never appears in a command line, a log, or your ORAS configuration.
3. **After pushing**, it checks that GitHub reports the package as private and unlinked, that an **anonymous** pull of
   the new version is refused, and that an authenticated pull works (so the refusal is real, not a broken check). When
   any of this fails, it deletes the version it just pushed and exits with an error.

GitHub cannot make a public package private again. Never change the package's visibility to public, and never turn on
"Inherit access from source repository" in its settings.

## Examples

### A folder with nested match folders

```text
samples/
├── kickoff.mp4
├── match-2026-10-12/
│   ├── half-1.mkv
│   ├── half-2.mkv
│   └── notes.txt
└── training/
    └── drills.mov
```

```powershell
node tools/upload-samples.mjs .\samples --manifest samples.json
```

```text
Found 4 video file(s), 1.2 GiB, below D:\videos\samples:
  kickoff.mp4 (48.0 MiB)
  match-2026-10-12/half-1.mkv (512.4 MiB)
  match-2026-10-12/half-2.mkv (498.7 MiB)
  training/drills.mov (161.9 MiB)
Skipped 1 other file(s):
  match-2026-10-12/notes.txt (not a video)
The package ghcr.io/octocat/socalytics-samples does not exist yet; GHCR creates it private.
Pushing ghcr.io/octocat/socalytics-samples:20261028-153012 ...
Privacy check passed: the package is private, not linked to a repository, and refuses anonymous pulls.
Wrote the sample manifest to samples.json; commit it, never the videos.

Uploaded: ghcr.io/octocat/socalytics-samples@sha256:3f1c…9a2e
Download: node tools/download-samples.mjs ghcr.io/octocat/socalytics-samples@sha256:3f1c…9a2e
CI access: grant the repository read access under Package settings > Manage Actions access of ghcr.io/octocat/socalytics-samples.
```

Each video becomes one layer whose title is its path relative to the folder, so `match-2026-10-12/half-1.mkv` comes
back in the same place when downloaded. The written `samples.json`:

```json
{
  "reference": "ghcr.io/octocat/socalytics-samples@sha256:3f1c…9a2e",
  "tag": "20261028-153012",
  "artifactType": "application/vnd.socalytics.samples.v1",
  "created": "2026-10-28T15:30:12.000Z",
  "files": [
    { "path": "kickoff.mp4", "size": 50331648, "mediaType": "video/mp4", "digest": "sha256:…" },
    { "path": "match-2026-10-12/half-1.mkv", "size": 537291776, "mediaType": "video/x-matroska", "digest": "sha256:…" },
    { "path": "match-2026-10-12/half-2.mkv", "size": 522920345, "mediaType": "video/x-matroska", "digest": "sha256:…" },
    { "path": "training/drills.mov", "size": 169764864, "mediaType": "video/quicktime", "digest": "sha256:…" }
  ]
}
```

### Check first, push later

```powershell
node tools/upload-samples.mjs .\samples --dry-run
```

```text
Found 4 video file(s), 1.2 GiB, below D:\videos\samples:
  …
The package ghcr.io/octocat/socalytics-samples exists and is private.
Dry run: would push 4 file(s) as ghcr.io/octocat/socalytics-samples:20261028-153512. Nothing was pushed.
```

### Another package

```powershell
node tools/upload-samples.mjs .\edge-cases --package socalytics-samples-edge-cases --manifest edge-cases.json
```

### A refused upload

When the package is public (or linked to a repository), nothing is pushed:

```text
upload-samples: Refusing to push to ghcr.io/octocat/socalytics-samples: its visibility is public, not private. A public package cannot be made private again; use another name with --package.
```

When the privacy check after the push fails, the new version is removed again:

```text
upload-samples: The pushed version sha256:3f1c…9a2e failed the privacy check (an anonymous pull is allowed), so it was deleted. Check the package settings of ghcr.io/octocat/socalytics-samples on GitHub before trying again.
```

## After uploading

- **Commit the manifest**, not the videos. Downloads use its `reference`.
- **CI access:** open the package on GitHub (your profile > Packages > `socalytics-samples` > Package settings) and add
  the repository under **Manage Actions access** with the role **Read**. A workflow then pulls with its own
  `GITHUB_TOKEN` and `permissions: packages: read`, without a stored secret; workflows of forks get no access. Pull
  sample videos only in jobs that need them, never in jobs that run agent-written code.
- **Other people:** add them under **Manage access** with the role **Read**; they download with their own `gh` login.
- **Check by hand** that anonymous access is refused (the answer must not contain a token):

  ```powershell
  curl.exe -s "https://ghcr.io/token?scope=repository:octocat/socalytics-samples:pull"
  ```

- **Delete a version:** on the package page, or with
  `gh api -X DELETE /user/packages/container/socalytics-samples/versions/<id>`
  (`gh api /user/packages/container/socalytics-samples/versions` lists them).

## Exit codes

| Code | Meaning |
| --- | --- |
| 0 | Uploaded and verified (or a dry run that passed). |
| 2 | Refused: wrong usage, no videos, a missing tool or scope, a package that is not private, or a failed privacy check. |
| 1 | An unexpected error. |

## Real footage

The tool keeps videos out of the repository and private, but it does not decide whether real match footage may be
used. The architecture states that the evaluation evidence uses no real footage, and retention (`POL-004`) is
unresolved. Recording players, who may be minors, raises rights and personal-data questions: record that decision
before uploading real matches.
