# Contracts: Analyst Manager Update Feed

| Contract | Owner | Authoritative location (created during implementation) |
| --- | --- | --- |
| [Update offer operation](update-offer.md) | Registry (control plane) and Analyst Manager | `contracts/analyst-manager/update-offer/v1/update-offer.schema.json` with `examples/valid` and `examples/invalid`, version `1.0.0`; catalogued in `contracts/README.md`; operation in the OpenAPI snapshot |
| Local-control status `updateAvailable` | Analyst Manager | optional property added to `src/analysts/manager/contracts/local-control.schema.json` (additive) |

The authoritative schemas are authored in the repository-root `contracts/` folder and validated by
`src/platform/Tests/SocAlytics.Platform.Contracts.Tests` and the Manager tests; this folder only describes them.
