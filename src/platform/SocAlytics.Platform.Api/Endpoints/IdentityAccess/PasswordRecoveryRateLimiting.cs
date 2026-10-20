using System.Globalization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using SocAlytics.Platform.Api.Http;
using SocAlytics.Platform.Application.IdentityAccess.PasswordRecovery;

namespace SocAlytics.Platform.Api.Endpoints.IdentityAccess;

internal static class PasswordRecoveryRateLimiting
{
	public const string RequestPolicy = "password-recovery-request";

	public const string TokenPolicy = "password-recovery-token";

	public const string RateLimitedCode = "password-recovery-rate-limited";

	private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

	public static IServiceCollection AddPasswordRecoveryRateLimiting(this IServiceCollection services) =>
		services.AddRateLimiter(options =>
		{
			options.AddPolicy<string>(RequestPolicy, new PerClientPolicy(RequestPolicy, o => o.RequestsPerMinute));
			options.AddPolicy<string>(TokenPolicy, new PerClientPolicy(TokenPolicy, o => o.TokenAttemptsPerMinute));
		});

	// Partitioned by remote address only, so known and unknown addresses and tokens are limited alike.
	private sealed class PerClientPolicy(string name, Func<PasswordRecoveryOptions, int> limit) : IRateLimiterPolicy<string>
	{
		public Func<OnRejectedContext, CancellationToken, ValueTask>? OnRejected { get; } = RejectAsync;

		public RateLimitPartition<string> GetPartition(HttpContext httpContext)
		{
			var configured = httpContext.RequestServices.GetRequiredService<IOptions<PasswordRecoveryOptions>>().Value;
			var address = httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
			return RateLimitPartition.GetFixedWindowLimiter($"{name}:{address}", _ => new FixedWindowRateLimiterOptions
			{
				PermitLimit = Math.Max(1, limit(configured)),
				Window = Window,
				QueueLimit = 0,
				AutoReplenishment = true,
			});
		}

		private static async ValueTask RejectAsync(OnRejectedContext context, CancellationToken cancellationToken)
		{
			var response = context.HttpContext.Response;
			var retryAfter = context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var delay)
				? Math.Max(1, (int)Math.Ceiling(delay.TotalSeconds))
				: (int)Window.TotalSeconds;
			response.Headers.RetryAfter = retryAfter.ToString(CultureInfo.InvariantCulture);
			response.Headers.CacheControl = "no-store";
			await ProblemResults.Problem(StatusCodes.Status429TooManyRequests, RateLimitedCode).ExecuteAsync(context.HttpContext);
		}
	}
}
