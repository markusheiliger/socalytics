import {
  createAgentConversationDtoFromDiscriminatorValue,
  createAgentConversationListDtoFromDiscriminatorValue,
  createAgentInvocationAcceptedDtoFromDiscriminatorValue,
  createAgentInvocationDtoFromDiscriminatorValue,
  createAgentInvocationLineageDtoFromDiscriminatorValue,
  createAgentTurnPageDtoFromDiscriminatorValue,
  createAvailableAgentsDtoFromDiscriminatorValue,
  type AgentConversationDto,
  type AgentConversationListDto,
  type AgentInvocationAcceptedDto,
  type AgentInvocationDto,
  type AgentTurnPageDto,
  type AvailableAgentsDto,
  type AgentInvocationLineageDto,
  type AgentTurnDto,
} from '../../generated/platform-api/models';
import {
  ResponseHandlerOption,
  type Guid,
  type Parsable,
  type ParsableFactory,
  type ParseNodeFactory,
  type RequestInformation,
  type ResponseHandler,
} from '@microsoft/kiota-abstractions';
import { PlatformFailure, mapProblem, parseProblem, toPlatformFailure } from '../failure';
import {
  toAgentGroup,
  toFailureReason,
  toLineageKind,
  toTurnState,
  type Advice,
  type Conditional,
  type ConversationSummary,
  type EnabledAgent,
  type ExecutionReferences,
  type InvocationLineage,
  type InvocationStatus,
  type SubmissionAccepted,
  type Turn,
  type TurnPage,
  type TriageConversation,
  type TriageOutcome,
} from '../models/agents';
import { withIdempotencyKey, withIfMatch, type PlatformApi } from './kiota';

export class TriageFailure extends Error {
  readonly outcome: TriageOutcome;
  readonly failure: PlatformFailure;

  constructor(outcome: TriageOutcome, failure: PlatformFailure) {
    super(`Triage request failed: ${outcome.kind}`);
    this.name = 'TriageFailure';
    this.outcome = outcome;
    this.failure = failure;
  }
}

export interface AgentConversationAdapter {
  listAgents(): Promise<EnabledAgent[]>;
  listConversations(matchId: string): Promise<ConversationSummary[]>;
  createConversation(matchId: string, analysisRunId: string, key: string): Promise<TriageConversation>;
  getConversation(conversationId: string, etag?: string): Promise<Conditional<TriageConversation>>;
  addParticipant(conversationId: string, agentId: string, version: number): Promise<TriageConversation>;
  submitQuestion(conversationId: string, agentId: string, question: string, key: string): Promise<SubmissionAccepted>;
  listTurns(conversationId: string, cursor?: string, etag?: string): Promise<Conditional<TurnPage>>;
  getInvocationStatus(invocationId: string, etag?: string): Promise<Conditional<InvocationStatus>>;
  getLineage(invocationId: string, etag?: string): Promise<Conditional<InvocationLineage>>;
}

const STATE_CHANGING = new Set(['createConversation', 'submitQuestion']);

const CONFLICT_OUTCOMES: Record<string, TriageOutcome['kind']> = {
  'agent-not-available': 'agentNotAvailable',
  'agent-not-participant': 'agentNotParticipant',
  'participant-exists': 'participantExists',
  'analysis-run-not-eligible': 'runNotEligible',
};

/** Translates the shared failure into a triage outcome; the shared mapping itself stays unchanged. */
export function toTriageOutcome(failure: PlatformFailure, stateChanging = false): TriageOutcome {
  const { status, code } = failure;
  if (status === 429 && code === 'too-many-unfinished-invocations') return { kind: 'tooManyUnfinished' };
  if (status === 409 && code && CONFLICT_OUTCOMES[code]) return { kind: CONFLICT_OUTCOMES[code] } as TriageOutcome;
  if (failure.kind === 'validation' && status === 400 && failure.fieldErrors.length > 0) {
    return { kind: 'invalidQuestion', fieldErrors: failure.fieldErrors };
  }
  if (failure.kind === 'notPermitted') return { kind: 'notPermitted' };
  if (status === 404) return { kind: 'notFound' };
  if (status === 412) return { kind: 'staleVersion' };
  if (failure.kind === 'unreachable' && stateChanging) return { kind: 'unknownOutcome' };
  return { kind: 'platform', failureKind: failure.kind };
}

interface Captured<T> {
  status: number;
  etag?: string;
  retryAfterSeconds?: number;
  value?: T;
}

function retryAfterOf(response: Response): number | undefined {
  const raw = response.headers.get('Retry-After')?.trim();
  return raw && /^\d+$/.test(raw) ? Number(raw) : undefined;
}

// Delivers status and headers with the parsed body; every non-success answer goes through the shared problem mapping.
class CaptureHandler<T extends Parsable> implements ResponseHandler {
  constructor(
    private readonly factory: ParsableFactory<T>,
    private readonly parseNodeFactory: ParseNodeFactory,
  ) {}

  async handleResponse<NativeResponseType, ModelType>(response: NativeResponseType): Promise<ModelType | undefined> {
    const res = response as unknown as Response;
    const text = await res.text();
    if (res.status === 304) return { status: 304 } as ModelType;
    if (res.status < 200 || res.status >= 300) {
      let problem = {};
      try {
        problem = parseProblem(JSON.parse(text));
      } catch {
        // an answer without a problem body keeps its status
      }
      throw mapProblem(res.status, problem, retryAfterOf(res));
    }
    const captured: Captured<T> = {
      status: res.status,
      etag: res.headers.get('ETag') ?? undefined,
      retryAfterSeconds: retryAfterOf(res),
    };
    if (text.length > 0) {
      const buffer = new TextEncoder().encode(text).buffer as ArrayBuffer;
      captured.value = this.parseNodeFactory
        .getRootParseNode('application/json', buffer)
        .getObjectValue<T>(this.factory);
    }
    return captured as ModelType;
  }
}

const iso = (value: Date | null | undefined): string | undefined =>
  value instanceof Date && !Number.isNaN(value.getTime()) ? value.toISOString() : undefined;

function required<T>(value: T | null | undefined): T {
  if (value === undefined || value === null || value === '') throw new PlatformFailure('incompatible');
  return value;
}

export function toConversation(dto: AgentConversationDto | undefined): Omit<TriageConversation, 'etag'> {
  if (!dto) throw new PlatformFailure('incompatible');
  return {
    conversationId: required(dto.conversationId).toString(),
    matchId: required(dto.matchId).toString(),
    analysisRunId: required(dto.analysisRunId).toString(),
    teamId: required(dto.teamId).toString(),
    analysisRunIsCurrent: dto.analysisRunIsCurrent === true,
    state: required(dto.state),
    participants: (dto.participants ?? []).map((p) => ({
      agentId: required(p.agentId),
      addedAt: required(iso(p.addedAt)),
    })),
    createdAt: required(iso(dto.createdAt)),
    version: required(dto.version),
  };
}

const adviceOf = (text: string | null | undefined): Advice | undefined =>
  text === undefined || text === null ? undefined : { text, evidenceReferences: [] };

function toTurn(dto: AgentTurnDto): Turn {
  return {
    invocationId: required(dto.invocationId).toString(),
    agentId: required(dto.agentId),
    question: dto.question ?? '',
    state: toTurnState(required(dto.state)),
    failureReason: dto.failureReason ? toFailureReason(dto.failureReason) : undefined,
    submittedAt: required(iso(dto.submittedAt)),
    endedAt: iso(dto.endedAt),
    advice: adviceOf(dto.advice),
  };
}

const unwrap = (node: unknown): unknown =>
  typeof node === 'object' && node !== null && typeof (node as { getValue?: unknown }).getValue === 'function'
    ? (node as { getValue: () => unknown }).getValue()
    : node;

const EXECUTION_FIELDS = ['runtime', 'modelProvider', 'model', 'deployment', 'promptRef', 'toolPolicyRef', 'safetyPolicyRef'] as const;

function toExecutionReferences(node: unknown): ExecutionReferences | undefined {
  const value = unwrap(node);
  if (typeof value !== 'object' || value === null) return undefined;
  const record = value as Record<string, unknown>;
  const references: ExecutionReferences = {};
  for (const field of EXECUTION_FIELDS) {
    const entry = unwrap(record[field]);
    if (typeof entry === 'string') references[field] = entry;
  }
  return references;
}

function detailText(value: unknown): string {
  const plain = unwrap(value);
  return typeof plain === 'string' ? plain : JSON.stringify(plain) ?? '';
}

function toLineage(dto: AgentInvocationLineageDto, etag: string): InvocationLineage {
  const advice = dto.advice
    ? {
        text: required(dto.advice.advice),
        evidenceReferences: [],
        executionReferences: toExecutionReferences(dto.advice.executionReferences),
      }
    : undefined;
  return {
    invocationId: required(dto.invocationId).toString(),
    policyVersion: dto.policyVersion ?? '',
    entries: (dto.entries ?? [])
      .map((entry) => ({
        sequence: required(entry.sequence),
        kind: toLineageKind(required(entry.kind)),
        attemptId: entry.attemptId?.toString(),
        attemptNumber: entry.attemptNumber ?? undefined,
        recordedAt: required(iso(entry.recordedAt)),
        details: Object.fromEntries(
          Object.entries(entry.details?.additionalData ?? {}).map(([key, value]) => [key, detailText(value)]),
        ),
      }))
      .sort((a, b) => a.sequence - b.sequence),
    advice,
    etag,
  };
}

export function createAgentConversationAdapter(api: PlatformApi): AgentConversationAdapter {
  const v1 = api.client.api.v1;
  const conversation = (id: string) => v1.agentConversations.byConversationId(id as Guid);
  const invocation = (id: string) => v1.agentInvocations.byInvocationId(id as Guid);

  async function send<T extends Parsable>(
    method: string,
    info: RequestInformation,
    factory: ParsableFactory<T>,
    etag?: string,
  ): Promise<Captured<T>> {
    if (etag) info.headers.add('If-None-Match', etag);
    const option = new ResponseHandlerOption();
    option.responseHandler = new CaptureHandler(factory, api.adapter.getParseNodeFactory());
    info.addRequestOptions([option]);
    try {
      const result = await api.adapter.send<Captured<T>>(info, (() => undefined) as unknown as ParsableFactory<Parsable & Captured<T>>, {});
      if (!result) throw new PlatformFailure('incompatible');
      return result;
    } catch (error) {
      const failure = toPlatformFailure(error);
      throw new TriageFailure(toTriageOutcome(failure, STATE_CHANGING.has(method)), failure);
    }
  }

  async function body<T extends Parsable>(
    method: string,
    info: RequestInformation,
    factory: ParsableFactory<T>,
  ): Promise<Captured<T> & { value: T }> {
    const result = await send(method, info, factory);
    if (!result.value) throw new TriageFailure({ kind: 'platform', failureKind: 'incompatible' }, new PlatformFailure('incompatible'));
    return result as Captured<T> & { value: T };
  }

  async function conditional<T extends Parsable, R>(
    method: string,
    info: RequestInformation,
    factory: ParsableFactory<T>,
    etag: string | undefined,
    map: (dto: T, etag: string) => R,
  ): Promise<Conditional<R>> {
    const result = await send(method, info, factory, etag);
    if (result.status === 304) return { kind: 'unchanged' };
    try {
      const responseEtag = required(result.etag);
      if (!result.value) throw new PlatformFailure('incompatible');
      return { kind: 'changed', value: map(result.value, responseEtag), etag: responseEtag, retryAfterSeconds: result.retryAfterSeconds };
    } catch (error) {
      const failure = toPlatformFailure(error);
      throw new TriageFailure(toTriageOutcome(failure), failure);
    }
  }

  // Creation and participant answers carry no own validator; the version is the fallback for the next conditional read.
  const withEtag = (dto: AgentConversationDto, etag: string | undefined): TriageConversation => {
    const model = toConversation(dto);
    return { ...model, etag: etag ?? `"${model.version}"` };
  };

  const mapped = <R>(fn: () => R): R => {
    try {
      return fn();
    } catch (error) {
      const failure = toPlatformFailure(error);
      throw new TriageFailure(toTriageOutcome(failure), failure);
    }
  };

  return {
    listAgents: async () => {
      const { value } = await body('listAgents', v1.agentCatalog.agents.toGetRequestInformation(), createAvailableAgentsDtoFromDiscriminatorValue as ParsableFactory<AvailableAgentsDto>);
      return mapped(() =>
        (value.agents ?? []).map((agent) => ({
          agentId: required(agent.agentId),
          group: toAgentGroup(agent.group ?? ''),
          domain: agent.domain ?? '',
        })),
      );
    },
    listConversations: async (matchId) => {
      const info = v1.matches.byMatchId(matchId as Guid).agentConversations.toGetRequestInformation();
      const { value } = await body('listConversations', info, createAgentConversationListDtoFromDiscriminatorValue as ParsableFactory<AgentConversationListDto>);
      return mapped(() => (value.conversations ?? []).map(toConversation));
    },
    createConversation: async (matchId, analysisRunId, key) => {
      const builder = v1.matches.byMatchId(matchId as Guid).agentConversations;
      const info = withIdempotencyKey(builder.toPostRequestInformation({ analysisRunId: analysisRunId as Guid }), key);
      const result = await body('createConversation', info, createAgentConversationDtoFromDiscriminatorValue as ParsableFactory<AgentConversationDto>);
      return mapped(() => withEtag(result.value, result.etag));
    },
    getConversation: (conversationId, etag) =>
      conditional('getConversation', conversation(conversationId).toGetRequestInformation(), createAgentConversationDtoFromDiscriminatorValue as ParsableFactory<AgentConversationDto>, etag, (dto, tag) => ({
        ...toConversation(dto),
        etag: tag,
      })),
    addParticipant: async (conversationId, agentId, version) => {
      const info = withIfMatch(conversation(conversationId).participants.toPostRequestInformation({ agentId }), version);
      const result = await body('addParticipant', info, createAgentConversationDtoFromDiscriminatorValue as ParsableFactory<AgentConversationDto>);
      return mapped(() => withEtag(result.value, result.etag));
    },
    submitQuestion: async (conversationId, agentId, question, key) => {
      const info = withIdempotencyKey(conversation(conversationId).invocations.toPostRequestInformation({ agentId, question }), key);
      const result = await body('submitQuestion', info, createAgentInvocationAcceptedDtoFromDiscriminatorValue as ParsableFactory<AgentInvocationAcceptedDto>);
      return mapped(() => ({
        conversationId: required(result.value.conversationId).toString(),
        invocationId: required(result.value.invocationId).toString(),
        statusLocation: result.value.statusLocation ?? '',
        retryAfterSeconds: result.value.retryAfterSeconds ?? result.retryAfterSeconds ?? 2,
      }));
    },
    listTurns: (conversationId, cursor, etag) =>
      conditional(
        'listTurns',
        conversation(conversationId).turns.toGetRequestInformation({ queryParameters: cursor ? { cursor } : {} }),
        createAgentTurnPageDtoFromDiscriminatorValue as ParsableFactory<AgentTurnPageDto>,
        etag,
        (dto, tag) => ({ turns: (dto.turns ?? []).map(toTurn), nextCursor: dto.nextCursor ?? undefined, etag: tag }),
      ),
    getInvocationStatus: (invocationId, etag) =>
      conditional('getInvocationStatus', invocation(invocationId).toGetRequestInformation(), createAgentInvocationDtoFromDiscriminatorValue as ParsableFactory<AgentInvocationDto>, etag, (dto, tag) => ({
        invocationId: required(dto.invocationId).toString(),
        conversationId: required(dto.conversationId).toString(),
        agentId: required(dto.agentId),
        route: dto.route ?? '',
        state: toTurnState(required(dto.state)),
        failureReason: dto.failureReason ? toFailureReason(dto.failureReason) : undefined,
        attemptCount: dto.attemptCount ?? 0,
        retryAfterSeconds: dto.retryAfterSeconds ?? undefined,
        etag: tag,
      })).then((result) =>
        // The Retry-After header seeds the delay when the body does not.
        result.kind === 'changed' && result.value.retryAfterSeconds === undefined
          ? { ...result, value: { ...result.value, retryAfterSeconds: result.retryAfterSeconds } }
          : result,
      ),
    getLineage: (invocationId, etag) =>
      conditional('getLineage', invocation(invocationId).lineage.toGetRequestInformation(), createAgentInvocationLineageDtoFromDiscriminatorValue as ParsableFactory<AgentInvocationLineageDto>, etag, toLineage),
  };
}
