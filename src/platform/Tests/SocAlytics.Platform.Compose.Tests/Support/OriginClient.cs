using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace SocAlytics.Platform.Compose.Tests.Support;

/// <summary>
/// A client for the published Web UI origin. The session cookie is <c>Secure</c> and the evaluation origin is plain HTTP on loopback,
/// so a cookie container would never send it back: this client keeps the cookie and the anti-forgery token itself.
/// </summary>
public sealed class OriginClient : IDisposable
{
    private const string CookieName = "__Host-socalytics-session";

    private readonly HttpClient _http;
    private string? _sessionToken;
    private string? _antiforgeryToken;

    public OriginClient(string origin)
    {
        _http = new HttpClient(new HttpClientHandler { UseCookies = false, AllowAutoRedirect = false })
        {
            BaseAddress = new Uri(origin),
            Timeout = TimeSpan.FromSeconds(15),
        };
    }

    public bool HasSession => _sessionToken is not null;

    /// <summary>Sends a request through the origin, with the session cookie and, for changing requests, the anti-forgery token.</summary>
    public Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, object? body, CancellationToken ct)
    {
        var request = new HttpRequestMessage(method, path);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        if (_sessionToken is not null)
        {
            request.Headers.Add("Cookie", $"{CookieName}={_sessionToken}");
        }

        if (_antiforgeryToken is not null && method != HttpMethod.Get && method != HttpMethod.Head)
        {
            request.Headers.Add("X-CSRF-Token", _antiforgeryToken);
        }

        return _http.SendAsync(request, ct);
    }

    /// <summary>Calls <c>POST /api/v1/session</c> and keeps the issued cookie and anti-forgery token. Returns the session information.</summary>
    public async Task<JsonElement> SignInAsync(string accountName, string password, CancellationToken ct)
    {
        _sessionToken = null;
        _antiforgeryToken = null;
        using var response = await SendAsync(HttpMethod.Post, "/api/v1/session", new { accountName, password }, ct);
        if (response.StatusCode != HttpStatusCode.OK)
        {
            throw new InvalidOperationException($"Sign-in answered {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync(ct)}");
        }

        var cookie = response.Headers.GetValues("Set-Cookie").Single(value => value.StartsWith(CookieName + "=", StringComparison.Ordinal));
        _sessionToken = cookie.Split(';')[0].Split('=', 2)[1];
        var info = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
        _antiforgeryToken = info.GetProperty("antiforgeryToken").GetString();
        return info;
    }

    /// <summary>Calls <c>POST /api/v1/me/password</c> with the current session.</summary>
    public Task<HttpResponseMessage> ChangeOwnPasswordAsync(string currentPassword, string newPassword, CancellationToken ct) =>
        SendAsync(HttpMethod.Post, "/api/v1/me/password", new { currentPassword, newPassword }, ct);

    /// <summary>Calls <c>GET /api/v1/me</c> with the current session and returns the own profile.</summary>
    public async Task<JsonElement> GetOwnProfileAsync(CancellationToken ct)
    {
        using var response = await SendAsync(HttpMethod.Get, "/api/v1/me", null, ct);
        if (response.StatusCode != HttpStatusCode.OK)
        {
            throw new InvalidOperationException($"The own profile answered {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync(ct)}");
        }

        return await response.Content.ReadFromJsonAsync<JsonElement>(ct);
    }

    public void Dispose() => _http.Dispose();
}
