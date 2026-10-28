# Tools

Developer tools that are not part of the product and not part of the Spec Kit automation.

| Tool | What it does |
| --- | --- |
| [upload-samples](upload-samples.md) | Packages every video below a folder as a private OCI artifact in GHCR, authenticated through the GitHub CLI. |
| [download-samples](download-samples.md) | Downloads such an artifact by digest, restores its folders, and verifies every file. |

Run their tests from the repository root with `node --test tools/samples.test.mjs` (no network, no Docker).
