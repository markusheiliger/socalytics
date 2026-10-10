using System.Diagnostics;
using SocAlytics.Platform.Application.Abstractions;

namespace SocAlytics.Platform.Api.Http;

internal static class ProblemResults
{
	public static IResult From(OperationFailure failure)
	{
		var status = failure.Kind switch
		{
			OperationFailureKind.Validation => StatusCodes.Status400BadRequest,
			OperationFailureKind.NotFound => StatusCodes.Status404NotFound,
			OperationFailureKind.Forbidden => StatusCodes.Status403Forbidden,
			OperationFailureKind.Conflict => StatusCodes.Status409Conflict,
			OperationFailureKind.VersionRequired => StatusCodes.Status428PreconditionRequired,
			OperationFailureKind.VersionMismatch => StatusCodes.Status412PreconditionFailed,
			OperationFailureKind.Unauthenticated => StatusCodes.Status401Unauthorized,
			OperationFailureKind.PasswordChangeRequired => StatusCodes.Status403Forbidden,
			_ => throw new ArgumentOutOfRangeException(nameof(failure)),
		};
		return Problem(status, failure.Code, failure.FieldViolations);
	}

	public static IResult Problem(int status, string code, IReadOnlyList<FieldViolation>? violations = null) =>
		new ProblemResult(status, code, violations);

	private sealed class ProblemResult(int status, string code, IReadOnlyList<FieldViolation>? violations) : IResult
	{
		public Task ExecuteAsync(HttpContext httpContext)
		{
			var body = new Dictionary<string, object?>
			{
				["type"] = $"urn:socalytics:problem:{code}",
				["title"] = Title(code),
				["status"] = status,
				["code"] = code,
				["correlationId"] = (Activity.Current?.TraceId ?? ActivityTraceId.CreateRandom()).ToHexString(),
			};
			if (violations is { Count: > 0 })
			{
				body["errors"] = violations.Select(v => new { field = v.Field, code = v.Code }).ToArray();
			}

			return Results.Json(body, statusCode: status, contentType: "application/problem+json").ExecuteAsync(httpContext);
		}
	}

	private static string Title(string code) => code switch
	{
		"validation-failed" => "The request is invalid.",
		"unsupported-media-type" => "The media type is not supported.",
		"sign-in-failed" => "Sign-in failed.",
		"unauthenticated" => "Authentication is required.",
		"antiforgery-failed" => "The anti-forgery proof is missing or invalid.",
		"forbidden" => "The caller is not allowed to perform this operation.",
		"password-change-required" => "A password change is required.",
		"not-found" => "The resource was not found.",
		"version-required" => "A version precondition is required.",
		"version-mismatch" => "The version precondition failed.",
		_ => "The request could not be completed.",
	};
}
