namespace SocAlytics.Platform.Api.Http;

internal static class SharedProblemCodes
{
	public const string IdempotencyKeyMissing = "idempotency-key-missing";
	public const string IdempotencyKeyReused = "idempotency-key-reused";

	public static int StatusOf(string code) => code switch
	{
		IdempotencyKeyMissing => StatusCodes.Status400BadRequest,
		IdempotencyKeyReused => StatusCodes.Status409Conflict,
		_ => throw new ArgumentOutOfRangeException(nameof(code)),
	};
}
