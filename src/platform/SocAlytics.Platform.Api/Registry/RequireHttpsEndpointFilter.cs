using SocAlytics.Platform.Api.Http;

namespace SocAlytics.Platform.Api.Registry;

internal sealed class RequireHttpsEndpointFilter : IEndpointFilter
{
	public const string Code = "analyst-manager-https-required";

	public ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next) =>
		context.HttpContext.Request.IsHttps
			? next(context)
			: ValueTask.FromResult<object?>(ProblemResults.Problem(StatusCodes.Status403Forbidden, Code));
}
