# Contract: Fake Object Store Control (state read)

Component-local contract of the `simulated` browser tests' fake S3-compatible store (`src/clients/web/tests/e2e/support/fakeObjectStorage.mjs`).

`GET /__control/state` returns JSON:

| Field | Type | Meaning |
| --- | --- | --- |
| `parts` | array | Stored parts (unchanged). |
| `requests` | array | Recorded part requests since the last `reset` (unchanged). |
| `faults` | object | Active faults (unchanged). |
| `inFlight` | integer, new | Part requests (PUT, the requests the `requests` log can record) whose storage handler started and whose response has not finished or whose connection has not closed. It is incremented at the start of the handler before the body is read and decremented in a `finally` or on both `finish` and `close`, so a request aborted by the client is released. CORS preflight (`OPTIONS`) and control requests are not counted. Live count: `reset` does not change it. It covers only requests the server has seen, not requests still on their way. |

`POST /__control/reset` and `POST /__control/faults` are unchanged. Consumers that ignore unknown fields (all existing ones) are unaffected.
