namespace SocAlytics.Platform.Application.Registry.RegistryCredentials;

public static class RegistryCredentialKinds
{
    public const string Bearer = "bearer";
    public const string Anonymous = "anonymous";
}

public sealed record RegistryPullToken(string Kind, string? Token, DateTimeOffset ExpiresAt);

/// <summary>The registry's token service cannot be reached or refuses the platform's credentials.</summary>
public sealed class RegistryPullTokenException(string message) : Exception(message);

public interface IRegistryPullTokenIssuer
{
    string RegistryHost { get; }

    string RepositoryPrefix { get; }

    /// <summary>Issues a short-lived, read-only credential for exactly the given repositories.</summary>
    /// <exception cref="RegistryPullTokenException">The registry cannot issue the credential.</exception>
    Task<RegistryPullToken> IssueAsync(IReadOnlyList<string> repositories, CancellationToken cancellationToken);
}
