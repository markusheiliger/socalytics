# Contracts: Analyst Manager Minimum Version

All changes are additive within `/api/v1` and the existing v1 schema families
([Contracts and Compatibility](../../../../docs/architecture/contracts-and-compatibility.md#versioning-and-compatibility)).

## 1. Status report response — `POST /api/v1/analyst-managers/self/status`

`contracts/analyst-manager/status/v1/status.schema.json` moves from `1.0.0` to `1.1.0`; the `1.0.0` schema is kept at
`contracts/analyst-manager/status/v1/releases/1.0.0.schema.json`. The `response` definition gains one optional
property; nothing else changes.

```json
"minimumManagerVersion": {
  "type": ["string", "null"],
  "maxLength": 64,
  "pattern": "^(0|[1-9][0-9]*)\\.(0|[1-9][0-9]*)\\.(0|[1-9][0-9]*)$"
}
```

New valid examples: `examples/valid/accepted-with-minimum.response.json` (`"minimumManagerVersion": "0.2.0"`) and
`examples/valid/accepted-without-minimum.response.json` (`null`). New invalid example:
`examples/invalid/malformed-minimum.response.json` (`"0.2"`). The request is unchanged.

`contracts/analyst-manager/status/v1/minimum-version.fixtures.json` lists comparison cases used by the platform and
Manager tests:

```json
[
  { "reported": "0.2.0", "minimum": "0.2.0", "below": false },
  { "reported": "0.1.9", "minimum": "0.2.0", "below": true },
  { "reported": "0.10.0", "minimum": "0.9.3", "below": false },
  { "reported": "1.0", "minimum": "0.1.0", "below": true },
  { "reported": "0.1.0", "minimum": null, "below": false }
]
```

## 2. Version refusal problem response

Returned by `POST /api/v1/analyst-managers/self/queue-credentials` and
`POST /api/v1/analysis/logical-jobs/{logicalJobId}/claims` when a minimum applies and the registration's effective
version is below it. No credential is issued and no attempt is created; an idempotent replay of a claim granted before
the minimum was raised still returns its existing grant.

```json
{
  "type": "urn:socalytics:problem:analyst-manager-version-below-minimum",
  "title": "The Analyst Manager version is below the stamp's minimum.",
  "status": 403,
  "code": "analyst-manager-version-below-minimum",
  "detail": "Update the Analyst Manager to the stamp's minimum version or later before it acquires new work.",
  "correlationId": "4bf92f3577b34da6a3ce929d0e0e4736",
  "minimumManagerVersion": "0.2.0"
}
```

Both operations document `403` already; the OpenAPI snapshot gains no new status code. Lease renewals, completions,
result grants, upstream reads, registration and status exchanges, queue depth, and unregistration never return it.

## 3. Club Admin fleet read — registration list and detail

`GET /api/v1/analyst-manager-registrations` and `GET /api/v1/analyst-manager-registrations/{registrationId}` for Club
Admins: each registration object gains two properties next to `status` and `connectivity`, serialized under the same
rule (only with fleet operations data; never in the Registrar's view of their own submissions or in decision
responses):

```json
"minimumManagerVersion": "0.2.0",
"belowMinimumVersion": true
```

`minimumManagerVersion` is `null` when no minimum applies, and `belowMinimumVersion` is then `false`. Pending requests
are evaluated from the version claimed at pairing. The committed OpenAPI snapshot
`src/platform/SocAlytics.Platform.Api/openapi/v1.json` is refreshed and the Web UI's Kiota client regenerated.

## 4. Manager local control — `status` result

`src/analysts/manager/contracts/local-control.schema.json`: the `status` result gains an optional object, present only
while the Manager is below the minimum:

```json
"update": {
  "required": true,
  "managerVersion": "0.1.0",
  "minimumVersion": "0.2.0"
}
```

CLI plain-text `status` adds the line `Update required: Manager 0.1.0 is below the stamp minimum 0.2.0.`; `--json`
prints the object above. The tray status window shows the same sentence and the tray menu a disabled line
`Update required (minimum 0.2.0)`.

## 5. Platform configuration

| Key | Default | Meaning |
| --- | --- | --- |
| `Registry:ManagerVersion:Minimum` | empty | Oldest Analyst Manager version that may acquire new work; empty means none. Only explicit non-production values are used in Development and Test. |
