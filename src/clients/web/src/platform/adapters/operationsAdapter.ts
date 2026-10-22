import { isUntypedNode, type Guid, type ParsableFactory } from '@microsoft/kiota-abstractions';
import {
  createAnalysisOperationalSummaryDtoFromDiscriminatorValue,
  createNotificationReceiptPageDtoFromDiscriminatorValue,
  createOutboxMessageDtoFromDiscriminatorValue,
  createOutboxMessagePageDtoFromDiscriminatorValue,
  createReconciliationResultDtoFromDiscriminatorValue,
  type AnalysisOperationalSummaryDto,
  type NotificationReceiptDto,
  type NotificationReceiptPageDto,
  type OutboxMessageDto,
  type OutboxMessagePageDto,
  type ReconciliationResultDto,
} from '../../generated/platform-api/models';
import { PlatformFailure } from '../failure';
import type {
  NotificationReceipt,
  OperationalSummary,
  OutboxMessage,
  OutboxMessageType,
  OutboxState,
  Page,
  ReceiptOutcome,
  ReconciliationOutcome,
  RunStateCount,
} from '../models/operations';
import { sendFor, type PlatformApi } from './kiota';

export interface OperationsAdapter {
  summary(): Promise<OperationalSummary>;
  listOutboxMessages(
    filter: { state?: OutboxState; messageType?: OutboxMessageType },
    continuationToken?: string,
  ): Promise<Page<OutboxMessage>>;
  getOutboxMessage(messageId: string): Promise<OutboxMessage>;
  listNotificationReceipts(filter: { outcome?: ReceiptOutcome }, continuationToken?: string): Promise<Page<NotificationReceipt>>;
  reconcile(): Promise<ReconciliationOutcome>;
}

// Lifecycle order of the run states; unknown states follow with their raw names (research.md § R2).
const RUN_STATE_ORDER = ['Requested', 'Running', 'Completed', 'PartiallyCompleted', 'Failed', 'Cancelled'];

const incompatible = () => new PlatformFailure('incompatible');

/** Generated models type counts as `integer | string`; anything that is not a finite integer is absent. */
function toInteger(value: unknown): number | undefined {
  if (isUntypedNode(value)) return toInteger(value.getValue());
  if (typeof value === 'number') return Number.isInteger(value) ? value : undefined;
  if (typeof value === 'string' && /^-?\d+$/.test(value.trim())) {
    const parsed = Number(value.trim());
    return Number.isSafeInteger(parsed) ? parsed : undefined;
  }
  return undefined;
}

function requiredInteger(value: unknown): number {
  const result = toInteger(value);
  if (result === undefined) throw incompatible();
  return result;
}

function toDate(value: unknown): Date | undefined {
  if (value === null || value === undefined) return undefined;
  if (value instanceof Date && !Number.isNaN(value.getTime())) return value;
  throw incompatible();
}

function requiredDate(value: unknown): Date {
  const result = toDate(value);
  if (result === undefined) throw incompatible();
  return result;
}

function requiredString(value: string | null | undefined): string {
  if (!value) throw incompatible();
  return value;
}

function toId(value: Guid | string | null | undefined): string | undefined {
  return value == null ? undefined : value.toString();
}

function toRunsByState(additionalData: Record<string, unknown> | undefined): RunStateCount[] {
  const entries: RunStateCount[] = [];
  for (const [state, raw] of Object.entries(additionalData ?? {})) {
    const count = toInteger(raw);
    if (count !== undefined) entries.push({ state, count });
  }
  const rank = (state: string) => {
    const index = RUN_STATE_ORDER.indexOf(state);
    return index < 0 ? RUN_STATE_ORDER.length : index;
  };
  return entries
    .map((entry, position) => ({ entry, position }))
    .sort((a, b) => rank(a.entry.state) - rank(b.entry.state) || a.position - b.position)
    .map(({ entry }) => entry);
}

function toSummary(dto: AnalysisOperationalSummaryDto): OperationalSummary {
  const definition = dto.workflowDefinition;
  return {
    workflowDefinition: {
      state: requiredString(definition?.state),
      category: definition?.category ?? undefined,
      since: toDate(definition?.since),
    },
    runsByState: toRunsByState(dto.runsByState?.additionalData),
    runsWithBlockedNodes: requiredInteger(dto.runsWithBlockedNodes),
    staleAttempts: requiredInteger(dto.staleAttempts),
    rejectedNotifications: requiredInteger(dto.rejectedNotifications),
    outboxPending: requiredInteger(dto.outboxPending),
    outboxFailed: requiredInteger(dto.outboxFailed),
    oldestPendingAgeSeconds: toInteger(dto.oldestPendingAgeSeconds),
    observedAt: requiredDate(dto.observedAt),
  };
}

function toOutboxMessage(dto: OutboxMessageDto): OutboxMessage {
  return {
    messageId: requiredString(toId(dto.messageId)),
    messageType: requiredString(dto.messageType),
    contractVersion: requiredString(dto.contractVersion),
    subject: dto.subject ?? '',
    state: requiredString(dto.state),
    attemptCount: requiredInteger(dto.attemptCount),
    publicationRound: requiredInteger(dto.publicationRound),
    lastAttemptAt: toDate(dto.lastAttemptAt),
    nextAttemptAt: toDate(dto.nextAttemptAt),
    failureCategory: dto.failureCategory ?? undefined,
    createdAt: requiredDate(dto.createdAt),
    publishedAt: toDate(dto.publishedAt),
    ageSeconds: requiredInteger(dto.ageSeconds),
    publicationLagSeconds: toInteger(dto.publicationLagSeconds),
    runId: toId(dto.runId),
    logicalJobId: toId(dto.logicalJobId),
    capabilityId: dto.capabilityId ?? undefined,
  };
}

function toReceipt(dto: NotificationReceiptDto): NotificationReceipt {
  return {
    messageId: requiredString(toId(dto.messageId)),
    messageType: requiredString(dto.messageType),
    recordingSetVersionId: toId(dto.recordingSetVersionId),
    outcome: requiredString(dto.outcome),
    rejectionCategory: dto.rejectionCategory ?? undefined,
    runId: toId(dto.runId),
    receivedAt: requiredDate(dto.receivedAt),
  };
}

function toReconciliation(dto: ReconciliationResultDto): ReconciliationOutcome {
  return {
    readyJobsRepublished: requiredInteger(dto.readyJobsRepublished),
    notificationsRepublished: requiredInteger(dto.notificationsRepublished),
    startedAt: requiredDate(dto.startedAt),
    completedAt: requiredDate(dto.completedAt),
  };
}

function toPage<T, D>(dto: { items?: D[] | null; continuationToken?: string | null }, map: (item: D) => T): Page<T> {
  return { items: (dto.items ?? []).map(map), continuationToken: dto.continuationToken || undefined };
}

export function createOperationsAdapter(api: PlatformApi): OperationsAdapter {
  const operations = api.client.api.v1.operations;
  const summaryFactory = createAnalysisOperationalSummaryDtoFromDiscriminatorValue as ParsableFactory<AnalysisOperationalSummaryDto>;
  const messagePageFactory = createOutboxMessagePageDtoFromDiscriminatorValue as ParsableFactory<OutboxMessagePageDto>;
  const messageFactory = createOutboxMessageDtoFromDiscriminatorValue as ParsableFactory<OutboxMessageDto>;
  const receiptPageFactory = createNotificationReceiptPageDtoFromDiscriminatorValue as ParsableFactory<NotificationReceiptPageDto>;
  const reconciliationFactory = createReconciliationResultDtoFromDiscriminatorValue as ParsableFactory<ReconciliationResultDto>;
  return {
    summary: async () =>
      toSummary(await sendFor(api, operations.analysis.summary.toGetRequestInformation(), summaryFactory)),
    listOutboxMessages: async (filter, continuationToken) => {
      const queryParameters = {
        state: filter.state || undefined,
        messageType: filter.messageType || undefined,
        continuationToken: continuationToken || undefined,
      };
      const info = operations.outboxMessages.toGetRequestInformation({ queryParameters });
      return toPage(await sendFor(api, info, messagePageFactory), toOutboxMessage);
    },
    getOutboxMessage: async (messageId) =>
      toOutboxMessage(
        await sendFor(api, operations.outboxMessages.byMessageId(messageId as Guid).toGetRequestInformation(), messageFactory),
      ),
    listNotificationReceipts: async (filter, continuationToken) => {
      const queryParameters = {
        outcome: filter.outcome || undefined,
        continuationToken: continuationToken || undefined,
      };
      const info = operations.analysis.notificationReceipts.toGetRequestInformation({ queryParameters });
      return toPage(await sendFor(api, info, receiptPageFactory), toReceipt);
    },
    reconcile: async () =>
      toReconciliation(await sendFor(api, operations.analysis.reconciliations.toPostRequestInformation(), reconciliationFactory)),
  };
}
