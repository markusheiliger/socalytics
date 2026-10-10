using System.Buffers.Text;
using System.Globalization;
using System.Text;
using SocAlytics.Platform.Application.Abstractions;
using SocAlytics.Platform.Application.Abstractions.Persistence;

namespace SocAlytics.Platform.Application.Club;

public sealed class ListSeasonsHandler(IUnitOfWork unitOfWork, IClubHierarchyStore clubs)
{
    public const int DefaultPageSize = 50;
    public const int MaxPageSize = 200;

    public async Task<OperationResult<SeasonPage>> HandleAsync(ListSeasonsQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        var violations = new List<FieldViolation>();
        var pageSize = DefaultPageSize;
        if (query.PageSize is not null
            && (!int.TryParse(query.PageSize, NumberStyles.None, CultureInfo.InvariantCulture, out pageSize) || pageSize is < 1 or > MaxPageSize))
        {
            violations.Add(new FieldViolation("pageSize", "out-of-range"));
        }

        SeasonPageKey? after = null;
        if (query.ContinuationToken is not null && !TryDecode(query.ContinuationToken, out after))
        {
            violations.Add(new FieldViolation("continuationToken", "invalid"));
        }

        if (violations.Count > 0)
        {
            return OperationFailure.Validation([.. violations]);
        }

        await using var scope = await unitOfWork.BeginAsync(cancellationToken);
        var page = await clubs.ListSeasonsAsync(after, pageSize, cancellationToken);
        var token = page.HasMore && page.LastKey is not null ? Encode(page.LastKey) : null;
        return new SeasonPage(page.Items, token);
    }

    private static string Encode(SeasonPageKey key) =>
        Base64Url.EncodeToString(Encoding.UTF8.GetBytes($"{key.CreatedAt.UtcTicks.ToString(CultureInfo.InvariantCulture)}:{key.Id:D}"));

    private static bool TryDecode(string token, out SeasonPageKey? key)
    {
        key = null;
        try
        {
            var text = Encoding.UTF8.GetString(Base64Url.DecodeFromChars(token));
            var separator = text.IndexOf(':', StringComparison.Ordinal);
            if (separator <= 0
                || !long.TryParse(text[..separator], NumberStyles.None, CultureInfo.InvariantCulture, out var ticks)
                || ticks > DateTimeOffset.MaxValue.UtcTicks
                || !Guid.TryParseExact(text[(separator + 1)..], "D", out var id))
            {
                return false;
            }

            key = new SeasonPageKey(new DateTimeOffset(ticks, TimeSpan.Zero), id);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
