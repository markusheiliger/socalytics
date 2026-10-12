using SocAlytics.Platform.Api.Http;
using SocAlytics.Platform.Api.Security;
using SocAlytics.Platform.Application.Analysis.Commands;
using SocAlytics.Platform.Application.Analysis.Queries;
using SocAlytics.Platform.Application.Messaging;

namespace SocAlytics.Platform.Api.Operations;

internal sealed record OutboxMessageDto(
	Guid MessageId, string MessageType, string ContractVersion, string Subject, string State, int AttemptCount, int PublicationRound,
	DateTimeOffset? LastAttemptAt, DateTimeOffset? NextAttemptAt, string? FailureCategory, DateTimeOffset CreatedAt,
	DateTimeOffset? PublishedAt, long AgeSeconds, long? PublicationLagSeconds, Guid? RunId, Guid? LogicalJobId, string? CapabilityId)
{
	public static OutboxMessageDto From(OutboxMessageView v) => new(
		v.MessageId, v.MessageType, v.ContractVersion, v.Subject, v.State, v.AttemptCount, v.PublicationRound, v.LastAttemptAt,
		v.NextAttemptAt, v.FailureCategory, v.CreatedAt, v.PublishedAt, v.AgeSeconds, v.PublicationLagSeconds, v.RunId,
		v.LogicalJobId, v.CapabilityId);
}

internal sealed record OutboxMessagePageDto(IReadOnlyList<OutboxMessageDto> Items, string? ContinuationToken);

internal sealed record WorkflowDefinitionDto(string State, string? Category, DateTimeOffset? Since);

internal sealed record AnalysisOperationalSummaryDto(
	WorkflowDefinitionDto WorkflowDefinition, IReadOnlyDictionary<string, int> RunsByState, int RunsWithBlockedNodes, int StaleAttempts,
	int RejectedNotifications, int OutboxPending, int OutboxFailed, long? OldestPendingAgeSeconds, DateTimeOffset ObservedAt);

internal sealed record NotificationReceiptDto(
	Guid MessageId, string MessageType, Guid? RecordingSetVersionId, string Outcome, string? RejectionCategory, Guid? RunId, DateTimeOffset ReceivedAt);

internal sealed record NotificationReceiptPageDto(IReadOnlyList<NotificationReceiptDto> Items, string? ContinuationToken);

internal sealed record ReconciliationResultDto(int ReadyJobsRepublished, int NotificationsRepublished, DateTimeOffset StartedAt, DateTimeOffset CompletedAt);

internal static class WorkflowOperationsEndpoints
{
	private const string Tag = "Workflow operations";

	public static IEndpointRouteBuilder MapWorkflowOperationsEndpoints(this IEndpointRouteBuilder routes)
	{
		var members = routes.MapMemberApi();
		members.MapGet("/operations/outbox-messages", async (string? state, string? messageType, string? pageSize, string? continuationToken, ListOutboxMessagesHandler h, CancellationToken ct) =>
		{
			var r = await h.HandleAsync(new ListOutboxMessagesQuery(state, messageType, pageSize, continuationToken), ct);
			return r.IsSuccess
				? Results.Ok(new OutboxMessagePageDto([.. r.Value.Items.Select(OutboxMessageDto.From)], r.Value.ContinuationToken))
				: ProblemResults.From(r.Failure);
		})
			.WithName("listOutboxMessages").WithTags(Tag)
			.Produces<OutboxMessagePageDto>()
			.ProducesProblem(StatusCodes.Status400BadRequest).ProducesProblem(StatusCodes.Status401Unauthorized).ProducesProblem(StatusCodes.Status403Forbidden);
		members.MapGet("/operations/outbox-messages/{messageId:guid}", async (Guid messageId, GetOutboxMessageHandler h, CancellationToken ct) =>
		{
			var r = await h.HandleAsync(new GetOutboxMessageQuery(messageId), ct);
			return r.IsSuccess ? Results.Ok(OutboxMessageDto.From(r.Value)) : ProblemResults.From(r.Failure);
		})
			.WithName("getOutboxMessage").WithTags(Tag)
			.Produces<OutboxMessageDto>()
			.ProducesProblem(StatusCodes.Status401Unauthorized).ProducesProblem(StatusCodes.Status403Forbidden).ProducesProblem(StatusCodes.Status404NotFound);
		members.MapGet("/operations/analysis/summary", async (GetAnalysisOperationalSummaryHandler h, CancellationToken ct) =>
		{
			var r = await h.HandleAsync(ct);
			if (!r.IsSuccess)
			{
				return ProblemResults.From(r.Failure);
			}

			var v = r.Value;
			return Results.Ok(new AnalysisOperationalSummaryDto(
				new WorkflowDefinitionDto(v.WorkflowDefinition.State, v.WorkflowDefinition.Category, v.WorkflowDefinition.Since),
				v.RunsByState, v.RunsWithBlockedNodes, v.StaleAttempts, v.RejectedNotifications, v.OutboxPending, v.OutboxFailed,
				v.OldestPendingAgeSeconds, v.ObservedAt));
		})
			.WithName("getAnalysisOperationalSummary").WithTags(Tag)
			.Produces<AnalysisOperationalSummaryDto>()
			.ProducesProblem(StatusCodes.Status401Unauthorized).ProducesProblem(StatusCodes.Status403Forbidden);
		members.MapGet("/operations/analysis/notification-receipts", async (string? outcome, string? pageSize, string? continuationToken, ListNotificationReceiptsHandler h, CancellationToken ct) =>
		{
			var r = await h.HandleAsync(new ListNotificationReceiptsQuery(outcome, pageSize, continuationToken), ct);
			return r.IsSuccess
				? Results.Ok(new NotificationReceiptPageDto(
					[.. r.Value.Items.Select(v => new NotificationReceiptDto(v.MessageId, v.MessageType, v.RecordingSetVersionId, v.Outcome, v.RejectionCategory, v.RunId, v.ReceivedAt))],
					r.Value.ContinuationToken))
				: ProblemResults.From(r.Failure);
		})
			.WithName("listNotificationReceipts").WithTags(Tag)
			.Produces<NotificationReceiptPageDto>()
			.ProducesProblem(StatusCodes.Status400BadRequest).ProducesProblem(StatusCodes.Status401Unauthorized).ProducesProblem(StatusCodes.Status403Forbidden);
		members.MapPost("/operations/analysis/reconciliations", async (RunAnalysisReconciliationHandler h, CancellationToken ct) =>
		{
			var r = await h.HandleAsync(ct);
			return r.IsSuccess
				? Results.Ok(new ReconciliationResultDto(r.Value.ReadyJobsRepublished, r.Value.NotificationsRepublished, r.Value.StartedAt, r.Value.CompletedAt))
				: ProblemResults.From(r.Failure);
		})
			.WithName("reconcileAnalysisPublication").WithTags(Tag)
			.Produces<ReconciliationResultDto>()
			.ProducesProblem(StatusCodes.Status401Unauthorized).ProducesProblem(StatusCodes.Status403Forbidden).ProducesProblem(StatusCodes.Status503ServiceUnavailable);
		return routes;
	}
}
