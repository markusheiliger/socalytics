using System.Globalization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using SocAlytics.Platform.Api.Http;
using SocAlytics.Platform.Application.IdentityAccess.Invitations;

namespace SocAlytics.Platform.Api.Endpoints.IdentityAccess;

internal static class InvitationRateLimiting
{
	public const string TokenPolicy = "invitation-token";

	public const string RateLimitedCode = "invitation-rate-limited";

	private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

	public static IServiceCollection AddInvitationRateLimiting(this IServiceCollection services) =>
		services.AddRateLimiter(options => options.AddPolicy<string>(TokenPolicy, new TokenLimiterPolicy()));

	// Partitioned by remote address only, so known and unknown tokens are limited alike.
	private sealed class TokenLimiterPolicy : IRateLimiterPolicy<string>
	{
		public Func<OnRejectedContext, CancellationToken, ValueTask>? OnRejected { get; } = RejectAsync;

		public RateLimitPartition<string> GetPartition(HttpContext httpContext)
		{
			var configured = httpContext.RequestServices.GetRequiredService<IOptions<InvitationOptions>>().Value;
			var address = httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
			return RateLimitPartition.GetFixedWindowLimiter($"{TokenPolicy}:{address}", _ => new FixedWindowRateLimiterOptions
			{
				PermitLimit = Math.Max(1, configured.TokenAttemptsPerMinute),
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
