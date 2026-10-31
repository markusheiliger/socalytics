# Tools

Developer tools that are not part of the product and not part of the Spec Kit automation.

| Tool | What it does |
| --- | --- |
| [upload-samples](upload-samples.md) | Uploads every video below a folder as a private OCI artifact in GHCR and tags it `latest`, authenticated through the GitHub CLI. |
| [download-samples](download-samples.md) | Brings a local folder in line with the latest (or a pinned) version: downloads new and changed videos and verifies every file. |

Run their tests from the repository root with `node --test tools/samples.test.mjs` (no network, no Docker).
