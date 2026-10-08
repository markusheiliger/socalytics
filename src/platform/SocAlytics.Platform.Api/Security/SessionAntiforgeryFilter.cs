using System.Security.Cryptography;
using System.Text;
using SocAlytics.Platform.Api.Http;

namespace SocAlytics.Platform.Api.Security;

internal sealed class SessionAntiforgeryFilter : IEndpointFilter
{
	public const string HeaderName = "X-CSRF-Token";

	public static string ComputeToken(string rawSessionToken)
	{
		var mac = HMACSHA256.HashData(Encoding.UTF8.GetBytes(rawSessionToken), "socalytics-csrf"u8);
		return Convert.ToBase64String(mac).TrimEnd('=').Replace('+', '-').Replace('/', '_');
	}

	public ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
	{
		var request = context.HttpContext.Request;
		if (HttpMethods.IsGet(request.Method) || HttpMethods.IsHead(request.Method)
			|| HttpMethods.IsOptions(request.Method) || HttpMethods.IsTrace(request.Method))
		{
			return next(context);
		}

		var cookie = SessionCookie.Read(request);
		var presented = request.Headers[HeaderName].ToString();
		if (cookie is not null && presented.Length > 0)
		{
			var expected = Encoding.UTF8.GetBytes(ComputeToken(cookie));
			if (CryptographicOperations.FixedTimeEquals(expected, Encoding.UTF8.GetBytes(presented)))
			{
				return next(context);
			}
		}

		return ValueTask.FromResult<object?>(ProblemResults.Problem(StatusCodes.Status403Forbidden, "antiforgery-failed"));
	}
}
