# upload-samples

[`upload-samples.mjs`](upload-samples.mjs) uploads every video file below a folder, including nested folders, as a
**private** OCI artifact in the GitHub Container Registry (GHCR), and tags it `latest`. The videos never enter the
repository; [download-samples](download-samples.md) fetches them.

```text
node tools/upload-samples.mjs <folder> [--dry-run]
```

Run it inside a clone of the repository: the repository defines where the samples go, `ghcr.io/<repository
owner>/<repository name>-samples`, here `ghcr.io/markusheiliger/socalytics-samples`. Only the repository owner's gh
account can upload.

## Prerequisites

- Node.js 24 (the repository's tooling version).
- The [GitHub CLI](https://cli.github.com), logged in with `gh auth login`. The script uses gh's token for everything;
  you never handle a token yourself.
- [ORAS](https://oras.land/docs/installation), for example `winget install ORASProject.ORAS` or `brew install oras`.

The script needs the token scopes `write:packages` (which includes reading) and `delete:packages` (to remove a version
that fails the privacy check). When one is missing it runs
`gh auth refresh -h github.com -s write:packages,read:packages,delete:packages` for you, which asks you to approve in
the browser once.

## Options

| Option | Meaning |
| --- | --- |
| `<folder>` | The folder with the videos. Every file with the extension `.mp4`, `.m4v`, `.mov`, `.mkv`, `.webm`, `.avi`, or `.ts` (in any case) below it is uploaded, also in nested folders. Other files are listed as skipped. |
| `--dry-run` | Finds the videos and runs every check, but uploads nothing. |
| `--help` | Prints the usage. |

To try the tools without touching the real samples, set `SOCALYTICS_SAMPLES_PACKAGE` to a throwaway package name (for
example `socalytics-samples-selftest`) for both scripts, and delete that package afterwards.

## A growing set

Each upload is a complete snapshot of the folder: a new version of the package with exactly the videos that are in the
folder now, at their current paths. It gets two tags: the UTC time (`yyyyMMdd-HHmmss`) and `latest`, which moves to the
newest upload. Every version also has a digest (`…@sha256:…`) that never changes.

- **Only new videos are uploaded.** The registry stores every file by its content, so videos that an earlier upload
  already sent are not sent again; adding three videos to fifty uploads three.
- **Moving or renaming videos costs nothing.** A new folder structure only changes the paths in the new version.
- **Removed videos** are not in the new version; older versions keep them until you delete those versions.

So keep one folder with the whole set, change it as you like, and upload it again.

## Privacy

The package must stay private: the repository is public, and the videos must never be. The script makes sure of it and
fails closed:

1. **Before uploading**, it reads the package from the GitHub API. A package that exists and is not private, or is
   linked to another repository than this one, is refused, and nothing is uploaded. A package that does not exist yet
   is created private by GHCR. A link to this repository is expected: it is how this repository's workflows get read
   access (see [After uploading](#after-uploading)).
2. **While uploading**, it never sets the `org.opencontainers.image.source` annotation, so an upload never links the
   package by itself. It passes gh's token to ORAS through stdin and a temporary registry configuration that is deleted
   afterwards, so the token never appears in a command line, a log, or your ORAS configuration.
3. **After uploading**, it checks that GitHub reports the package as private and linked to no other repository, that
   an **anonymous** download of the new version is refused, and that an authenticated one works (so the refusal is
   real, not a broken check). Only then does it move `latest`. When a check fails, it deletes the version it just
   uploaded, and `latest` stays where it was.

GitHub cannot make a public package private again. Never change the package's visibility to public. A link to this
repository does not change the visibility: the package stays private, and anonymous downloads stay refused.

## Examples

### Upload a folder with nested folders

```text
D:\videos\samples
├── kickoff.mp4
├── match-2026-10-12\
│   ├── half-1.mkv
│   ├── half-2.mkv
│   └── notes.txt
└── training\
    └── drills.mov
```

```powershell
node tools/upload-samples.mjs D:\videos\samples
```

```text
Found 4 video file(s), 1.2 GiB, below D:\videos\samples:
  kickoff.mp4 (48.0 MiB)
  match-2026-10-12/half-1.mkv (512.4 MiB)
  match-2026-10-12/half-2.mkv (498.7 MiB)
  training/drills.mov (161.9 MiB)
Skipped 1 other file(s):
  match-2026-10-12/notes.txt (not a video)
The package ghcr.io/markusheiliger/socalytics-samples does not exist yet; GHCR creates it private.
Pushing ghcr.io/markusheiliger/socalytics-samples:20261028-153012 ...
Privacy check passed: the package is private, linked to no repository other than markusheiliger/socalytics, and refuses anonymous pulls.

Uploaded 4 file(s) as ghcr.io/markusheiliger/socalytics-samples:latest (also :20261028-153012).
Digest:   ghcr.io/markusheiliger/socalytics-samples@sha256:3f1c…9a2e
Download: node tools/download-samples.mjs
```

Each video keeps its path relative to the folder, so `match-2026-10-12/half-1.mkv` comes back in the same place when
downloaded.

### Upload again after adding videos

Add `match-2026-10-19\half-1.mkv` to the folder and run the same command. Only the new video is sent; the result is a
new version that is now `latest`.

### Check first, upload later

```powershell
node tools/upload-samples.mjs D:\videos\samples --dry-run
```

```text
Found 4 video file(s), 1.2 GiB, below D:\videos\samples:
  …
The package ghcr.io/markusheiliger/socalytics-samples exists and is private.
Dry run: would push 4 file(s) as ghcr.io/markusheiliger/socalytics-samples:20261028-153512 and tag it latest. Nothing was pushed.
```

### A refused upload

When the package is public (or linked to another repository), nothing is uploaded:

```text
upload-samples: Refusing to push to ghcr.io/markusheiliger/socalytics-samples: its visibility is public, not private. A public package cannot be made private again; fix or delete it on GitHub first.
```

When the privacy check after the upload fails, the new version is removed again and `latest` does not move:

```text
upload-samples: The pushed version sha256:3f1c…9a2e failed the privacy check (an anonymous pull is allowed), so it was deleted. Check the package settings of ghcr.io/markusheiliger/socalytics-samples on GitHub before trying again.
```

## After uploading

- **CI access:** the package is connected to this repository (Package settings > **Manage Actions access**, the
  repository with the role **Read**). A workflow then downloads with its own `GITHUB_TOKEN` and
  `permissions: packages: read`, without a stored secret; workflows of forks get no access. This was verified with a
  workflow run that read `latest` with the workflow token alone while an anonymous request got HTTP 401. Download
  sample videos only in jobs that need them, never in jobs that run agent-written code, and pin the digest there.
- **Other people:** add them under **Manage access** with the role **Read**; they download with their own `gh` login.
- **Check by hand** that anonymous access is refused (the answer must say `authentication required`):

  ```powershell
  curl.exe -s "https://ghcr.io/token?scope=repository:markusheiliger/socalytics-samples:pull"
  ```

- **List or delete versions:**
  `gh api /user/packages/container/socalytics-samples/versions --jq '.[] | {id, name, tags: .metadata.container.tags}'`
  lists them; `gh api -X DELETE /user/packages/container/socalytics-samples/versions/<id>` deletes one. Never delete the
  version tagged `latest`.
- **Point `latest` at another version** (for example an upload made before the tag existed):

  ```powershell
  $cfg = "$env:TEMP\oras-tag.json"
  gh auth token | oras login ghcr.io -u markusheiliger --password-stdin --registry-config $cfg
  oras tag ghcr.io/markusheiliger/socalytics-samples@sha256:<digest> latest --registry-config $cfg
  Remove-Item $cfg
  ```

## Exit codes

| Code | Meaning |
| --- | --- |
| 0 | Uploaded, verified, and tagged `latest` (or a dry run that passed). |
| 2 | Refused: wrong usage, no videos, a missing tool or scope, a package that is not private, or a failed privacy check. |
| 1 | An unexpected error. |

## Real footage

The tool keeps videos out of the repository and private, but it does not decide whether real match footage may be
used. The architecture states that the evaluation evidence uses no real footage, and retention (`POL-004`) is
unresolved. Recording players, who may be minors, raises rights and personal-data questions: record that decision
before uploading real matches.
