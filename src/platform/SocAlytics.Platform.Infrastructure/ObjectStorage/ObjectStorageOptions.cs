namespace SocAlytics.Platform.Infrastructure.ObjectStorage;

internal sealed class ObjectStorageOptions
{
    public string ServiceUrl { get; set; } = string.Empty;

    public string Region { get; set; } = "us-east-1";

    public string AccessKey { get; set; } = string.Empty;

    public string SecretKey { get; set; } = string.Empty;

    public string Bucket { get; set; } = string.Empty;

    public bool ForcePathStyle { get; set; } = true;

    public bool EnsureBucketOnStartup { get; set; }
}
