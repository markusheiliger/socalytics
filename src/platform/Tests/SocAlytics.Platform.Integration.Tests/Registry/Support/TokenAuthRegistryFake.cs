using System.Collections.Concurrent;
using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.AspNetCore.Hosting.Server.Features;

namespace SocAlytics.Platform.Integration.Tests.Registry.Support;

/// <summary>A registry that answers <c>/v2/</c> with a bearer challenge and issues a fixed token from its token endpoint.</summary>
internal sealed class TokenAuthRegistryFake : IAsyncDisposable
{
    public const string IssuedToken = "registry-token-4f9c1d7a2b";

    private readonly WebApplication _app;
    private readonly ConcurrentQueue<string> _tokenQueries = new();
    private readonly ConcurrentQueue<string?> _tokenAuthorizations = new();

    private TokenAuthRegistryFake(WebApplication app, string host)
    {
        _app = app;
        RegistryHost = host;
    }

    public string RegistryHost { get; }

    public IReadOnlyList<string> TokenQueries => _tokenQueries.ToArray();

    public IReadOnlyList<string?> TokenAuthorizations => _tokenAuthorizations.ToArray();

    public int ExpiresInSeconds { get; set; } = 3600;

    public bool RefuseTokens { get; set; }

    public static async Task<TokenAuthRegistryFake> StartAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        TokenAuthRegistryFake? fake = null;
        app.MapGet("/v2/", (HttpContext http) =>
        {
            http.Response.Headers.WWWAuthenticate =
                $"Bearer realm=\"http://{fake!.RegistryHost}/token\",service=\"fake-registry\"";
            return Results.StatusCode((int)HttpStatusCode.Unauthorized);
        });
        app.MapGet("/token", (HttpContext http) =>
        {
            fake!._tokenQueries.Enqueue(http.Request.QueryString.Value ?? string.Empty);
            fake._tokenAuthorizations.Enqueue(http.Request.Headers.Authorization.ToString());
            return fake.RefuseTokens
                ? Results.StatusCode((int)HttpStatusCode.Unauthorized)
                : Results.Json(new { token = IssuedToken, expires_in = fake.ExpiresInSeconds });
        });
        await app.StartAsync();
        var address = new Uri(app.Services.GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>()
            .Features.Get<IServerAddressesFeature>()!.Addresses.First());
        fake = new TokenAuthRegistryFake(app, $"127.0.0.1:{address.Port}");
        return fake;
    }

    public async ValueTask DisposeAsync() => await _app.DisposeAsync();
}
