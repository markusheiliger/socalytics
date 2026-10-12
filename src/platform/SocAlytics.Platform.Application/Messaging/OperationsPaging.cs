using System.Buffers.Text;
using System.Globalization;
using System.Text;
using SocAlytics.Platform.Application.Abstractions;

namespace SocAlytics.Platform.Application.Messaging;

public sealed record OperationsPageKey(DateTimeOffset At, Guid Id);

public sealed record OperationsPageResult<T>(IReadOnlyList<T> Items, bool HasMore);

public sealed record OperationsPage<T>(IReadOnlyList<T> Items, string? ContinuationToken);

public static class OperationsPaging
{
    public const int DefaultPageSize = 50;
    public const int MaxPageSize = 200;

    public static bool TryParse(string? pageSize, string? token, out int size, out OperationsPageKey? after, List<FieldViolation> violations)
    {
        size = DefaultPageSize;
        after = null;
        if (pageSize is not null
            && (!int.TryParse(pageSize, NumberStyles.None, CultureInfo.InvariantCulture, out size) || size is < 1 or > MaxPageSize))
        {
            violations.Add(new FieldViolation("pageSize", "out-of-range"));
        }

        if (token is not null && (token.Length > 512 || !TryDecode(token, out after)))
        {
            violations.Add(new FieldViolation("continuationToken", "invalid"));
        }

        return violations.Count == 0;
    }

    public static string Encode(OperationsPageKey key) =>
        Base64Url.EncodeToString(Encoding.UTF8.GetBytes($"{key.Id:D}:{key.At.UtcTicks}"));

    private static bool TryDecode(string token, out OperationsPageKey? key)
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

            key = new OperationsPageKey(new DateTimeOffset(ticks, TimeSpan.Zero), id);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
