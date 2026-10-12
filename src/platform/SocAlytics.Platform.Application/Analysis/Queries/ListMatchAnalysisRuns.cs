using System.Buffers.Text;
using System.Globalization;
using System.Text;
using SocAlytics.Platform.Application.Abstractions;
using SocAlytics.Platform.Application.Abstractions.Persistence;
using SocAlytics.Platform.Application.IdentityAccess;

namespace SocAlytics.Platform.Application.Analysis.Queries;

public sealed record ListMatchAnalysisRunsQuery(Guid MatchId, string? PageSize, string? ContinuationToken);

public sealed class ListMatchAnalysisRunsHandler(IUnitOfWork unitOfWork, IAnalysisRunQueries queries, IAccessAuthorizer authorizer)
{
    public const int DefaultPageSize = 50;
    public const int MaxPageSize = 200;

    public async Task<OperationResult<AnalysisRunSummaryPage>> HandleAsync(ListMatchAnalysisRunsQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        var violations = new List<FieldViolation>();
        var pageSize = DefaultPageSize;
        if (query.PageSize is not null
            && (!int.TryParse(query.PageSize, NumberStyles.None, CultureInfo.InvariantCulture, out pageSize) || pageSize is < 1 or > MaxPageSize))
        {
            violations.Add(new FieldViolation("pageSize", "out-of-range"));
        }

        AnalysisRunPageKey? after = null;
        if (query.ContinuationToken is not null && (query.ContinuationToken.Length > 512 || !TryDecode(query.ContinuationToken, out after)))
        {
            violations.Add(new FieldViolation("continuationToken", "invalid"));
        }

        if (violations.Count > 0)
        {
            return OperationFailure.Validation([.. violations]);
        }

        await using var scope = await unitOfWork.BeginAsync(cancellationToken);
        var decision = await authorizer.AuthorizeTeamResourceAsync(new("match", query.MatchId), TeamPermission.Read, cancellationToken);
        if (!decision.IsGranted)
        {
            return OperationFailure.NotFound();
        }

        var page = await queries.ListForMatchAsync(query.MatchId, after, pageSize, cancellationToken);
        var last = page.Items.Count > 0 ? page.Items[^1] : null;
        var next = page.HasMore && last is not null ? Encode(new AnalysisRunPageKey(last.CreatedAt, last.RunId)) : null;
        return new AnalysisRunSummaryPage(page.Items, next);
    }

    private static string Encode(AnalysisRunPageKey key) =>
        Base64Url.EncodeToString(Encoding.UTF8.GetBytes($"{key.Id:D}:{key.CreatedAt.UtcTicks}"));

    private static bool TryDecode(string token, out AnalysisRunPageKey? key)
    {
        key = null;
        try
        {
            var text = Encoding.UTF8.GetString(Base64Url.DecodeFromChars(token));
            if (text.Length < 38 || text[36] != ':'
                || !Guid.TryParseExact(text[..36], "D", out var id)
                || !long.TryParse(text[37..], NumberStyles.None, CultureInfo.InvariantCulture, out var ticks)
                || ticks > DateTimeOffset.MaxValue.UtcTicks)
            {
                return false;
            }

            key = new AnalysisRunPageKey(new DateTimeOffset(ticks, TimeSpan.Zero), id);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
