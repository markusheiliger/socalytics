using System.Security.Cryptography;
using Shouldly;
using SocAlytics.Platform.Application.Abstractions.ObjectStorage;
using SocAlytics.Platform.Domain.Recordings;
using Xunit;

namespace SocAlytics.Platform.Integration.Tests.Recordings;

public sealed class ObjectStoreConformanceTests(RustFsContainerFixture store)
{
    private static readonly HttpClient Http = new();

    private readonly IObjectStorage _storage = store.CreateObjectStorage();

    [Fact]
    public async Task Valid_part_upload_is_idempotent_and_completes_with_composite_evidence()
    {
        var (upload, grant, body, digest) = await StartAsync();

        (await PutAsync(grant, body)).StatusCode.ShouldBe(System.Net.HttpStatusCode.OK);
        (await PutAsync(grant, body)).StatusCode.ShouldBe(System.Net.HttpStatusCode.OK);

        var parts = await _storage.ListPartsAsync(upload, null, CancellationToken.None);
        var part = parts.ShouldHaveSingleItem();
        part.PartNumber.ShouldBe(1);
        part.SizeBytes.ShouldBe(body.Length);
        part.ETag.ShouldNotBeNullOrEmpty();

        var composite = CompositeContentDigest.FromPartDigests(body.Length, [digest]);
        await _storage.CompleteMultipartUploadAsync(
            upload, [new CompletedPartEntry(1, part.ETag, digest)], composite, body.Length, CancellationToken.None);

        var evidence = await _storage.GetIntegrityEvidenceAsync(upload.ObjectKey, CancellationToken.None);
        evidence.ShouldNotBeNull();
        evidence.Checksum.ShouldBe(composite.ToS3ChecksumValue());
        evidence.ChecksumType.ShouldBe("COMPOSITE");
        evidence.ContentLength.ShouldBe(body.Length);
    }

    [Fact]
    public async Task Flipped_bytes_are_rejected_with_bad_digest_and_store_nothing()
    {
        var (upload, grant, body, _) = await StartAsync();
        var flipped = (byte[])body.Clone();
        flipped[0] ^= 0xFF;

        var response = await PutAsync(grant, flipped);

        response.StatusCode.ShouldBe(System.Net.HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).ShouldContain("BadDigest");
        await AssertNothingStoredAsync(upload);
    }

    [Fact]
    public async Task Wrong_or_missing_checksum_header_is_refused_and_stores_nothing()
    {
        var (upload, grant, body, _) = await StartAsync();
        var other = Sha256Digest.FromBytes(SHA256.HashData([1, 2, 3])).ToBase64();

        (await PutAsync(grant, body, headers: h => h["x-amz-checksum-sha256"] = other))
            .StatusCode.ShouldBe(System.Net.HttpStatusCode.Forbidden);
        await AssertNothingStoredAsync(upload);

        (await PutAsync(grant, body, headers: h => h.Remove("x-amz-checksum-sha256")))
            .StatusCode.ShouldBe(System.Net.HttpStatusCode.Forbidden);
        await AssertNothingStoredAsync(upload);
    }

    [Fact]
    public async Task Wrong_content_length_and_oversize_bodies_are_refused_and_store_nothing()
    {
        var (upload, grant, body, _) = await StartAsync();

        var larger = new byte[body.Length + 512];
        body.CopyTo(larger, 0);
        (await PutAsync(grant, larger, h => h["Content-Length"] = larger.Length.ToString(System.Globalization.CultureInfo.InvariantCulture)))
            .StatusCode.ShouldBe(System.Net.HttpStatusCode.Forbidden);
        await AssertNothingStoredAsync(upload);

        (await RawPutStatusAsync(grant, body[..(body.Length / 2)], closeAfterSend: true)).ShouldNotBe(200);
        await AssertNothingStoredAsync(upload);
    }

    [Fact]
    public async Task Chunked_transfer_is_refused_and_stores_nothing()
    {
        var (upload, grant, body, _) = await StartAsync();

        using var request = new HttpRequestMessage(HttpMethod.Put, grant.Url);
        request.Headers.TransferEncodingChunked = true;
        request.Content = new StreamContent(new NonSeekableStream(body));
        request.Headers.TryAddWithoutValidation("x-amz-checksum-sha256", grant.RequiredHeaders["x-amz-checksum-sha256"]);
        using var response = await Http.SendAsync(request, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(System.Net.HttpStatusCode.Forbidden);
        await AssertNothingStoredAsync(upload);
    }

    [Fact]
    public async Task Tampered_part_number_is_refused_and_stores_nothing()
    {
        var (upload, grant, body, _) = await StartAsync();
        var tampered = grant with { Url = new Uri(grant.Url.AbsoluteUri.Replace("partNumber=1", "partNumber=2", StringComparison.Ordinal)) };

        (await PutAsync(tampered, body)).StatusCode.ShouldBe(System.Net.HttpStatusCode.Forbidden);
        await AssertNothingStoredAsync(upload);
    }

    [Fact]
    public async Task Part_url_cannot_be_used_for_other_operations()
    {
        var (_, grant, _, _) = await StartAsync();

        foreach (var method in new[] { HttpMethod.Get, HttpMethod.Delete, HttpMethod.Post })
        {
            using var request = new HttpRequestMessage(method, grant.Url);
            using var response = await Http.SendAsync(request, TestContext.Current.CancellationToken);
            response.StatusCode.ShouldBe(System.Net.HttpStatusCode.Forbidden, method.Method);
        }

        var withoutPart = new Uri(System.Text.RegularExpressions.Regex.Replace(grant.Url.AbsoluteUri, "[?&]partNumber=\\d+", string.Empty));
        using var list = await Http.GetAsync(withoutPart, TestContext.Current.CancellationToken);
        list.StatusCode.ShouldBe(System.Net.HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Abort_makes_list_parts_fail_with_no_such_upload()
    {
        var (upload, _, _, _) = await StartAsync();

        await _storage.AbortMultipartUploadAsync(upload, CancellationToken.None);

        var ex = await Should.ThrowAsync<ObjectStorageRejectedException>(
            () => _storage.ListPartsAsync(upload, null, CancellationToken.None));
        ex.Reason.ShouldBe(ObjectStorageRejectionReason.NoSuchUpload);
    }

    private async Task<(MultipartUploadReference Upload, PartUploadGrant Grant, byte[] Body, Sha256Digest Digest)> StartAsync()
    {
        var body = RandomNumberGenerator.GetBytes(4096);
        var digest = Sha256Digest.FromBytes(SHA256.HashData(body));
        var upload = await _storage.InitiateCompositeMultipartUploadAsync(
            $"conformance/{Guid.NewGuid():N}", "video/mp4", CancellationToken.None);
        var grant = _storage.PresignUploadPart(upload, 1, body.Length, digest, DateTimeOffset.UtcNow.AddMinutes(5));
        return (upload, grant, body, digest);
    }

    private static async Task<HttpResponseMessage> PutAsync(
        PartUploadGrant grant, byte[] body, Action<Dictionary<string, string>>? headers = null)
    {
        var sent = grant.RequiredHeaders.ToDictionary(p => p.Key, p => p.Value);
        headers?.Invoke(sent);

        using var request = new HttpRequestMessage(HttpMethod.Put, grant.Url) { Content = new ByteArrayContent(body) };
        foreach (var (name, value) in sent)
        {
            if (name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
            {
                // Declared length from the grant; the body may deliberately differ in size.
                request.Content.Headers.ContentLength = long.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
            }
            else
            {
                request.Headers.TryAddWithoutValidation(name, value);
            }
        }

        return await Http.SendAsync(request, TestContext.Current.CancellationToken);
    }

    // HttpClient refuses to send a body that disagrees with Content-Length, so write the request by hand.
    private static async Task<int> RawPutStatusAsync(PartUploadGrant grant, byte[] body, bool closeAfterSend = false)
    {
        var ct = TestContext.Current.CancellationToken;
        using var tcp = new System.Net.Sockets.TcpClient();
        await tcp.ConnectAsync(grant.Url.Host, grant.Url.Port, ct);
        var stream = tcp.GetStream();
        var head = $"PUT {grant.Url.PathAndQuery} HTTP/1.1\r\nHost: {grant.Url.Authority}\r\n" +
            string.Concat(grant.RequiredHeaders.Select(h => $"{h.Key}: {h.Value}\r\n")) + "Connection: close\r\n\r\n";
        await stream.WriteAsync(System.Text.Encoding.ASCII.GetBytes(head), ct);
        await stream.WriteAsync(body, ct);
        if (closeAfterSend)
        {
            tcp.Client.Shutdown(System.Net.Sockets.SocketShutdown.Send);
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(10));
        var buffer = new byte[256];
        try
        {
            var read = await stream.ReadAsync(buffer, cts.Token);
            var line = System.Text.Encoding.ASCII.GetString(buffer, 0, read).Split(' ');
            return line.Length > 1 && int.TryParse(line[1], out var code) ? code : 0;
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException)
        {
            return 0;
        }
    }

    private async Task AssertNothingStoredAsync(MultipartUploadReference upload) =>
        (await _storage.ListPartsAsync(upload, null, CancellationToken.None)).ShouldBeEmpty();

    private sealed class NonSeekableStream(byte[] data) : Stream
    {
        private readonly MemoryStream _inner = new(data);

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
