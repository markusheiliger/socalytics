# Contract: Analyst Container Interface - Segment Media Mount

Extends the [completed container interface](../../20261017-220709-python-analyst-sdk/contracts/container-interface.md)
of the Python Analyst SDK feature; everything not listed here is unchanged (job and attempt files, inputs, result,
scratch, no network, no environment variables, no credentials, labels, outcome rules).

## Mounts

| Path in the container | Access | Content | Change |
| --- | --- | --- | --- |
| `/socalytics/media/` | read-only | Present only when the job message carries `segmentMedia`: `segment-manifest.json` (valid against `segments/segment-manifest` v1, byte-identical to the stored manifest) and `segment.mp4` (the materialized segment media, primary video stream only) | new |

Both runtimes (simulated and Docker Desktop) expose the same directory; it lives inside the attempt directory and is
deleted with it when the attempt ends and at Manager start.

## Analyst obligations

- Read media only from `/socalytics/media/segment.mp4`; convert media time to match time with the manifest's
  `timelineSpans`; report facts only inside the job's nominal half-open window (unchanged acceptance rule), never in
  the keyframe context outside `requestedSourceInterval`.
- A job with `segmentMedia.videoPolicy = optional` may run without the directory when its segment failed; the job
  message then has no `segmentMedia`.

## SDK additions (`socalytics_analyst`)

| Member | Type | Behavior |
| --- | --- | --- |
| `AnalystContext.media` | `SegmentMedia \| None` | `None` when the job message has no `segmentMedia` and the directory is absent |
| `SegmentMedia.manifest` | `SegmentManifest` | typed, frozen; validated against the bundled schema copy |
| `SegmentMedia.path` | `pathlib.Path` | `/socalytics/media/segment.mp4`, digest and size verified before the author's function runs |
| `SegmentMedia.to_match_ms(media_ms)` | `int \| None` | match time for a media time inside a mapped span, `None` outside |

Input errors (exit 2): `segment-media-missing` (job carries `segmentMedia` but the directory or a file is absent),
`segment-media-unexpected` (directory present without `segmentMedia`), `segment-manifest-mismatch` (checksum differs from
the job), `segment-media-digest-mismatch` (digest or size differs from the manifest), `segment-manifest-invalid`
(schema), and `unsupported-version` for a newer manifest minor version.
