using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using SocAlytics.Platform.Api.Http;
using SocAlytics.Platform.Application.Abstractions;
using SocAlytics.Platform.Application.IdentityAccess;

namespace SocAlytics.Platform.Api.Security;

internal sealed class SessionAuthenticationHandler(
	IOptionsMonitor<AuthenticationSchemeOptions> options,
	ILoggerFactory logger,
	UrlEncoder encoder)
	: AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
	public const string SchemeName = "SocAlyticsSession";

	protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
	{
		var token = SessionCookie.Read(Request);
		if (token is null)
		{
			return AuthenticateResult.NoResult();
		}

		var validator = Context.RequestServices.GetRequiredService<ValidateSessionHandler>();
		var result = await validator.HandleAsync(new ValidateSessionQuery(SessionCookie.Hash(token)), Context.RequestAborted);
		if (!result.IsSuccess)
		{
			return AuthenticateResult.NoResult();
		}

		var session = result.Value;
		var identity = new ClaimsIdentity(
			[
				new Claim(SessionClaimTypes.AccountId, session.AccountId.ToString()),
				new Claim(SessionClaimTypes.SessionId, session.SessionId.ToString()),
				new Claim(SessionClaimTypes.PasswordChangeRequired, session.PasswordChangeRequired ? "true" : "false"),
			],
			SchemeName);
		return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName));
	}

	protected override Task HandleChallengeAsync(AuthenticationProperties properties) =>
		ProblemResults.From(OperationFailure.Unauthenticated()).ExecuteAsync(Context);

	protected override Task HandleForbiddenAsync(AuthenticationProperties properties) =>
		ProblemResults.From(OperationFailure.PasswordChangeRequired()).ExecuteAsync(Context);
}
