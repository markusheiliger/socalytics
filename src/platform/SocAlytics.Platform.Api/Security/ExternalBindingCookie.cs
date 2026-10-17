namespace SocAlytics.Platform.Api.Security;

/// <summary>Binds an external sign-in callback to the browser that started it; <c>Lax</c> so it returns on the provider's top-level redirect.</summary>
internal static class ExternalBindingCookie
{
	public const string Name = "__Host-socalytics-external";

	public static string? Read(HttpRequest request) =>
		request.Cookies.TryGetValue(Name, out var value) && !string.IsNullOrEmpty(value) ? value : null;

	public static void Write(HttpResponse response, string value, TimeSpan lifetime) =>
		response.Cookies.Append(Name, value, Options(lifetime));

	public static void Clear(HttpResponse response) => response.Cookies.Delete(Name, Options(null));

	private static CookieOptions Options(TimeSpan? lifetime) => new()
	{
		Secure = true,
		HttpOnly = true,
		SameSite = SameSiteMode.Lax,
		Path = "/",
		MaxAge = lifetime,
	};
}
