import type { Parsable, ParsableFactory } from '@microsoft/kiota-abstractions';
import {
  createAnalystImageDtoFromDiscriminatorValue,
  createAnalystImageListDtoFromDiscriminatorValue,
  createAnalystProfileDtoFromDiscriminatorValue,
  createAnalystProfileListDtoFromDiscriminatorValue,
  createDefaultWorkflowDtoFromDiscriminatorValue,
  createRegistryWorkflowDefinitionDtoFromDiscriminatorValue,
  createWorkflowListDtoFromDiscriminatorValue,
  type AnalystImageDto,
  type AnalystImageListDto,
  type AnalystImageListItemDto,
  type AnalystProfileContentDto,
  type AnalystProfileDto,
  type AnalystProfileListDto,
  type DefaultWorkflowDto,
  type RegistryWorkflowDefinitionDto,
  type WorkflowEdgeDto,
  type WorkflowListDto,
  type WorkflowListItemDto,
} from '../../generated/platform-api/models';
import { PlatformFailure } from '../failure';
import {
  RegistryChangedError,
  RegistryRefusalError,
  type CapabilityDeclaration,
  type DeclarationDeterminism,
  type DeclarationRequirements,
  type DeclarationRuntime,
  type DeclarationUpstream,
  type DefaultWorkflow,
  type EdgePredicate,
  type ImageSummary,
  type MemberRef,
  type ProfileContent,
  type ProfileDraft,
  type ProfileVersion,
  type RegistryOperation,
  type WorkflowDraft,
  type WorkflowEdge,
  type WorkflowSummary,
  type WorkflowVersion,
} from '../models/registry';
import { sendFor, sendWithMetadataFor, withIfMatch, type PlatformApi } from './kiota';

export interface RegistryAdapter {
  listImages(filter?: { capabilityId?: string }): Promise<ImageSummary[]>;
  getImage(digest: string): Promise<CapabilityDeclaration>;
  registerImage(reference: string): Promise<{ declaration: CapabilityDeclaration; created: boolean }>;

  listProfiles(filter?: { state?: string; capabilityId?: string }): Promise<ProfileVersion[]>;
  getProfile(profileId: string, version: number): Promise<ProfileVersion>;
  createProfile(draft: ProfileDraft): Promise<{ profile: ProfileVersion; created: boolean }>;
  approveProfile(profileId: string, version: number, rowVersion: number): Promise<ProfileVersion>;
  retireProfile(profileId: string, version: number, rowVersion: number): Promise<ProfileVersion>;

  listWorkflows(): Promise<Omit<WorkflowSummary, 'isDefault'>[]>;
  getWorkflow(workflowId: string, workflowVersion: string): Promise<WorkflowVersion>;
  defineWorkflow(draft: WorkflowDraft): Promise<WorkflowVersion>;

  getDefault(): Promise<DefaultWorkflow | null>;
  setDefault(target: { workflowId: string; workflowVersion: string }, rowVersion?: number): Promise<DefaultWorkflow>;
}

const incompatible = () => new PlatformFailure('incompatible');
const EMPTY_GUID = '00000000-0000-0000-0000-000000000000';

function text(value: unknown): string | undefined {
  return typeof value === 'string' && value.length > 0 ? value : undefined;
}

function required(value: unknown): string {
  const result = text(value);
  if (result === undefined) throw incompatible();
  return result;
}

function requiredNumber(value: unknown): number {
  if (typeof value !== 'number' || !Number.isFinite(value)) throw incompatible();
  return value;
}

function requiredDate(value: unknown): Date {
  if (!(value instanceof Date) || Number.isNaN(value.getTime())) throw incompatible();
  return value;
}

function optionalDate(value: unknown): Date | undefined {
  return value instanceof Date && !Number.isNaN(value.getTime()) ? value : undefined;
}

function memberRef(value: unknown): MemberRef {
  const id = typeof value === 'string' ? value : '';
  return id === '' || id === EMPTY_GUID ? { id: '', kind: 'seed' } : { id, kind: 'member' };
}

function optionalMemberRef(value: unknown): MemberRef | undefined {
  return typeof value === 'string' ? memberRef(value) : undefined;
}

function strings(value: unknown): string[] {
  return Array.isArray(value) ? value.filter((entry): entry is string => typeof entry === 'string') : [];
}

// Kiota keeps unmodelled objects as `additionalData` and unwraps untyped nodes through `getValue`.
function plain(value: unknown): unknown {
  if (value === null || value === undefined) return undefined;
  if (Array.isArray(value)) return value.map(plain);
  if (typeof value !== 'object') return value;
  const record = value as { additionalData?: unknown; getValue?: () => unknown };
  if (typeof record.getValue === 'function') return plain(record.getValue());
  if (record.additionalData !== undefined && typeof record.additionalData === 'object') return plain(record.additionalData);
  if (value instanceof Date) return value;
  return Object.fromEntries(Object.entries(value).map(([key, entry]) => [key, plain(entry)]));
}

function stringMap(holder: unknown): Record<string, string> {
  const map = plain(holder);
  if (typeof map !== 'object' || map === null || Array.isArray(map)) return {};
  return Object.fromEntries(Object.entries(map).filter((entry): entry is [string, string] => typeof entry[1] === 'string'));
}

function asRecord(value: unknown): Record<string, unknown> | undefined {
  return typeof value === 'object' && value !== null && !Array.isArray(value) ? (value as Record<string, unknown>) : undefined;
}

function optionalNumber(value: unknown): number | undefined {
  return typeof value === 'number' && Number.isFinite(value) ? value : undefined;
}

function optionalBoolean(value: unknown): boolean | undefined {
  return typeof value === 'boolean' ? value : undefined;
}

function manifestUpstream(value: unknown): DeclarationUpstream[] | undefined {
  if (!Array.isArray(value)) return undefined;
  const entries = value.map((entry) => {
    const item = asRecord(entry);
    const capabilityId = text(item?.capabilityId);
    const capabilityVersion = text(item?.capabilityVersion);
    const schemaVersion = text(item?.schemaVersion);
    const kind = text(item?.kind);
    return capabilityId && capabilityVersion && schemaVersion && kind
      ? { capabilityId, capabilityVersion, schemaVersion, kind }
      : undefined;
  });
  return entries.every((entry) => entry !== undefined) ? (entries as DeclarationUpstream[]) : undefined;
}

function manifestRuntime(value: unknown): DeclarationRuntime | undefined {
  const runtime = asRecord(value);
  if (!runtime || !Array.isArray(runtime.constraints) || !Array.isArray(runtime.accelerators)) return undefined;
  return { constraints: strings(runtime.constraints), accelerators: strings(runtime.accelerators) };
}

function manifestRequirements(value: unknown): DeclarationRequirements | undefined {
  const requirements = asRecord(value);
  if (!requirements) return undefined;
  return {
    minMaxAttempts: optionalNumber(requirements.minMaxAttempts),
    minLeaseSeconds: optionalNumber(requirements.minLeaseSeconds),
    minStaleTimeoutSeconds: optionalNumber(requirements.minStaleTimeoutSeconds),
    minExecutionTimeoutSeconds: optionalNumber(requirements.minExecutionTimeoutSeconds),
  };
}

function manifestDeterminism(value: unknown): DeclarationDeterminism | undefined {
  const determinism = asRecord(value);
  if (!determinism) return undefined;
  return { deterministic: optionalBoolean(determinism.deterministic), idempotent: optionalBoolean(determinism.idempotent) };
}

function manifestResources(value: unknown): Record<string, string> | undefined {
  const resources = asRecord(value);
  if (!resources) return undefined;
  return Object.fromEntries(Object.entries(resources).filter((entry): entry is [string, string] => typeof entry[1] === 'string'));
}

export function toImageSummary(dto: AnalystImageListItemDto | undefined): ImageSummary {
  if (!dto) throw incompatible();
  return {
    imageDigest: required(dto.imageDigest),
    capabilityId: required(dto.capabilityId),
    capabilityVersion: required(dto.capabilityVersion),
    registeredAt: requiredDate(dto.registeredAt),
  };
}

export function toDeclaration(dto: AnalystImageDto | undefined): CapabilityDeclaration {
  if (!dto || !dto.capability) throw incompatible();
  const manifest = asRecord(plain(dto.manifest));
  const output = asRecord(manifest?.output);
  return {
    imageDigest: required(dto.imageDigest),
    repository: dto.repository ?? '',
    manifestDigest: required(dto.manifestDigest),
    capability: {
      id: required(dto.capability.id),
      version: required(dto.capability.version),
      tier: dto.capability.tier ?? '',
      executionScope: dto.capability.executionScope ?? '',
    },
    modelDigest: required(dto.modelDigest),
    source: dto.source ?? '',
    registeredAt: requiredDate(dto.registeredAt),
    registeredBy: memberRef(dto.registeredBy),
    upstream: manifestUpstream(manifest?.upstream),
    videoAccess: text(manifest?.videoAccess),
    outputSchemaVersion: text(output?.schemaVersion),
    compatibleProfiles: Array.isArray(manifest?.compatibleProfiles) ? strings(manifest.compatibleProfiles) : undefined,
    runtime: manifestRuntime(manifest?.runtime),
    resources: manifestResources(manifest?.resources),
    requirements: manifestRequirements(manifest?.requirements),
    determinism: manifestDeterminism(manifest?.determinism),
    manifestText: manifest ? JSON.stringify(manifest, null, 2) : undefined,
  };
}

function toContent(dto: AnalystProfileContentDto | null | undefined): ProfileContent {
  if (!dto) throw incompatible();
  return {
    capabilityId: required(dto.capabilityId),
    capabilityVersion: required(dto.capabilityVersion),
    modelDigest: required(dto.modelDigest),
    runtimeConstraints: strings(dto.runtimeConstraints),
    accelerators: strings(dto.accelerators),
    resources: stringMap(dto.resources),
    maxAttempts: requiredNumber(dto.maxAttempts),
    leaseSeconds: requiredNumber(dto.leaseSeconds),
    staleTimeoutSeconds: requiredNumber(dto.staleTimeoutSeconds),
    executionTimeoutSeconds: requiredNumber(dto.executionTimeoutSeconds),
    videoPolicy: dto.videoPolicy ?? '',
    inputSchemaVersions: stringMap(dto.inputSchemaVersions),
    outputSchemaVersion: dto.outputSchemaVersion ?? '',
  };
}

// The ETag `"<n>"` carries the row version; any other form yields none.
export function rowVersionOf(etag: string | undefined): number | undefined {
  const match = etag === undefined ? null : /^"(\d+)"$/.exec(etag.trim());
  return match ? Number(match[1]) : undefined;
}

export function toProfile(dto: AnalystProfileDto | undefined, etag?: string): ProfileVersion {
  if (!dto) throw incompatible();
  return {
    profileId: required(dto.profileId),
    version: requiredNumber(dto.version),
    rowVersion: rowVersionOf(etag),
    profileDigest: required(dto.profileDigest),
    imageDigest: required(dto.imageDigest),
    state: required(dto.state),
    content: toContent(dto.content),
    createdAt: requiredDate(dto.createdAt),
    createdBy: memberRef(dto.createdBy),
    approvedAt: optionalDate(dto.approvedAt),
    approvedBy: optionalMemberRef(dto.approvedBy),
    retiredAt: optionalDate(dto.retiredAt),
    retiredBy: optionalMemberRef(dto.retiredBy),
  };
}

function toWorkflowSummary(dto: WorkflowListItemDto | undefined): Omit<WorkflowSummary, 'isDefault'> {
  if (!dto) throw incompatible();
  return {
    workflowId: required(dto.workflowId),
    workflowVersion: required(dto.workflowVersion),
    definitionDigest: required(dto.definitionDigest),
    createdAt: requiredDate(dto.createdAt),
  };
}

function toEdge(dto: WorkflowEdgeDto | undefined): WorkflowEdge {
  if (!dto) throw incompatible();
  const edge: WorkflowEdge = {
    upstreamNodeKey: required(dto.upstreamNodeKey),
    dependentNodeKey: required(dto.dependentNodeKey),
    kind: required(dto.kind),
  };
  if (dto.predicate) {
    const predicate: EdgePredicate = { kind: required(dto.predicate.kind) };
    if (dto.predicate.capabilityId) predicate.capabilityId = dto.predicate.capabilityId;
    if (dto.predicate.nodeKey) predicate.nodeKey = dto.predicate.nodeKey;
    edge.predicate = predicate;
  }
  return edge;
}

export function toWorkflow(dto: RegistryWorkflowDefinitionDto | undefined): WorkflowVersion {
  const definition = dto?.definition;
  if (!dto || !definition || !definition.segmentationPolicy) throw incompatible();
  return {
    workflowId: required(dto.workflowId),
    workflowVersion: required(dto.workflowVersion),
    definitionDigest: required(dto.definitionDigest),
    requestedCapabilities: strings(definition.requestedCapabilities),
    completionPolicy: required(definition.completionPolicy),
    segmentationPolicy: {
      version: requiredNumber(definition.segmentationPolicy.version),
      durationSeconds: requiredNumber(definition.segmentationPolicy.durationSeconds),
      digest: required(definition.segmentationPolicy.digest),
    },
    nodes: (definition.nodes ?? [])
      .map((node) => ({
        nodeKey: required(node.nodeKey),
        profileId: required(node.profileId),
        profileVersion: requiredNumber(node.profileVersion),
        profileDigest: required(node.profileDigest),
      }))
      .sort((a, b) => (a.nodeKey < b.nodeKey ? -1 : a.nodeKey > b.nodeKey ? 1 : 0)),
    edges: (definition.edges ?? []).map(toEdge),
    createdAt: requiredDate(dto.createdAt),
    createdBy: memberRef(dto.createdBy),
  };
}

export function toDefault(dto: DefaultWorkflowDto | undefined, etag?: string): DefaultWorkflow {
  if (!dto) throw incompatible();
  return {
    workflowId: required(dto.workflowId),
    workflowVersion: required(dto.workflowVersion),
    changedAt: requiredDate(dto.changedAt),
    changedBy: memberRef(dto.changedBy),
    rowVersion: rowVersionOf(etag),
  };
}

interface Translation {
  operation: RegistryOperation;
  // Statuses whose problem code is a Registry refusal.
  refusalStatuses: number[];
  // Statuses that always mean "changed since loaded".
  changedStatuses?: number[];
  // Server-error statuses that are refusals only with a known code.
  knownServerCodes?: string[];
}

const IMAGE_REFUSAL_CODES = [
  'reference-not-digest',
  'reference-not-allowed',
  'image-not-found',
  'registry-unavailable',
  'digest-mismatch',
  'manifest-missing',
  'manifest-invalid',
  'labels-mismatch',
  'unsupported-media-type',
];

const translations = {
  image: { operation: 'imageRefused', refusalStatuses: [400, 404, 422], knownServerCodes: IMAGE_REFUSAL_CODES },
  profile: { operation: 'profileRefused', refusalStatuses: [400, 404, 409, 422] },
  transition: { operation: 'transitionRefused', refusalStatuses: [409], changedStatuses: [412, 428] },
  workflow: { operation: 'workflowRefused', refusalStatuses: [400, 409, 422] },
  default: { operation: 'defaultRefused', refusalStatuses: [400, 409, 422], changedStatuses: [412, 428] },
} satisfies Record<string, Translation>;

// Registry refusals are told apart from the shared failures by status and problem code (research.md § R4).
function translate(failure: PlatformFailure, translation: Translation): Error {
  const status = failure.status;
  if (status === undefined) return failure;
  if (translation.changedStatuses?.includes(status)) return new RegistryChangedError();
  const code = failure.code;
  const refused =
    code !== undefined &&
    (translation.refusalStatuses.includes(status) ||
      (status >= 500 && (translation.knownServerCodes?.includes(code) ?? false)));
  if (!refused) return failure;
  return new RegistryRefusalError({
    operation: translation.operation,
    code,
    details: failure.fieldErrors.map((entry) => ({ field: entry.field, code: entry.code })),
    retryable: translation.operation === 'imageRefused' && code === 'registry-unavailable',
  });
}

async function guarded<T>(translation: Translation, run: () => Promise<T>): Promise<T> {
  try {
    return await run();
  } catch (error) {
    throw error instanceof PlatformFailure ? translate(error, translation) : error;
  }
}

function resourcesBody(values: Record<string, string>) {
  return { additionalData: values };
}

function predicateBody(predicate: EdgePredicate | undefined) {
  return predicate
    ? { kind: predicate.kind, capabilityId: predicate.capabilityId, nodeKey: predicate.nodeKey }
    : undefined;
}

export function createRegistryAdapter(api: PlatformApi): RegistryAdapter {
  const registry = api.client.api.v1.registry;
  const profileVersion = (profileId: string, version: number) => registry.analystProfiles.byProfileId(profileId).versions.byVersion(version);
  const send = <T extends Parsable>(info: Parameters<typeof sendFor>[1], factory: ParsableFactory<T>) => sendFor(api, info, factory);
  const sendMeta = <T extends Parsable>(info: Parameters<typeof sendFor>[1], factory: ParsableFactory<T>) =>
    sendWithMetadataFor(api, info, factory);

  const transition = (info: Parameters<typeof sendFor>[1], rowVersion: number) =>
    guarded(translations.transition, async () => {
      const { body, etag } = await sendMeta(withIfMatch(info, rowVersion), createAnalystProfileDtoFromDiscriminatorValue);
      return toProfile(body, etag);
    });

  return {
    listImages: async (filter) => {
      const list = await send(
        registry.analystImages.toGetRequestInformation({ queryParameters: { capabilityId: filter?.capabilityId } }),
        createAnalystImageListDtoFromDiscriminatorValue as ParsableFactory<AnalystImageListDto>,
      );
      if (!Array.isArray(list.items)) throw incompatible();
      return list.items.map(toImageSummary);
    },
    getImage: async (digest) =>
      toDeclaration(await send(registry.analystImages.byDigest(digest).toGetRequestInformation(), createAnalystImageDtoFromDiscriminatorValue)),
    registerImage: (reference) =>
      guarded(translations.image, async () => {
        const { body, status } = await sendMeta(
          registry.analystImages.toPostRequestInformation({ reference }),
          createAnalystImageDtoFromDiscriminatorValue,
        );
        return { declaration: toDeclaration(body), created: status === 201 };
      }),

    listProfiles: async (filter) => {
      const list = await send(
        registry.analystProfiles.toGetRequestInformation({
          queryParameters: { state: filter?.state, capabilityId: filter?.capabilityId },
        }),
        createAnalystProfileListDtoFromDiscriminatorValue as ParsableFactory<AnalystProfileListDto>,
      );
      if (!Array.isArray(list.items)) throw incompatible();
      return list.items.map((item) => toProfile(item));
    },
    getProfile: async (profileId, version) => {
      const { body, etag } = await sendMeta(profileVersion(profileId, version).toGetRequestInformation(), createAnalystProfileDtoFromDiscriminatorValue);
      return toProfile(body, etag);
    },
    createProfile: (draft) =>
      guarded(translations.profile, async () => {
        const { content } = draft;
        const { body, status, etag } = await sendMeta(
          registry.analystProfiles.toPostRequestInformation({
            profileId: draft.profileId,
            imageDigest: draft.imageDigest,
            content: {
              ...content,
              resources: resourcesBody(content.resources),
              inputSchemaVersions: resourcesBody(content.inputSchemaVersions),
            },
          }),
          createAnalystProfileDtoFromDiscriminatorValue,
        );
        return { profile: toProfile(body, etag), created: status === 201 };
      }),
    approveProfile: (profileId, version, rowVersion) =>
      transition(profileVersion(profileId, version).approval.toPostRequestInformation(), rowVersion),
    retireProfile: (profileId, version, rowVersion) =>
      transition(profileVersion(profileId, version).retirement.toPostRequestInformation(), rowVersion),

    listWorkflows: async () => {
      const list = await send(registry.workflows.toGetRequestInformation(), createWorkflowListDtoFromDiscriminatorValue as ParsableFactory<WorkflowListDto>);
      if (!Array.isArray(list.items)) throw incompatible();
      return list.items.map(toWorkflowSummary);
    },
    getWorkflow: async (workflowId, workflowVersion) =>
      toWorkflow(
        await send(
          registry.workflows.byWorkflowId(workflowId).versions.byVersion(workflowVersion).toGetRequestInformation(),
          createRegistryWorkflowDefinitionDtoFromDiscriminatorValue,
        ),
      ),
    defineWorkflow: (draft) =>
      guarded(translations.workflow, async () =>
        toWorkflow(
          await send(
            registry.workflows.toPostRequestInformation({
              workflowId: draft.workflowId,
              workflowVersion: draft.workflowVersion,
              requestedCapabilities: draft.requestedCapabilities,
              completionPolicy: draft.completionPolicy,
              segmentationPolicy: draft.segmentationPolicy,
              nodes: draft.nodes.map((node) => ({ nodeKey: node.nodeKey, profileId: node.profileId, profileVersion: node.profileVersion })),
              edges: draft.edges.map((edge) => ({
                upstreamNodeKey: edge.upstreamNodeKey,
                dependentNodeKey: edge.dependentNodeKey,
                kind: edge.kind,
                predicate: predicateBody(edge.predicate),
              })),
            }),
            createRegistryWorkflowDefinitionDtoFromDiscriminatorValue,
          ),
        ),
      ),

    getDefault: async () => {
      try {
        const { body, etag } = await sendMeta(registry.defaultWorkflow.toGetRequestInformation(), createDefaultWorkflowDtoFromDiscriminatorValue);
        return toDefault(body, etag);
      } catch (error) {
        if (error instanceof PlatformFailure && error.status === 404) return null;
        throw error;
      }
    },
    setDefault: (target, rowVersion) =>
      guarded(translations.default, async () => {
        const info = registry.defaultWorkflow.toPutRequestInformation({
          workflowId: target.workflowId,
          workflowVersion: target.workflowVersion,
        });
        const { body, etag } = await sendMeta(
          rowVersion === undefined ? info : withIfMatch(info, rowVersion),
          createDefaultWorkflowDtoFromDiscriminatorValue,
        );
        return toDefault(body, etag);
      }),
  };
}
