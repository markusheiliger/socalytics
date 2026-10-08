using SocAlytics.Platform.Application.Abstractions;

namespace SocAlytics.Platform.Api.Http;

internal static class IfMatchHeader
{
	public static string Format(long version) => $"\"{version}\"";

	public static OperationResult<long> Parse(HttpRequest request)
	{
		var raw = request.Headers.IfMatch.ToString().Trim();
		if (raw.Length == 0 || raw == "*")
		{
			return OperationFailure.VersionRequired();
		}

		if (raw.Length > 2 && raw[0] == '"' && raw[^1] == '"'
			&& long.TryParse(raw[1..^1], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var version))
		{
			return version;
		}

		return OperationFailure.VersionMismatch(null);
	}
}
