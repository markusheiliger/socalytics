import type { Parsable, ParsableFactory, RequestInformation } from '@microsoft/kiota-abstractions';
import {
  createAgentCatalogArtifactDtoFromDiscriminatorValue,
  createAgentCatalogArtifactListDtoFromDiscriminatorValue,
  createAgentEnablementDtoFromDiscriminatorValue,
  createAgentEnablementListDtoFromDiscriminatorValue,
  createAgentVersionDtoFromDiscriminatorValue,
  createAgentVersionListDtoFromDiscriminatorValue,
  createAvailableAgentsDtoFromDiscriminatorValue,
  createCatalogBaselineDtoFromDiscriminatorValue,
  type AgentCatalogArtifactDto,
  type AgentCatalogArtifactListDto,
  type AgentCatalogArtifactSummaryDto,
  type AgentEnablementDto,
  type AgentEnablementListDto,
  type AgentVersionDto,
  type AgentVersionListDto,
  type AgentVersionSummaryDto,
  type AvailableAgentsDto,
  type CatalogBaselineDto,
  type ProfileDistanceDto,
} from '../../generated/platform-api/models';
import { violationLocation } from '../../features/agent-catalog/violationLocation';
import { memberRefFor } from '../../features/registry/memberNames';
import { PlatformFailure, toPlatformFailure } from '../failure';
import { ResponseMetadataOption } from '../middleware/responseMetadata';
import type {
  AgentVersion,
  AgentVersionDocument,
  AgentVersionSummary,
  ArtifactKind,
  ArtifactRecord,
  ArtifactRef,
  ArtifactSummary,
  CatalogBaseline,
  CatalogOutcome,
  CatalogViolation,
  Distance,
  DocumentOf,
  Enablement,
  MemberRef,
} from '../models/agentCatalog';
import { createMemberAdapter } from './memberAdapter';
import { errorMappings, withJson, type PlatformApi } from './kiota';

export type EnablementRefused = Extract<CatalogOutcome<never>, { kind: 'enablementRefused' | 'changed' }>;
export type TransitionRefused = Extract<CatalogOutcome<never>, { kind: 'transitionRefused' | 'changed' }>;
export type RegistrationOutcome<T> = Exclude<CatalogOutcome<T>, { kind: 'transitionRefused' | 'enablementRefused' | 'changed' }>;

export interface AgentCatalogAdapter {
  getBaseline(): Promise<CatalogBaseline>;
  listAvailableAgents(): Promise<{ agentId: string; version: string }[]>;
  listEnablements(): Promise<Enablement[]>;
  setEnablement(agentId: string, version: string | null, etag: string): Promise<Enablement | EnablementRefused>;

  listAgentVersions(filter?: { agentId?: string; state?: string }): Promise<AgentVersionSummary[]>;
  getAgentVersion(agentId: string, version: string): Promise<AgentVersion>;
  registerAgentVersion(version: string, document: AgentVersionDocument & Record<string, unknown>): Promise<RegistrationOutcome<AgentVersion>>;
  approveAgentVersion(
    agentId: string,
    version: string,
    etag: string,
    options?: { differentiationReviewAcknowledged?: boolean },
  ): Promise<AgentVersion | TransitionRefused>;
  retireAgentVersion(agentId: string, version: string, etag: string): Promise<AgentVersion | TransitionRefused>;

  listArtifacts(kind: ArtifactKind, filter?: { id?: string }): Promise<ArtifactSummary[]>;
  getArtifact<K extends ArtifactKind>(kind: K, id: string, version: string): Promise<ArtifactRecord<K>>;
  registerArtifact<K extends ArtifactKind>(kind: K, document: DocumentOf<K> & Record<string, unknown>): Promise<RegistrationOutcome<ArtifactRecord<K>>>;
}

const incompatible = () => new PlatformFailure('incompatible');
const SEED_ID = 'system:seed';
const VALIDATION_CODE = 'agent-catalog-validation-failed';
const VERSION_EXISTS_CODE = 'agent-catalog-version-exists';

const ARTIFACT_KEYS: ReadonlyArray<[string, ArtifactKind]> = [
  ['promptTemplate', 'prompt-template'],
  ['modelSelection', 'model-selection'],
  ['toolPolicy', 'tool-policy'],
  ['safetyPolicy', 'safety-policy'],
  ['philosophyProfile', 'philosophy-profile'],
  ['modifierPolicy', 'modifier-policy'],
];

function text(value: unknown): string | undefined {
  return typeof value === 'string' && value.length > 0 ? value : undefined;
}

function required(value: unknown): string {
  const result = text(value);
  if (result === undefined) throw incompatible();
  return result;
}

function requiredDate(value: unknown): Date {
  if (!(value instanceof Date) || Number.isNaN(value.getTime())) throw incompatible();
  return value;
}

function optionalDate(value: unknown): Date | undefined {
  return value instanceof Date && !Number.isNaN(value.getTime()) ? value : undefined;
}

function strings(value: unknown): string[] {
  return Array.isArray(value) ? value.filter((entry): entry is string => typeof entry === 'string') : [];
}

function asRecord(value: unknown): Record<string, unknown> | undefined {
  return typeof value === 'object' && value !== null && !Array.isArray(value) ? (value as Record<string, unknown>) : undefined;
}

// Kiota keeps fields it does not model as `additionalData` and wraps untyped nodes; this returns plain data with every field.
function flat(value: unknown): unknown {
  if (value === null) return null;
  if (value === undefined || value instanceof Date) return value;
  if (Array.isArray(value)) return value.map(flat);
  if (typeof value !== 'object') return value;
  const holder = value as { additionalData?: unknown; getValue?: () => unknown };
  if (typeof holder.getValue === 'function') return flat(holder.getValue());
  const result: Record<string, unknown> = {};
  for (const [key, entry] of Object.entries(value)) {
    if (key === 'additionalData') continue;
    const converted = flat(entry);
    if (converted !== undefined) result[key] = converted;
  }
  const extra = asRecord(holder.additionalData);
  if (extra) {
    for (const [key, entry] of Object.entries(extra)) {
      const converted = flat(entry);
      if (converted !== undefined && !(key in result)) result[key] = converted;
    }
  }
  return result;
}

type MemberLookup = (id: string | null | undefined) => MemberRef;

async function memberLookup(api: PlatformApi, ids: Array<string | null | undefined>): Promise<MemberLookup> {
  let members: Awaited<ReturnType<ReturnType<typeof createMemberAdapter>['list']>> = [];
  if (ids.some((id) => id && id !== SEED_ID)) {
    try {
      members = await createMemberAdapter(api).list();
    } catch {
      members = [];
    }
  }
  return (id) => {
    if (id === SEED_ID) return { id: SEED_ID, kind: 'seed' };
    return id ? memberRefFor(id, members) : { id: '', kind: 'unknown' };
  };
}

function singularKind(kind: string): string {
  if (kind.endsWith('ies')) return `${kind.slice(0, -3)}y`;
  return kind.endsWith('s') ? kind.slice(0, -1) : kind;
}

function pluralKind(kind: string): string {
  if (kind.endsWith('s')) return kind;
  return kind.endsWith('y') ? `${kind.slice(0, -1)}ies` : `${kind}s`;
}

function toDistances(value: ProfileDistanceDto[] | null | undefined): Distance[] | undefined {
  if (!Array.isArray(value)) return undefined;
  return value.map((entry) => {
    if (typeof entry.distance !== 'number') throw incompatible();
    return {
      agentId: required(entry.agentId),
      agentVersion: required(entry.agentVersion),
      profileId: required(entry.profileId),
      profileVersion: required(entry.profileVersion),
      distance: entry.distance,
      reviewRequired: entry.reviewRequired === true,
    };
  });
}

function toBaseline(dto: CatalogBaselineDto | undefined): CatalogBaseline {
  if (!dto) throw incompatible();
  return {
    version: required(dto.schemaVersion),
    tools: strings(dto.toolAllowlist),
    agents: (dto.agents ?? []).map((agent) => ({
      agentId: required(agent.agentId),
      group: required(agent.group),
      domain: agent.domain ?? '',
      displayName: required(agent.displayName),
      toolBaseline: strings(agent.permittedTools),
    })),
  };
}

function toEnablement(dto: AgentEnablementDto | undefined, lookup: MemberLookup, headerEtag?: string): Enablement {
  if (!dto) throw incompatible();
  return {
    agentId: required(dto.agentId),
    enabledVersion: text(dto.enabledVersion),
    changedBy: text(dto.changedBy) ? lookup(dto.changedBy) : undefined,
    changedAt: optionalDate(dto.changedAt),
    etag: required(headerEtag ?? dto.etag),
  };
}

function toVersionSummary(dto: AgentVersionSummaryDto | undefined): AgentVersionSummary {
  if (!dto) throw incompatible();
  return {
    agentId: required(dto.agentId),
    version: required(dto.version),
    digest: required(dto.digest),
    group: required(dto.group),
    state: required(dto.state),
    enabled: dto.enabled === true,
    registeredAt: requiredDate(dto.registeredAt),
  };
}

function toArtifactRefs(artifacts: unknown): ArtifactRef[] {
  const record = asRecord(artifacts) ?? {};
  const refs: ArtifactRef[] = [];
  const add = (kind: string, value: unknown) => {
    const ref = asRecord(value);
    if (ref) refs.push({ kind, id: required(ref.id), version: required(ref.version), digest: required(ref.digest) });
  };
  for (const [key, kind] of ARTIFACT_KEYS) add(kind, record[key]);
  for (const [key, value] of Object.entries(record)) {
    if (!ARTIFACT_KEYS.some(([known]) => known === key)) add(key, value);
  }
  return refs;
}

function toAgentVersion(dto: AgentVersionDto | undefined, lookup: MemberLookup, etag: string | undefined): AgentVersion {
  if (!dto) throw incompatible();
  const document = asRecord(flat(dto.document)) ?? {};
  return {
    ...toVersionSummary(dto as AgentVersionSummaryDto),
    displayName: text(document.displayName) ?? '',
    disclosure: text(document.disclosure),
    artifacts: toArtifactRefs(document.artifacts),
    registeredBy: lookup(dto.registeredBy),
    approvedBy: text(dto.approvedBy) ? lookup(dto.approvedBy) : undefined,
    approvedAt: optionalDate(dto.approvedAt),
    selfApproval: dto.selfApproval === true,
    differentiationAcknowledged: dto.differentiationAcknowledged === true,
    retiredBy: text(dto.retiredBy) ? lookup(dto.retiredBy) : undefined,
    retiredAt: optionalDate(dto.retiredAt),
    distances: toDistances(dto.distances),
    etag: etag ?? '',
  };
}

function toArtifactSummary(dto: AgentCatalogArtifactSummaryDto | undefined, kind: string, lookup: MemberLookup): ArtifactSummary {
  if (!dto) throw incompatible();
  return {
    kind: text(dto.kind) ?? singularKind(kind),
    id: required(dto.id),
    version: required(dto.version),
    digest: required(dto.digest),
    registeredBy: lookup(dto.registeredBy),
    registeredAt: requiredDate(dto.registeredAt),
  };
}

function toArtifactRecord<K extends ArtifactKind>(dto: AgentCatalogArtifactDto | undefined, kind: K): ArtifactRecord<K> {
  if (!dto) throw incompatible();
  const recordKind = text(dto.kind) ?? singularKind(kind);
  const document = asRecord(flat(dto.document));
  if (!document) throw incompatible();
  const distances = toDistances(dto.distances);
  return {
    kind: recordKind,
    id: required(dto.id),
    version: required(dto.version),
    digest: required(dto.digest),
    registeredBy: { id: '', kind: 'unknown' },
    registeredAt: requiredDate(dto.registeredAt),
    // The generated polymorphic document cannot keep `kind`, so it is restored from the record.
    document: { ...document, kind: recordKind } as unknown as DocumentOf<K>,
    ...(distances ? { distances } : {}),
  };
}

// A failed exchange keeps the raw problem, because the generic failure drops `violations` and `reason`.
class CatalogProblem extends Error {
  readonly status: number | undefined;
  readonly code: string | undefined;
  readonly data: Record<string, unknown>;
  readonly failure: PlatformFailure;

  constructor(error: unknown) {
    super('Agent catalog request failed');
    const raw = error as { responseStatusCode?: unknown; additionalData?: unknown };
    this.status = typeof raw?.responseStatusCode === 'number' ? raw.responseStatusCode : undefined;
    this.data = asRecord(flat({ additionalData: raw?.additionalData })) ?? {};
    this.code = text(this.data.code);
    this.failure = toPlatformFailure(error);
  }
}

async function exchange<T extends Parsable>(api: PlatformApi, info: RequestInformation, factory: ParsableFactory<T>) {
  const option = new ResponseMetadataOption();
  info.addRequestOptions([option]);
  let body: T | undefined;
  try {
    body = await api.adapter.send(info, factory, errorMappings);
  } catch (error) {
    throw error instanceof PlatformFailure ? error : new CatalogProblem(error);
  }
  if (body === undefined) throw toPlatformFailure(undefined);
  return { body, status: option.status, etag: option.etag };
}

// Everything but the catalog's own refusals stays a shared failure.
async function failuresOnly<T>(run: () => Promise<T>): Promise<T> {
  try {
    return await run();
  } catch (error) {
    throw error instanceof CatalogProblem ? error.failure : error;
  }
}

async function refusable<T, R>(run: () => Promise<T>, refuse: (problem: CatalogProblem) => R | undefined): Promise<T | R> {
  try {
    return await run();
  } catch (error) {
    if (!(error instanceof CatalogProblem)) throw error;
    const refusal = refuse(error);
    if (refusal === undefined) throw error.failure;
    return refusal;
  }
}

const isStale = (problem: CatalogProblem) => problem.status === 412 || problem.status === 428;

function toViolations(value: unknown): CatalogViolation[] {
  if (!Array.isArray(value)) return [];
  return value.flatMap((entry): CatalogViolation[] => {
    const item = asRecord(entry);
    if (!item) return [];
    const pointer = typeof item.pointer === 'string' ? item.pointer : '';
    return [{ code: text(item.code) ?? 'unknown', pointer, detail: typeof item.detail === 'string' ? item.detail : '', location: violationLocation(pointer) }];
  });
}

function registrationRefusal(problem: CatalogProblem): { kind: 'validationFailed'; violations: CatalogViolation[] } | { kind: 'versionExists' } | undefined {
  if (problem.status === 400 && problem.code === VALIDATION_CODE) {
    return { kind: 'validationFailed', violations: toViolations(problem.data.violations) };
  }
  if (problem.status === 409 && problem.code === VERSION_EXISTS_CODE) return { kind: 'versionExists' };
  return undefined;
}

function transitionRefusal(problem: CatalogProblem): TransitionRefused | undefined {
  if (isStale(problem)) return { kind: 'changed' };
  if (problem.status === 409 && problem.code) return { kind: 'transitionRefused', code: problem.code };
  return undefined;
}

function enablementRefusal(problem: CatalogProblem): EnablementRefused | undefined {
  if (isStale(problem)) return { kind: 'changed' };
  if (problem.status === 404) return { kind: 'enablementRefused', code: problem.code ?? 'agent-not-found' };
  if (problem.status === 409 && problem.code) {
    const reason = text(problem.data.reason);
    return reason ? { kind: 'enablementRefused', code: problem.code, reason } : { kind: 'enablementRefused', code: problem.code };
  }
  return undefined;
}

function withIfMatchValue(info: RequestInformation, etag: string): RequestInformation {
  info.headers.add('If-Match', etag);
  return info;
}

export function createAgentCatalogAdapter(api: PlatformApi): AgentCatalogAdapter {
  const catalog = api.client.api.v1.agentCatalog;
  const versionItem = (agentId: string, version: string) => catalog.agentVersions.byAgentId(agentId).versions.byVersion(version);
  const artifactKind = (kind: string) => catalog.artifacts.byKind(pluralKind(kind));
  const read = <T extends Parsable>(info: RequestInformation, factory: ParsableFactory<T>) =>
    failuresOnly(() => exchange(api, info, factory));

  const transition = (info: RequestInformation, etag: string) =>
    refusable(async () => {
      const { body, etag: responseEtag } = await exchange(api, withIfMatchValue(info, etag), (createAgentVersionDtoFromDiscriminatorValue as ParsableFactory<AgentVersionDto>));
      const lookup = await memberLookup(api, [body.registeredBy, body.approvedBy, body.retiredBy]);
      return toAgentVersion(body, lookup, responseEtag);
    }, transitionRefusal);

  return {
    getBaseline: async () => toBaseline((await read(catalog.baseline.toGetRequestInformation(), (createCatalogBaselineDtoFromDiscriminatorValue as ParsableFactory<CatalogBaselineDto>))).body),

    listAvailableAgents: async () => {
      const { body } = await read(catalog.agents.toGetRequestInformation(), (createAvailableAgentsDtoFromDiscriminatorValue as ParsableFactory<AvailableAgentsDto>));
      const list = body as AvailableAgentsDto;
      if (!Array.isArray(list.agents)) throw incompatible();
      return list.agents.map((agent) => ({ agentId: required(agent.agentId), version: required(agent.enabledVersion) }));
    },

    listEnablements: async () => {
      const { body } = await read(
        catalog.enablements.toGetRequestInformation(),
        createAgentEnablementListDtoFromDiscriminatorValue as ParsableFactory<AgentEnablementListDto>,
      );
      if (!Array.isArray(body.items)) throw incompatible();
      const lookup = await memberLookup(api, body.items.map((item) => item.changedBy));
      return body.items.map((item) => toEnablement(item, lookup));
    },

    setEnablement: (agentId, version, etag) =>
      refusable(async () => {
        const info = withIfMatchValue(catalog.enablements.byAgentId(agentId).toPutRequestInformation({}), etag);
        // `null` disables, so the body is written as is instead of through the generated serializer.
        const { body, etag: responseEtag } = await exchange(api, withJson(info, { version }), (createAgentEnablementDtoFromDiscriminatorValue as ParsableFactory<AgentEnablementDto>));
        const lookup = await memberLookup(api, [body.changedBy]);
        return toEnablement(body, lookup, responseEtag);
      }, enablementRefusal),

    listAgentVersions: async (filter) => {
      const { body } = await read(
        catalog.agentVersions.toGetRequestInformation({ queryParameters: { agentId: filter?.agentId, state: filter?.state } }),
        createAgentVersionListDtoFromDiscriminatorValue as ParsableFactory<AgentVersionListDto>,
      );
      if (!Array.isArray(body.items)) throw incompatible();
      return body.items.map(toVersionSummary);
    },

    getAgentVersion: async (agentId, version) => {
      const { body, etag } = await read(versionItem(agentId, version).toGetRequestInformation(), (createAgentVersionDtoFromDiscriminatorValue as ParsableFactory<AgentVersionDto>));
      const lookup = await memberLookup(api, [body.registeredBy, body.approvedBy, body.retiredBy]);
      return toAgentVersion(body, lookup, etag);
    },

    registerAgentVersion: (version, document) =>
      refusable(async (): Promise<RegistrationOutcome<AgentVersion>> => {
        // The version travels as the query parameter; the document is sent unchanged without a version property.
        const info = withJson(catalog.agentVersions.toPostRequestInformation({}, { queryParameters: { version } }), document);
        const { body, status, etag } = await exchange(api, info, (createAgentVersionDtoFromDiscriminatorValue as ParsableFactory<AgentVersionDto>));
        const lookup = await memberLookup(api, [body.registeredBy, body.approvedBy, body.retiredBy]);
        return { kind: status === 201 ? 'created' : 'replayed', value: toAgentVersion(body, lookup, etag) };
      }, registrationRefusal),

    approveAgentVersion: (agentId, version, etag, options) =>
      transition(
        versionItem(agentId, version).approval.toPostRequestInformation({
          differentiationReviewAcknowledged: options?.differentiationReviewAcknowledged,
        }),
        etag,
      ),

    retireAgentVersion: (agentId, version, etag) => {
      const info = withJson(versionItem(agentId, version).retirement.toPostRequestInformation(), {});
      return transition(info, etag);
    },

    listArtifacts: async (kind, filter) => {
      const { body } = await read(
        artifactKind(kind).toGetRequestInformation({ queryParameters: { id: filter?.id } }),
        createAgentCatalogArtifactListDtoFromDiscriminatorValue as ParsableFactory<AgentCatalogArtifactListDto>,
      );
      if (!Array.isArray(body.items)) throw incompatible();
      const lookup = await memberLookup(api, body.items.map((item) => item.registeredBy));
      return body.items.map((item) => toArtifactSummary(item, kind, lookup));
    },

    getArtifact: async (kind, id, version) => {
      const info = artifactKind(kind).byId(id).versions.byVersion(version).toGetRequestInformation();
      return toArtifactRecord((await read(info, (createAgentCatalogArtifactDtoFromDiscriminatorValue as ParsableFactory<AgentCatalogArtifactDto>))).body, kind);
    },

    registerArtifact: (kind, document) =>
      refusable(async (): Promise<RegistrationOutcome<ArtifactRecord<typeof kind>>> => {
        const info = withJson(artifactKind(kind).toPostRequestInformation({}), document);
        const { body, status } = await exchange(api, info, (createAgentCatalogArtifactDtoFromDiscriminatorValue as ParsableFactory<AgentCatalogArtifactDto>));
        return { kind: status === 201 ? 'created' : 'replayed', value: toArtifactRecord(body, kind) };
      }, registrationRefusal),
  };
}
