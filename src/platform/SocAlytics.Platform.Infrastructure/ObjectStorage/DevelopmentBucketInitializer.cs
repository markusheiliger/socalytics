using Amazon.S3;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace SocAlytics.Platform.Infrastructure.ObjectStorage;

internal sealed class DevelopmentBucketInitializer(IOptions<ObjectStorageOptions> options) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var value = options.Value;
        if (!value.EnsureBucketOnStartup)
        {
            return;
        }

        using var client = new AmazonS3Client(
            new Amazon.Runtime.BasicAWSCredentials(value.AccessKey, value.SecretKey),
            new AmazonS3Config
            {
                ServiceURL = value.ServiceUrl,
                ForcePathStyle = value.ForcePathStyle,
                AuthenticationRegion = value.Region,
                UseHttp = value.ServiceUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase),
            });

        try
        {
            await client.PutBucketAsync(value.Bucket, cancellationToken);
        }
        catch (AmazonS3Exception ex) when (ex.ErrorCode is "BucketAlreadyOwnedByYou" or "BucketAlreadyExists")
        {
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
