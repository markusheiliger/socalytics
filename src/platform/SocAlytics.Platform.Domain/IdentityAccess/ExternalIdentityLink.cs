using System.Text.RegularExpressions;

namespace SocAlytics.Platform.Domain.IdentityAccess;

/// <summary>A member's link to one identity at a configured external OpenID Connect provider.</summary>
public sealed partial record ExternalIdentityLink
{
    public ExternalIdentityLink(
        Guid id,
        Guid accountId,
        string providerId,
        string issuer,
        string subject,
        DateTimeOffset linkedAt,
        Guid linkedBy,
        long version = 1)
    {
        if (!IsValidProviderId(providerId))
        {
            throw new ArgumentException("A provider identifier must match [a-z0-9-]{1,32}.", nameof(providerId));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(issuer);
        ArgumentException.ThrowIfNullOrWhiteSpace(subject);
        Id = id;
        AccountId = accountId;
        ProviderId = providerId;
        Issuer = issuer;
        Subject = subject;
        LinkedAt = linkedAt;
        LinkedBy = linkedBy;
        Version = version;
    }

    public Guid Id { get; }

    public Guid AccountId { get; }

    public string ProviderId { get; }

    public string Issuer { get; }

    /// <summary>The ID token's <c>sub</c>; never returned by any read model.</summary>
    public string Subject { get; }

    public DateTimeOffset LinkedAt { get; }

    public Guid LinkedBy { get; }

    public long Version { get; }

    public static bool IsValidProviderId(string? providerId) =>
        providerId is not null && ProviderIdPattern().IsMatch(providerId);

    [GeneratedRegex("^[a-z0-9-]{1,32}$", RegexOptions.CultureInvariant)]
    private static partial Regex ProviderIdPattern();
}
