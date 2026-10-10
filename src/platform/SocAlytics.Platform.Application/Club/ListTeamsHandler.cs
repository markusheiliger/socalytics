using System.Buffers.Text;
using System.Globalization;
using System.Text;
using SocAlytics.Platform.Application.Abstractions;
using SocAlytics.Platform.Application.Abstractions.Persistence;
using SocAlytics.Platform.Application.IdentityAccess;

namespace SocAlytics.Platform.Application.Club;

public sealed class ListTeamsHandler(IUnitOfWork unitOfWork, IClubHierarchyStore clubs, IAccessAuthorizer authorizer)
{
    public const int DefaultPageSize = 50;
    public const int MaxPageSize = 200;

    public Task<OperationResult<TeamPage>> HandleAsync(ListTeamsQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        return ListAsync(null, query.PageSize, query.ContinuationToken, cancellationToken);
    }

    public Task<OperationResult<TeamPage>> HandleAsync(ListSeasonTeamsQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        return ListAsync(query.SeasonId, query.PageSize, query.ContinuationToken, cancellationToken);
    }

    private async Task<OperationResult<TeamPage>> ListAsync(Guid? seasonId, string? rawPageSize, string? token, CancellationToken cancellationToken)
    {
        var violations = new List<FieldViolation>();
        var pageSize = DefaultPageSize;
        if (rawPageSize is not null
            && (!int.TryParse(rawPageSize, NumberStyles.None, CultureInfo.InvariantCulture, out pageSize) || pageSize is < 1 or > MaxPageSize))
        {
            violations.Add(new FieldViolation("pageSize", "out-of-range"));
        }

        TeamPageKey? after = null;
        if (token is not null && !TryDecode(token, out after))
        {
            violations.Add(new FieldViolation("continuationToken", "invalid"));
        }

        if (violations.Count > 0)
        {
            return OperationFailure.Validation([.. violations]);
        }

        await using var scope = await unitOfWork.BeginAsync(cancellationToken);
        if (seasonId is { } id && await clubs.GetSeasonAsync(id, cancellationToken) is null)
        {
            return OperationFailure.NotFound();
        }

        var visibility = await authorizer.GetVisibleTeamsAsync(cancellationToken);
        var page = await clubs.ListTeamsAsync(seasonId, visibility, after, pageSize, cancellationToken);
        var next = page.HasMore && page.LastKey is not null ? Encode(page.LastKey) : null;
        return new TeamPage(page.Items, next);
    }

    private static string Encode(TeamPageKey key) =>
        Base64Url.EncodeToString(Encoding.UTF8.GetBytes($"{key.Id:D}:{key.Name}"));

    private static bool TryDecode(string token, out TeamPageKey? key)
    {
        key = null;
        try
        {
            var text = Encoding.UTF8.GetString(Base64Url.DecodeFromChars(token));
            if (text.Length < 38 || text[36] != ':' || !Guid.TryParseExact(text[..36], "D", out var id))
            {
                return false;
            }

            key = new TeamPageKey(text[37..], id);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
