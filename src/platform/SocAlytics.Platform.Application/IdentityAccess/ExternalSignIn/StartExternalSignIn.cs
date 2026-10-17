using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using SocAlytics.Platform.Application.Abstractions.Persistence;
using SocAlytics.Platform.Domain.IdentityAccess;

namespace SocAlytics.Platform.Application.IdentityAccess.ExternalSignIn;

public sealed record StartExternalSignInCommand(
    string ProviderId, string RedirectUri, string? AccountName = null, string? LinkCredential = null);

public enum StartExternalSignInOutcome
{
    Started,
    UnknownProvider,
    InvalidLinkCredential,
    ProviderUnavailable,
}

/// <param name="BrowserBinding">The raw browser-binding value the caller stores in the binding cookie; only its hash is persisted.</param>
public sealed record StartExternalSignInResult(
    StartExternalSignInOutcome Outcome,
    Uri? AuthorizationUrl,
    string? BrowserBinding,
    TimeSpan BindingLifetime);

public sealed class StartExternalSignInHandler(
    IUnitOfWork unitOfWork,
    IOidcProviderClient provider,
    IPendingExternalSignInStore pending,
    IMemberAccountStore members,
    IAccountCredentialService credentials,
    IOptions<IdentityAccessOptions> options,
    TimeProvider time)
{
    private const int ExpiredBatchSize = 100;

    public async Task<StartExternalSignInResult> HandleAsync(StartExternalSignInCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var settings = options.Value;
        if (!settings.ExternalProviders.ContainsKey(command.ProviderId))
        {
            return new StartExternalSignInResult(StartExternalSignInOutcome.UnknownProvider, null, null, TimeSpan.Zero);
        }

        var linking = command.AccountName is not null || command.LinkCredential is not null;
        var state = RandomValue();
        var nonce = RandomValue();
        var verifier = RandomValue();
        var binding = RandomValue();

        Uri url;
        try
        {
            url = await provider.BuildAuthorizationUrlAsync(command.ProviderId, command.RedirectUri, state, nonce, verifier, cancellationToken);
        }
        catch (OidcProviderException ex)
        {
            var outcome = ex.Failure == OidcProviderFailure.UnknownProvider
                ? StartExternalSignInOutcome.UnknownProvider
                : StartExternalSignInOutcome.ProviderUnavailable;
            return new StartExternalSignInResult(outcome, null, null, TimeSpan.Zero);
        }

        var now = time.GetUtcNow();
        var lifetime = settings.ExternalSignIn.PendingLifetime;
        await using var scope = await unitOfWork.BeginAsync(cancellationToken);
        Guid? linkCredentialId = null;
        if (linking)
        {
            linkCredentialId = await ValidateLinkCredentialAsync(command, cancellationToken);
            if (linkCredentialId is null)
            {
                return new StartExternalSignInResult(StartExternalSignInOutcome.InvalidLinkCredential, null, null, TimeSpan.Zero);
            }
        }

        await pending.DeleteExpiredOrConsumedAsync(now, ExpiredBatchSize, cancellationToken);
        await pending.CreateAsync(
            new PendingExternalSignIn(
                Guid.NewGuid(), Hash(state), Hash(binding), nonce, verifier, command.ProviderId, linkCredentialId, now, now + lifetime),
            cancellationToken);
        await scope.CommitAsync(cancellationToken);
        return new StartExternalSignInResult(StartExternalSignInOutcome.Started, url, binding, lifetime);
    }

    // Only checks; the credential is consumed by the callback together with the link.
    private async Task<Guid?> ValidateLinkCredentialAsync(StartExternalSignInCommand command, CancellationToken cancellationToken)
    {
        if (!AccountName.TryCreate(command.AccountName, out var name, out _)
            || string.IsNullOrEmpty(command.LinkCredential)
            || await members.FindAccountIdByNameAsync(name, cancellationToken) is not { } accountId)
        {
            return null;
        }

        return await credentials.ValidateLinkCredentialAsync(accountId, command.LinkCredential, command.ProviderId, cancellationToken);
    }

    internal static byte[] Hash(string value) => SHA256.HashData(Encoding.UTF8.GetBytes(value));

    private static string RandomValue() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
