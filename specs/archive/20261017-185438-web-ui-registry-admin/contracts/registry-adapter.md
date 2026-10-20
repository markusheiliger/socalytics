# Contract: Registry Adapter

`src/clients/web/src/platform/adapters/registryAdapter.ts` is the only Web UI
code that calls the Registry operations. It uses the generated Kiota client,
the helpers in `platform/adapters/kiota.ts` (`sendFor`, `withJson`,
`withIfMatch`, and the new `sendWithMetadataFor`), and returns the models of
[data-model.md](../data-model.md). Screens use it through TanStack Query hooks
in `src/features/registry/registryKeys.ts`; nothing is persisted.

## Interface

```ts
export interface RegistryAdapter {
  listImages(filter?: { capabilityId?: string }): Promise<ImageSummary[]>;
  getImage(digest: string): Promise<CapabilityDeclaration>;
  registerImage(reference: string): Promise<{ declaration: CapabilityDeclaration; created: boolean }>;

  listProfiles(filter?: { state?: string; capabilityId?: string }): Promise<ProfileVersion[]>;
  getProfile(profileId: string, version: number): Promise<ProfileVersion>;           // rowVersion from ETag
  createProfile(draft: ProfileDraft): Promise<{ profile: ProfileVersion; created: boolean }>;
  approveProfile(profileId: string, version: number, rowVersion: number): Promise<ProfileVersion>;
  retireProfile(profileId: string, version: number, rowVersion: number): Promise<ProfileVersion>;

  listWorkflows(): Promise<Omit<WorkflowSummary, 'isDefault'>[]>;                  // isDefault derived from getDefault
  getWorkflow(workflowId: string, workflowVersion: string): Promise<WorkflowVersion>;
  defineWorkflow(draft: WorkflowDraft): Promise<WorkflowVersion>;

  getDefault(): Promise<DefaultWorkflow | null>;                                      // 404 → null
  setDefault(target: { workflowId: string; workflowVersion: string }, rowVersion?: number): Promise<DefaultWorkflow>;
}
```

`rowVersion` is sent as `If-Match: "<n>"`; `setDefault` omits `If-Match` only
when no default was loaded.

## Operations used

| Method | Operation | Request | Success |
| --- | --- | --- | --- |
| `listImages` | `listAnalystImages` | `capabilityId?` | `items[]` |
| `getImage` | `getAnalystImage` | digest | declaration |
| `registerImage` | `registerAnalystImage` | `{ reference }` | `201` created, `200` already registered |
| `listProfiles` | `listAnalystProfiles` | `state?`, `capabilityId?` | `items[]` (no `ETag`) |
| `getProfile` | `getAnalystProfile` | id, version | profile + `ETag` |
| `createProfile` | `createAnalystProfile` | `{ profileId, imageDigest, content }` | `201` created draft, `200` existing draft or approved version; `ETag` |
| `approveProfile` | `approveAnalystProfile` | `If-Match` | profile + `ETag` |
| `retireProfile` | `retireAnalystProfile` | `If-Match` | profile + `ETag` |
| `listWorkflows` | `listWorkflows` | – | `items[]` |
| `getWorkflow` | `getWorkflow` | id, version | definition |
| `defineWorkflow` | `defineWorkflow` | `{ workflowId, workflowVersion, requestedCapabilities, completionPolicy, segmentationPolicy, nodes[{ nodeKey, profileId, profileVersion }], edges[{ upstreamNodeKey, dependentNodeKey, kind, predicate? }] }` | `201` definition |
| `getDefault` | `getDefaultWorkflow` | – | default + `ETag`; `404` none |
| `setDefault` | `setDefaultWorkflow` | `{ workflowId, workflowVersion }`, `If-Match` when a default was loaded | default + `ETag` |

No other platform operation is called, except the existing member list
(`memberAdapter.list`) to resolve member names (research.md § R6).

## Errors

The adapter throws `PlatformFailure` for shared failures (session ended, not
permitted, unreachable, unexpected, incompatible) through `toPlatformFailure`,
and `RegistryRefusalError` (carrying a `RegistryRefusal`) or
`RegistryChangedError` for the Registry outcomes of
[research.md § R4](../research.md#r4-refusals-conflicts-and-problem-mapping).
Unknown problem codes on the Registry statuses become a `RegistryRefusal` with
the raw code. A response that lacks a required field (identity, digest, state)
throws `PlatformFailure('incompatible')`, as the other adapters do.

## Response metadata

`sendWithMetadataFor(api, info, factory)` adds a `ResponseMetadataOption` to
the request and returns `{ body, status, etag }`; the
`ResponseMetadataHandler` middleware (registered in `platform/client.ts` before
the default middlewares) fills the option from the fetch `Response`. The
`ETag` value `"<n>"` is parsed to a number; any other form yields
`rowVersion: undefined`, and a transition without a `rowVersion` is not
offered.
