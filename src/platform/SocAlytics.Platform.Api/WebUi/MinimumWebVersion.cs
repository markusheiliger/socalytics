using System.Text.RegularExpressions;

namespace SocAlytics.Platform.Api.WebUi;

public sealed partial class WebUiOptions
{
	public const string SectionName = "WebUi";

	public string? MinimumVersion { get; set; }

	public override string ToString() => nameof(WebUiOptions);

	[GeneratedRegex(@"^\d{4}\.\d{4}\.\d{6}$", RegexOptions.CultureInvariant)]
	private static partial Regex VersionShape();

	public static bool IsValidVersion(string? value) => value is not null && VersionShape().IsMatch(value);
}

public static class MinimumWebVersion
{
	public const string HeaderName = "SocAlytics-Minimum-Web-Version";

	public static IServiceCollection AddMinimumWebVersion(this IServiceCollection services, IConfiguration configuration)
	{
		services.AddOptions<WebUiOptions>()
			.Bind(configuration.GetSection(WebUiOptions.SectionName))
			.Validate(
				options => string.IsNullOrEmpty(options.MinimumVersion) || WebUiOptions.IsValidVersion(options.MinimumVersion),
				"WebUi:MinimumVersion must be empty or have the shape YYYY.MMDD.HHMMSS.")
			.ValidateOnStart();
		return services;
	}

	// Informational only: the platform never refuses a request because of the client's version.
	public static IApplicationBuilder UseMinimumWebVersion(this IApplicationBuilder app) =>
		app.Use(async (context, next) =>
		{
			if (context.Request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase))
			{
				var minimum = context.RequestServices
					.GetRequiredService<Microsoft.Extensions.Options.IOptions<WebUiOptions>>().Value.MinimumVersion;
				if (!string.IsNullOrEmpty(minimum))
				{
					context.Response.OnStarting(static state =>
					{
						var (response, value) = ((HttpResponse, string))state;
						response.Headers[HeaderName] = value;
						return Task.CompletedTask;
					}, (context.Response, minimum));
				}
			}

			await next(context);
		});
}
