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
			OperationFailureKind.Unavailable => StatusCodes.Status503ServiceUnavailable,
			OperationFailureKind.TooManyRequests => StatusCodes.Status429TooManyRequests,
			_ => throw new ArgumentOutOfRangeException(nameof(failure)),
		};
		return new ProblemResult(status, failure.Code, failure.FieldViolations, failure.Extensions);
	}

	public static IResult Problem(
		int status, string code, IReadOnlyList<FieldViolation>? violations = null, IReadOnlyDictionary<string, string>? extensions = null) =>
		new ProblemResult(status, code, violations, extensions);

	private sealed class ProblemResult(
		int status, string code, IReadOnlyList<FieldViolation>? violations, IReadOnlyDictionary<string, string>? extensions) : IResult
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
			if (Detail(code) is { } detail)
			{
				body["detail"] = detail;
			}

			if (extensions is not null)
			{
				foreach (var (name, value) in extensions)
				{
					body[name] = value;
				}
			}

			if (violations is { Count: > 0 })
			{
				body["errors"] = violations.Select(v => new { field = v.Field, code = v.Code }).ToArray();
			}

			if (extensions is not null)
			{
				foreach (var (name, value) in extensions)
				{
					body.TryAdd(name, value);
				}
			}

			return Results.Json(body, statusCode: status, contentType: "application/problem+json").ExecuteAsync(httpContext);
		}
	}

	private static string? Detail(string code) => code switch
	{
		"analyst-manager-software-key-not-allowed" => "This stamp requires hardware-backed key storage for this Manager profile.",
		"analyst-manager-version-below-minimum" =>
			"Update the Analyst Manager to the stamp's minimum version or later before it acquires new work.",
		_ => null,
	};

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
		"completion-rejected" => "The completion does not match the pinned job snapshot.",
		"version-required" => "A version precondition is required.",
		"queue-unavailable" => "The queue depth is temporarily unavailable.",
		"version-mismatch" => "The version precondition failed.",
		"analyst-manager-version-below-minimum" => "The Analyst Manager version is below the stamp's minimum.",
		_ => "The request could not be completed.",
	};
}
