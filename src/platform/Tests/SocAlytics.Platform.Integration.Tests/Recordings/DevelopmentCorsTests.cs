using System.Net;
using System.Security.Cryptography;
using Amazon;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using DotNet.Testcontainers.Builders;
using Shouldly;
using Xunit;

namespace SocAlytics.Platform.Integration.Tests.Recordings;

// Proves that RUSTFS_CORS_ALLOWED_ORIGINS (the AppHost's development setting) covers the browser preflight of a
// presigned part PUT. It starts its own container because the shared fixture sets no allow-list.
public sealed class DevelopmentCorsTests
{
    private const int ApiPort = 9000;
    private const string AllowedOrigin = "http://localhost:4174";
    private const string OtherOrigin = "http://evil.example";

    [Fact]
    public async Task PartPutPreflightSucceedsForAllowedOriginAndIsRefusedForAnotherOrigin()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var accessKey = Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();
        var secretKey = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        var bucket = "cors-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(6)).ToLowerInvariant();

        // Fails visibly when Docker is unavailable; there is no fallback store.
        await using var container = new ContainerBuilder("rustfs/rustfs:1.0.1")
            .WithPortBinding(ApiPort, true)
            .WithEnvironment("RUSTFS_ACCESS_KEY", accessKey)
            .WithEnvironment("RUSTFS_SECRET_KEY", secretKey)
            .WithEnvironment("RUSTFS_CORS_ALLOWED_ORIGINS", AllowedOrigin)
            .WithWaitStrategy(Wait.ForUnixContainer()
                .UntilHttpRequestIsSucceeded(request => request.ForPath("/health").ForPort(ApiPort)))
            .Build();
        await container.StartAsync(timeout.Token);

        var serviceUrl = $"http://{container.Hostname}:{container.GetMappedPublicPort(ApiPort)}";
        using var s3 = new AmazonS3Client(
            new BasicAWSCredentials(accessKey, secretKey),
            new AmazonS3Config
            {
                ServiceURL = serviceUrl,
                ForcePathStyle = true,
                AuthenticationRegion = "us-east-1",
                UseHttp = true,
                RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED,
                ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED,
            });
        await s3.PutBucketAsync(bucket, timeout.Token);

        var partUrl = await s3.GetPreSignedURLAsync(new GetPreSignedUrlRequest
        {
            BucketName = bucket,
            Key = "recordings/part-1",
            Verb = HttpVerb.PUT,
            Expires = DateTime.UtcNow.AddMinutes(10),
            Protocol = Protocol.HTTP,
        });

        using var http = new HttpClient();

        using var allowed = await PreflightAsync(http, partUrl, AllowedOrigin, timeout.Token);
        allowed.IsSuccessStatusCode.ShouldBeTrue();
        allowed.Headers.GetValues("Access-Control-Allow-Origin").ShouldBe([AllowedOrigin]);
        allowed.Headers.GetValues("Access-Control-Allow-Methods")
            .SelectMany(static value => value.Split(',', StringSplitOptions.TrimEntries))
            .ShouldContain(static method => method == "PUT" || method == "*");
        allowed.Headers.GetValues("Access-Control-Allow-Headers")
            .SelectMany(static value => value.Split(',', StringSplitOptions.TrimEntries))
            .ShouldContain(static header => header.Equals("x-amz-checksum-sha256", StringComparison.OrdinalIgnoreCase) || header == "*");

        using var refused = await PreflightAsync(http, partUrl, OtherOrigin, timeout.Token);
        (refused.StatusCode == HttpStatusCode.Forbidden
            || !refused.Headers.TryGetValues("Access-Control-Allow-Origin", out var origins)
            || !origins.Any(static origin => origin == OtherOrigin || origin == "*")).ShouldBeTrue();
    }

    private static Task<HttpResponseMessage> PreflightAsync(HttpClient http, string url, string origin, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Options, url);
        request.Headers.Add("Origin", origin);
        request.Headers.Add("Access-Control-Request-Method", "PUT");
        request.Headers.Add("Access-Control-Request-Headers", "x-amz-checksum-sha256");
        return http.SendAsync(request, ct);
    }
}
