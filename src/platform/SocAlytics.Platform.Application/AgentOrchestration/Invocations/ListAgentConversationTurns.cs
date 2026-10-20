using System.Globalization;
using System.Text;
using SocAlytics.Platform.Application.Abstractions;
using SocAlytics.Platform.Application.Abstractions.Persistence;
using SocAlytics.Platform.Application.IdentityAccess;

namespace SocAlytics.Platform.Application.AgentOrchestration.Invocations;

public sealed record ListAgentConversationTurnsQuery(Guid ConversationId, string? Cursor, int? Limit);

public sealed class ListAgentConversationTurnsHandler(
    IUnitOfWork unitOfWork,
    IAgentConversationStore conversations,
    IAgentInvocationStore invocations,
    IAccessAuthorizer authorizer,
    IRequestContext requestContext)
{
    public const int DefaultLimit = 20;
    public const int MaxLimit = 100;

    public async Task<OperationResult<AgentTurnPage>> HandleAsync(
        ListAgentConversationTurnsQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        if (requestContext.MemberAccountId is not { } ownerId)
        {
            return OperationFailure.NotFound();
        }

        await using var scope = await unitOfWork.BeginAsync(cancellationToken);
        var decision = await authorizer.AuthorizeTeamResourceAsync(
            new(AgentOrchestrationAuditEvents.ConversationResourceType, query.ConversationId), TeamPermission.Read, cancellationToken);
        if (!decision.IsGranted)
        {
            return OperationFailure.NotFound();
        }

        var conversation = await conversations.LockByIdAsync(query.ConversationId, cancellationToken);
        if (conversation is null || conversation.OwnerMemberId != ownerId)
        {
            return OperationFailure.NotFound();
        }

        var limit = query.Limit ?? DefaultLimit;
        if (limit is < 1 or > MaxLimit)
        {
            return OperationFailure.Validation(new FieldViolation("limit", "out-of-range"));
        }

        AgentTurnCursor? after = null;
        if (!string.IsNullOrEmpty(query.Cursor))
        {
            if (!TryDecode(query.Cursor, out var decoded))
            {
                return OperationFailure.Validation(new FieldViolation("cursor", "invalid"));
            }

            after = decoded;
        }

        var turns = await invocations.ListTurnsAsync(conversation.Id, after, limit + 1, cancellationToken);
        var page = turns.Take(limit).ToList();
        var next = turns.Count > limit && page.Count > 0 ? Encode(page[^1]) : null;
        return new AgentTurnPage(page, next);
    }

    private static string Encode(AgentTurnRecord last)
    {
        var text = $"{last.SubmittedAt.UtcTicks.ToString(CultureInfo.InvariantCulture)}.{last.InvocationId:N}";
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(text)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static bool TryDecode(string cursor, out AgentTurnCursor result)
    {
        result = default!;
        try
        {
            var padded = cursor.Replace('-', '+').Replace('_', '/');
            padded = padded.PadRight(padded.Length + ((4 - (padded.Length % 4)) % 4), '=');
            var text = Encoding.UTF8.GetString(Convert.FromBase64String(padded));
            var parts = text.Split('.');
            if (parts.Length != 2
                || !long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var ticks)
                || ticks > DateTime.MaxValue.Ticks
                || !Guid.TryParseExact(parts[1], "N", out var id))
            {
                return false;
            }

            result = new AgentTurnCursor(new DateTimeOffset(ticks, TimeSpan.Zero), id);
            return true;
        }
        catch (Exception ex) when (ex is FormatException or DecoderFallbackException)
        {
            return false;
        }
    }
}
