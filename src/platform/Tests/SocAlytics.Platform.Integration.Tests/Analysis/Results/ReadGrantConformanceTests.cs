using System.Net;
using System.Security.Cryptography;
using Shouldly;
using SocAlytics.Platform.Application.Abstractions.ObjectStorage;
using SocAlytics.Platform.Domain.Recordings;
using SocAlytics.Platform.Integration.Tests.Recordings;
using Xunit;

namespace SocAlytics.Platform.Integration.Tests.Analysis.Results;

public sealed class ReadGrantConformanceTests(RustFsContainerFixture store)
{
    private static readonly HttpClient Http = new();

    private readonly IObjectStorage _storage = store.CreateObjectStorage();

    [Fact]
    public async Task Grant_reads_the_object_before_expiry()
    {
        var (key, body) = await StoreAsync();

        var grant = _storage.PresignGetObject(key, DateTimeOffset.UtcNow.AddMinutes(5));

        using var response = await Http.GetAsync(grant.Url, TestContext.Current.CancellationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken)).ShouldBe(body);
    }

    [Fact]
    public async Task Grant_is_refused_after_expiry()
    {
        var (key, _) = await StoreAsync();

        var grant = _storage.PresignGetObject(key, DateTimeOffset.UtcNow.AddSeconds(2));
        await Task.Delay(TimeSpan.FromSeconds(4), TestContext.Current.CancellationToken);

        using var response = await Http.GetAsync(grant.Url, TestContext.Current.CancellationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Grant_is_refused_for_another_key()
    {
        var (key, _) = await StoreAsync();
        var (other, _) = await StoreAsync();

        var grant = _storage.PresignGetObject(key, DateTimeOffset.UtcNow.AddMinutes(5));
        var swapped = new UriBuilder(grant.Url) { Path = grant.Url.AbsolutePath.Replace(key, other, StringComparison.Ordinal) }.Uri;
        swapped.ShouldNotBe(grant.Url);

        using var response = await Http.GetAsync(swapped, TestContext.Current.CancellationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    private async Task<(string Key, byte[] Body)> StoreAsync()
    {
        var body = RandomNumberGenerator.GetBytes(2048);
        var key = $"results/{Guid.NewGuid():N}";
        var grant = _storage.PresignPutObject(
            key, body.Length, Sha256Digest.FromBytes(SHA256.HashData(body)), DateTimeOffset.UtcNow.AddMinutes(5));

        using var request = new HttpRequestMessage(HttpMethod.Put, grant.Url) { Content = new ByteArrayContent(body) };
        foreach (var (name, value) in grant.RequiredHeaders)
        {
            if (!name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
            {
                request.Headers.TryAddWithoutValidation(name, value);
            }
        }

        using var response = await Http.SendAsync(request, TestContext.Current.CancellationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return (key, body);
    }
}
