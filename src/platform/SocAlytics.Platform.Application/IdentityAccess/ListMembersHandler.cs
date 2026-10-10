using System.Buffers.Text;
using System.Globalization;
using System.Text;
using SocAlytics.Platform.Application.Abstractions;
using SocAlytics.Platform.Application.Abstractions.Persistence;

namespace SocAlytics.Platform.Application.IdentityAccess;

public sealed class ListMembersHandler(IUnitOfWork unitOfWork, IAccessAuthorizer authorizer, IMemberAccountStore members)
{
    public const int DefaultPageSize = 50;
    public const int MaxPageSize = 200;

    public async Task<OperationResult<MemberPage>> HandleAsync(ListMembersQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        await using var scope = await unitOfWork.BeginAsync(cancellationToken);
        var decision = await authorizer.AuthorizeClubAsync(ClubPermission.Administer, new AuditResource("member", null), cancellationToken);
        if (!decision.IsGranted)
        {
            return OperationFailure.Forbidden();
        }

        var violations = new List<FieldViolation>();
        var pageSize = DefaultPageSize;
        if (query.PageSize is not null
            && (!int.TryParse(query.PageSize, NumberStyles.None, CultureInfo.InvariantCulture, out pageSize) || pageSize is < 1 or > MaxPageSize))
        {
            violations.Add(new FieldViolation("pageSize", "out-of-range"));
        }

        MemberPageKey? after = null;
        if (query.ContinuationToken is not null && !TryDecode(query.ContinuationToken, out after))
        {
            violations.Add(new FieldViolation("continuationToken", "invalid"));
        }

        if (violations.Count > 0)
        {
            return OperationFailure.Validation([.. violations]);
        }

        var page = await members.ListMembersAsync(after, pageSize, cancellationToken);
        var token = page.HasMore && page.LastKey is not null ? Encode(page.LastKey) : null;
        return new MemberPage(page.Items, token);
    }

    private static string Encode(MemberPageKey key) =>
        Base64Url.EncodeToString(Encoding.UTF8.GetBytes($"{key.Id:D}:{key.NormalizedAccountName}"));

    private static bool TryDecode(string token, out MemberPageKey? key)
    {
        key = null;
        try
        {
            var text = Encoding.UTF8.GetString(Base64Url.DecodeFromChars(token));
            var separator = text.IndexOf(':', StringComparison.Ordinal);
            if (separator <= 0 || separator == text.Length - 1 || !Guid.TryParseExact(text[..separator], "D", out var id))
            {
                return false;
            }

            key = new MemberPageKey(text[(separator + 1)..], id);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
