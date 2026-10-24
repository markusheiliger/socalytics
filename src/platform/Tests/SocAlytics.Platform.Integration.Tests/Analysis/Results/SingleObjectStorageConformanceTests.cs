using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using Shouldly;
using SocAlytics.Platform.Application.Abstractions.ObjectStorage;
using SocAlytics.Platform.Domain.Recordings;
using SocAlytics.Platform.Integration.Tests.Recordings;
using Xunit;

namespace SocAlytics.Platform.Integration.Tests.Analysis.Results;

public sealed class SingleObjectStorageConformanceTests(RustFsContainerFixture store)
{
    private static readonly HttpClient Http = new();

    private readonly IObjectStorage _storage = store.CreateObjectStorage();

    [Fact]
    public async Task Correct_put_reports_full_object_checksum_and_length()
    {
        var (key, grant, body, digest) = Prepare();

        (await PutAsync(grant, body)).StatusCode.ShouldBe(HttpStatusCode.OK);

        var checksum = await _storage.GetObjectChecksumAsync(key, CancellationToken.None);
        checksum.ShouldNotBeNull();
        checksum.Digest.ShouldBe(digest);
        checksum.ContentLength.ShouldBe(body.Length);
    }

    [Fact]
    public async Task Wrong_body_is_refused_and_stores_nothing()
    {
        var (key, grant, body, _) = Prepare();
        var flipped = (byte[])body.Clone();
        flipped[0] ^= 0xFF;

        (await PutAsync(grant, flipped)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        (await _storage.GetObjectChecksumAsync(key, CancellationToken.None)).ShouldBeNull();
    }

    [Fact]
    public async Task Wrong_length_is_refused_and_stores_nothing()
    {
        var (key, grant, body, _) = Prepare();
        var larger = new byte[body.Length + 512];
        body.CopyTo(larger, 0);

        (await PutAsync(grant, larger, h => h["Content-Length"] = larger.Length.ToString(CultureInfo.InvariantCulture)))
            .StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        (await _storage.GetObjectChecksumAsync(key, CancellationToken.None)).ShouldBeNull();
    }

    [Fact]
    public async Task Missing_or_wrong_checksum_header_is_refused_and_stores_nothing()
    {
        var (key, grant, body, _) = Prepare();
        var other = Sha256Digest.FromBytes(SHA256.HashData([1, 2, 3])).ToBase64();

        (await PutAsync(grant, body, h => h.Remove("x-amz-checksum-sha256"))).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await PutAsync(grant, body, h => h["x-amz-checksum-sha256"] = other)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        (await _storage.GetObjectChecksumAsync(key, CancellationToken.None)).ShouldBeNull();
    }

    [Fact]
    public async Task Bounded_read_returns_content_within_the_limit_and_refuses_oversize_objects()
    {
        var (key, grant, body, _) = Prepare();
        (await PutAsync(grant, body)).StatusCode.ShouldBe(HttpStatusCode.OK);

        var exact = await _storage.ReadObjectAsync(key, body.Length, CancellationToken.None);
        exact.ShouldNotBeNull();
        exact.TooLarge.ShouldBeFalse();
        exact.Content.ShouldBe(body);

        var oversize = await _storage.ReadObjectAsync(key, body.Length - 1, CancellationToken.None);
        oversize.ShouldNotBeNull();
        oversize.TooLarge.ShouldBeTrue();
        oversize.Content.ShouldBeNull();
    }

    [Fact]
    public async Task Streaming_put_stores_the_object_with_a_full_object_checksum_and_length()
    {
        var body = RandomNumberGenerator.GetBytes(300_000);
        var digest = Sha256Digest.FromBytes(SHA256.HashData(body));
        var key = $"segments/{Guid.NewGuid():N}";

        await _storage.PutObjectAsync(key, "video/mp4", new MemoryStream(body), body.Length, digest, CancellationToken.None);

        var checksum = await _storage.GetObjectChecksumAsync(key, CancellationToken.None);
        checksum.ShouldNotBeNull();
        checksum.Digest.ShouldBe(digest);
        checksum.ContentLength.ShouldBe(body.Length);
        (await _storage.ReadObjectAsync(key, body.Length, CancellationToken.None))!.Content.ShouldBe(body);
    }

    [Fact]
    public async Task Streaming_put_with_bytes_that_do_not_match_the_digest_is_rejected_and_stores_nothing()
    {
        var body = RandomNumberGenerator.GetBytes(4096);
        var digest = Sha256Digest.FromBytes(SHA256.HashData(body));
        var flipped = (byte[])body.Clone();
        flipped[0] ^= 0xFF;
        var key = $"segments/{Guid.NewGuid():N}";

        var rejection = await Should.ThrowAsync<ObjectStorageRejectedException>(() =>
            _storage.PutObjectAsync(key, "video/mp4", new MemoryStream(flipped), flipped.Length, digest, CancellationToken.None));

        rejection.Reason.ShouldBe(ObjectStorageRejectionReason.BadDigest);
        (await _storage.GetObjectChecksumAsync(key, CancellationToken.None)).ShouldBeNull();
    }

    [Fact]
    public async Task Missing_object_reports_null()
    {
        var key = $"results/{Guid.NewGuid():N}";

        (await _storage.GetObjectChecksumAsync(key, CancellationToken.None)).ShouldBeNull();
        (await _storage.ReadObjectAsync(key, 1024, CancellationToken.None)).ShouldBeNull();
    }

    private (string Key, ObjectUploadGrant Grant, byte[] Body, Sha256Digest Digest) Prepare()
    {
        var body = RandomNumberGenerator.GetBytes(4096);
        var digest = Sha256Digest.FromBytes(SHA256.HashData(body));
        var key = $"results/{Guid.NewGuid():N}";
        return (key, _storage.PresignPutObject(key, body.Length, digest, DateTimeOffset.UtcNow.AddMinutes(5)), body, digest);
    }

    private static async Task<HttpResponseMessage> PutAsync(
        ObjectUploadGrant grant, byte[] body, Action<Dictionary<string, string>>? headers = null)
    {
        var sent = grant.RequiredHeaders.ToDictionary(p => p.Key, p => p.Value);
        headers?.Invoke(sent);

        using var request = new HttpRequestMessage(HttpMethod.Put, grant.Url) { Content = new ByteArrayContent(body) };
        foreach (var (name, value) in sent)
        {
            if (name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
            {
                request.Content.Headers.ContentLength = long.Parse(value, CultureInfo.InvariantCulture);
            }
            else
            {
                request.Headers.TryAddWithoutValidation(name, value);
            }
        }

        return await Http.SendAsync(request, TestContext.Current.CancellationToken);
    }
}
