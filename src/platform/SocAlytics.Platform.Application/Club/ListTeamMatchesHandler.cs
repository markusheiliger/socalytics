using System.Buffers.Text;
using System.Globalization;
using System.Text;
using SocAlytics.Platform.Application.Abstractions;
using SocAlytics.Platform.Application.Abstractions.Persistence;
using SocAlytics.Platform.Application.IdentityAccess;

namespace SocAlytics.Platform.Application.Club;

public sealed class ListTeamMatchesHandler(IUnitOfWork unitOfWork, IClubHierarchyStore clubs, IAccessAuthorizer authorizer)
{
    public const int DefaultPageSize = 50;
    public const int MaxPageSize = 200;

    public async Task<OperationResult<MatchPage>> HandleAsync(ListTeamMatchesQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        var violations = new List<FieldViolation>();
        var pageSize = DefaultPageSize;
        if (query.PageSize is not null
            && (!int.TryParse(query.PageSize, NumberStyles.None, CultureInfo.InvariantCulture, out pageSize) || pageSize is < 1 or > MaxPageSize))
        {
            violations.Add(new FieldViolation("pageSize", "out-of-range"));
        }

        MatchPageKey? after = null;
        if (query.ContinuationToken is not null && !TryDecode(query.ContinuationToken, out after))
        {
            violations.Add(new FieldViolation("continuationToken", "invalid"));
        }

        if (violations.Count > 0)
        {
            return OperationFailure.Validation([.. violations]);
        }

        await using var scope = await unitOfWork.BeginAsync(cancellationToken);
        var decision = await authorizer.AuthorizeTeamResourceAsync(new("team", query.TeamId), TeamPermission.Read, cancellationToken);
        if (!decision.IsGranted)
        {
            return OperationFailure.NotFound();
        }

        var page = await clubs.ListTeamMatchesAsync(query.TeamId, after, pageSize, cancellationToken);
        var next = page.HasMore && page.LastKey is not null ? Encode(page.LastKey) : null;
        return new MatchPage(page.Items, next);
    }

    private static string Encode(MatchPageKey key) =>
        Base64Url.EncodeToString(Encoding.UTF8.GetBytes($"{key.Id:D}:{key.KickoffAt.UtcTicks}"));

    private static bool TryDecode(string token, out MatchPageKey? key)
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

            key = new MatchPageKey(new DateTimeOffset(ticks, TimeSpan.Zero), id);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
