using System.Net.Mime;
using MediaTypeHeaderValue = Microsoft.Net.Http.Headers.MediaTypeHeaderValue;

namespace SocAlytics.Platform.Api.Http;

internal sealed class JsonOnlyFilter : IEndpointFilter
{
	public ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
	{
		var contentType = context.HttpContext.Request.ContentType;
		var isJson = contentType is not null
			&& MediaTypeHeaderValue.TryParse(contentType, out var parsed)
			&& string.Equals(parsed.MediaType.Value, MediaTypeNames.Application.Json, StringComparison.OrdinalIgnoreCase);
		return isJson
			? next(context)
			: ValueTask.FromResult<object?>(ProblemResults.Problem(StatusCodes.Status415UnsupportedMediaType, "unsupported-media-type"));
	}
}
