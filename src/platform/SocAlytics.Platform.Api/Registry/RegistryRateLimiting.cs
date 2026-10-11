using System.Globalization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using SocAlytics.Platform.Api.Http;
using SocAlytics.Platform.Api.Security;
using SocAlytics.Platform.Application.Registry;

namespace SocAlytics.Platform.Api.Registry;

internal static class RegistryRateLimiting
{
	public const string PairingCreation = "registry-pairing-creation";
	public const string Status = "registry-status";
	public const string Activation = "registry-activation";
	public const string CodeSubmission = "registry-code-submission";
	public const string Token = "registry-token";

	private const string RateLimitedCode = "analyst-manager-rate-limited";
	private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

	public static IServiceCollection AddRegistryRateLimiting(this IServiceCollection services)
	{
		services.AddRateLimiter(options =>
		{
			options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
			AddPolicy(options, PairingCreation, ByAddress, o => o.PairingCreationPerMinute);
			AddPolicy(options, Status, ByAddress, o => o.StatusPerMinute);
			AddPolicy(options, Activation, ByAddress, o => o.ActivationPerMinute);
			AddPolicy(options, CodeSubmission, ByAccount, o => o.CodeSubmissionPerMinute);
			AddPolicy(options, Token, ByAddress, o => o.TokenPerMinute);
			options.OnRejected = async (context, cancellationToken) =>
			{
				var response = context.HttpContext.Response;
				var retryAfter = context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var delay)
					? Math.Max(1, (int)Math.Ceiling(delay.TotalSeconds))
					: (int)Window.TotalSeconds;
				response.Headers.RetryAfter = retryAfter.ToString(CultureInfo.InvariantCulture);
				response.Headers.CacheControl = "no-store";
				await ProblemResults.Problem(StatusCodes.Status429TooManyRequests, RateLimitedCode)
					.ExecuteAsync(context.HttpContext);
			};
		});
		return services;
	}

	private static string ByAddress(HttpContext http, string policy) =>
		$"{policy}:{http.Connection.RemoteIpAddress?.ToString() ?? "unknown"}";

	private static string ByAccount(HttpContext http, string policy)
	{
		var account = http.User.FindFirst(SessionClaimTypes.AccountId)?.Value;
		return account is null ? ByAddress(http, policy) : $"{policy}:account:{account}";
	}

	private static void AddPolicy(
		RateLimiterOptions options,
		string name,
		Func<HttpContext, string, string> partition,
		Func<RegistryOptions.RateLimitOptions, int> limit) =>
		options.AddPolicy(name, http =>
		{
			var configured = http.RequestServices.GetRequiredService<IOptions<RegistryOptions>>().Value.RateLimits;
			return RateLimitPartition.GetFixedWindowLimiter(partition(http, name), _ => new FixedWindowRateLimiterOptions
			{
				PermitLimit = Math.Max(1, limit(configured)),
				Window = Window,
				QueueLimit = 0,
				AutoReplenishment = true,
			});
		});
}
