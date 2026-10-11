using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using SocAlytics.Platform.Application.Registry.MachineAccess;

namespace SocAlytics.Platform.Api.Registry;

internal sealed class AnalystManagerDPoPAuthenticationHandler(
	IOptionsMonitor<AuthenticationSchemeOptions> options,
	ILoggerFactory logger,
	UrlEncoder encoder)
	: AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
	private const string Prefix = "DPoP ";
	private const string InactiveKey = "analyst-manager-inactive-state";

	protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
	{
		if (!Request.IsHttps)
		{
			return Fail(ManagerAuthenticationResult.InvalidToken);
		}

		var authorization = Request.Headers.Authorization;
		if (authorization.Count != 1 || authorization[0] is not { } value
			|| !value.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase) || value.Length == Prefix.Length)
		{
			return Fail(ManagerAuthenticationResult.InvalidToken);
		}

		var dpop = Request.Headers["DPoP"];
		var handler = Context.RequestServices.GetRequiredService<AuthenticateManagerRequestHandler>();
		var result = await handler.HandleAsync(
			new AuthenticateManagerCommand(
				value[Prefix.Length..].Trim(), dpop.Count == 1 ? dpop[0] : null, Request.Method, Request.Path.Value ?? "/"),
			Context.RequestAborted);
		if (!result.Succeeded)
		{
			if (result.InactiveState is not null)
			{
				Context.Items[InactiveKey] = result.InactiveState;
			}

			return Fail(result.Error!);
		}

		var identity = new ClaimsIdentity(
			[
				new Claim(AnalystManagerClaimTypes.ManagerId, result.Principal!.ManagerId.ToString()),
				new Claim(AnalystManagerClaimTypes.RegistrationId, result.Principal.RegistrationId.ToString()),
				new Claim(AnalystManagerClaimTypes.Scope, AnalystManagerDPoPDefaults.ScopeValue),
			],
			Scheme.Name);
		return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name));
	}

	protected override Task HandleChallengeAsync(AuthenticationProperties properties)
	{
		var error = Context.Items.TryGetValue(typeof(AnalystManagerDPoPAuthenticationHandler), out var e) && e is string s
			? s : ManagerAuthenticationResult.InvalidToken;
		Response.StatusCode = StatusCodes.Status401Unauthorized;
		Response.Headers.WWWAuthenticate = $"DPoP error=\"{error}\"";
		if (Context.Items.TryGetValue(InactiveKey, out var state) && state is string inactive)
		{
			return Response.WriteAsJsonAsync(new Dictionary<string, object?>
			{
				["type"] = "urn:socalytics:problem:analyst-manager-registration-inactive",
				["title"] = "Registration inactive",
				["status"] = StatusCodes.Status401Unauthorized,
				["code"] = "analyst-manager-registration-inactive",
				["correlationId"] = (System.Diagnostics.Activity.Current?.TraceId ?? System.Diagnostics.ActivityTraceId.CreateRandom()).ToHexString(),
				["registrationState"] = inactive,
			}, (System.Text.Json.JsonSerializerOptions?)null, "application/problem+json");
		}

		return Task.CompletedTask;
	}

	private AuthenticateResult Fail(string error)
	{
		Context.Items[typeof(AnalystManagerDPoPAuthenticationHandler)] = error;
		return AuthenticateResult.Fail(error);
	}
}
