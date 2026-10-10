using System.Security.Cryptography;
using Amazon;
using Amazon.Runtime;
using Amazon.S3;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Microsoft.Extensions.Options;
using SocAlytics.Platform.Application.Abstractions.ObjectStorage;
using SocAlytics.Platform.Infrastructure.ObjectStorage;
using Xunit;

[assembly: AssemblyFixture(typeof(SocAlytics.Platform.Integration.Tests.Recordings.RustFsContainerFixture))]

namespace SocAlytics.Platform.Integration.Tests.Recordings;

public sealed class RustFsContainerFixture : IAsyncLifetime
{
    private const int ApiPort = 9000;

    private IContainer? _container;
    private string? _serviceUrl;
    private string? _region;
    private string? _accessKey;
    private string? _secretKey;
    private string? _bucket;

    public string ServiceUrl => _serviceUrl ?? NotStarted();

    public string Region => _region ?? NotStarted();

    public string AccessKey => _accessKey ?? NotStarted();

    public string SecretKey => _secretKey ?? NotStarted();

    public string Bucket => _bucket ?? NotStarted();

    public async ValueTask InitializeAsync()
    {
        var external = ReadExternal();
        if (external is { } store)
        {
            (_serviceUrl, _region, _accessKey, _secretKey, _bucket) = store;
            return;
        }

        _accessKey = Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();
        _secretKey = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        _region = "us-east-1";
        _bucket = "rec-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(6)).ToLowerInvariant();

        // Fails visibly when Docker is unavailable; there is no fallback store.
        _container = new ContainerBuilder("rustfs/rustfs:1.0.1")
            .WithPortBinding(ApiPort, true)
            .WithEnvironment("RUSTFS_ACCESS_KEY", _accessKey)
            .WithEnvironment("RUSTFS_SECRET_KEY", _secretKey)
            .WithWaitStrategy(Wait.ForUnixContainer()
                .UntilHttpRequestIsSucceeded(request => request.ForPath("/health").ForPort(ApiPort)))
            .Build();
        await _container.StartAsync(CancellationToken.None);

        _serviceUrl = $"http://{_container.Hostname}:{_container.GetMappedPublicPort(ApiPort)}";
        using var client = CreateS3Client();
        await client.PutBucketAsync(_bucket);
    }

    public async ValueTask DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }

    public AmazonS3Client CreateS3Client() =>
        new(
            new BasicAWSCredentials(AccessKey, SecretKey),
            new AmazonS3Config
            {
                ServiceURL = ServiceUrl,
                ForcePathStyle = true,
                AuthenticationRegion = Region,
                UseHttp = ServiceUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase),
                RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED,
                ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED,
            });

    public IObjectStorage CreateObjectStorage() => CreateObjectStorage(ServiceUrl);

    internal IObjectStorage CreateObjectStorage(string serviceUrl) =>
        new S3ObjectStorage(Options.Create(new ObjectStorageOptions
        {
            ServiceUrl = serviceUrl,
            Region = Region,
            AccessKey = AccessKey,
            SecretKey = SecretKey,
            Bucket = Bucket,
            ForcePathStyle = true,
        }));

    private static (string Url, string Region, string Access, string Secret, string Bucket)? ReadExternal()
    {
        var values = new[]
        {
            "SOCALYTICS_CONFORMANCE_S3_SERVICE_URL",
            "SOCALYTICS_CONFORMANCE_S3_REGION",
            "SOCALYTICS_CONFORMANCE_S3_ACCESS_KEY",
            "SOCALYTICS_CONFORMANCE_S3_SECRET_KEY",
            "SOCALYTICS_CONFORMANCE_S3_BUCKET",
        }.Select(Environment.GetEnvironmentVariable).ToArray();

        return values.All(v => !string.IsNullOrWhiteSpace(v))
            ? (values[0]!, values[1]!, values[2]!, values[3]!, values[4]!)
            : null;
    }

    private static string NotStarted() =>
        throw new InvalidOperationException("The object store has not been started.");
}
