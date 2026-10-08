using System.Net.Http.Json;
using System.Text.Json;

namespace SocAlytics.Platform.Integration.Tests.IdentityAccess.Support;

internal sealed class ApiSession : IDisposable
{
	private ApiSession(HttpClient client, JsonElement info, string rawToken)
	{
		Client = client;
		RawToken = rawToken;
		Info = info;
		AntiforgeryToken = info.GetProperty("antiforgeryToken").GetString()!;
	}

	public HttpClient Client { get; }

	public JsonElement Info { get; }

	public string RawToken { get; }

	public string AntiforgeryToken { get; }

	public static async Task<ApiSession> SignInAsync(
		PlatformApiFactory factory,
		string accountName,
		string password,
		CancellationToken cancellationToken)
	{
		var client = factory.CreateApiClient();
		using var response = await client.PostAsJsonAsync("/api/v1/session", new { accountName, password }, cancellationToken);
		response.EnsureSuccessStatusCode();
		var info = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
		var cookie = response.Headers.GetValues("Set-Cookie").Single();
		return new ApiSession(client, info, cookie.Split(';')[0].Split('=', 2)[1]);
	}

	public Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, object? body, CancellationToken cancellationToken)
	{
		var request = new HttpRequestMessage(method, path);
		if (body is not null)
		{
			request.Content = JsonContent.Create(body);
		}

		if (method != HttpMethod.Get && method != HttpMethod.Head)
		{
			request.Headers.Add("X-CSRF-Token", AntiforgeryToken);
		}

		return Client.SendAsync(request, cancellationToken);
	}

	public void Dispose() => Client.Dispose();
}
