using System.Globalization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using SocAlytics.Platform.Api.Http;
using SocAlytics.Platform.Application.IdentityAccess.Email;

namespace SocAlytics.Platform.Api.Endpoints.IdentityAccess;

internal static class EmailRateLimiting
{
	public const string ConfirmationPolicy = "email-confirmation";

	public const string RateLimitedCode = "email-confirmation-rate-limited";

	private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

	public static IServiceCollection AddEmailRateLimiting(this IServiceCollection services) =>
		services.AddRateLimiter(options => options.AddPolicy<string>(ConfirmationPolicy, new ConfirmationLimiterPolicy()));

	private sealed class ConfirmationLimiterPolicy : IRateLimiterPolicy<string>
	{
		public Func<OnRejectedContext, CancellationToken, ValueTask>? OnRejected { get; } = RejectAsync;

		public RateLimitPartition<string> GetPartition(HttpContext httpContext)
		{
			var limit = httpContext.RequestServices.GetRequiredService<IOptions<EmailConfirmationOptions>>().Value.AttemptsPerMinute;
			var address = httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
			return RateLimitPartition.GetFixedWindowLimiter($"{ConfirmationPolicy}:{address}", _ => new FixedWindowRateLimiterOptions
			{
				PermitLimit = Math.Max(1, limit),
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
