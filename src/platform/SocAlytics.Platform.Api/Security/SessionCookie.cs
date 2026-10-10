using System.Security.Cryptography;
using System.Text;

namespace SocAlytics.Platform.Api.Security;

internal static class SessionCookie
{
	public const string Name = "__Host-socalytics-session";

	public static string? Read(HttpRequest request) =>
		request.Cookies.TryGetValue(Name, out var value) && !string.IsNullOrEmpty(value) ? value : null;

	public static byte[] Hash(string rawToken) => SHA256.HashData(Encoding.UTF8.GetBytes(rawToken));

	public static void Write(HttpResponse response, string rawToken) =>
		response.Cookies.Append(Name, rawToken, Options());

	public static void Clear(HttpResponse response) => response.Cookies.Delete(Name, Options());

	private static CookieOptions Options() => new()
	{
		Secure = true,
		HttpOnly = true,
		SameSite = SameSiteMode.Strict,
		Path = "/",
	};
}
