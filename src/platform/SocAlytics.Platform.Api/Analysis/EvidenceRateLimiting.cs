using System.Globalization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using SocAlytics.Platform.Api.Http;
using SocAlytics.Platform.Api.Security;
using SocAlytics.Platform.Application.Analysis.Evidence;

namespace SocAlytics.Platform.Api.Analysis;

internal static class EvidenceRateLimiting
{
	public const string Policy = "analysis-evidence-media";

	public const string RateLimitedCode = "evidence-rate-limited";

	private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

	public static IServiceCollection AddEvidenceRateLimiting(this IServiceCollection services) =>
		services.AddRateLimiter(options => options.AddPolicy<string>(Policy, new EvidenceLimiterPolicy()));

	private sealed class EvidenceLimiterPolicy : IRateLimiterPolicy<string>
	{
		public Func<OnRejectedContext, CancellationToken, ValueTask>? OnRejected { get; } = RejectAsync;

		public RateLimitPartition<string> GetPartition(HttpContext httpContext)
		{
			var limit = httpContext.RequestServices.GetRequiredService<IOptions<EvidenceOptions>>().Value.ResolutionsPerMinute ?? 1;
			var account = httpContext.User.FindFirst(SessionClaimTypes.AccountId)?.Value
				?? httpContext.Connection.RemoteIpAddress?.ToString()
				?? "unknown";
			return RateLimitPartition.GetFixedWindowLimiter($"{Policy}:{account}", _ => new FixedWindowRateLimiterOptions
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
