using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Images;

namespace SocAlytics.Platform.EndToEnd.Tests.Support;

/// <summary>Builds the reference Analyst image from the repository sources with Testcontainers and pushes it to a registry.</summary>
internal static class ReferenceAnalystImage
{
    public const string CapabilityId = "reference-media-probe";
    public const string ProfileId = "reference-media-probe-dev";
    public const string ModelDigest = "sha-256:0000000000000000000000000000000000000000000000000000000000000000";

    private const string CapabilityPath = "src/analysts/capabilities/reference-media-probe";
    private const string SdkPath = "src/analysts/sdk";
    private const string DockerfilePath = CapabilityPath + "/Dockerfile";

    /// <summary>The 1.0.0 image from <c>Dockerfile</c> (manifest 1.1.0), which declares no payload schemas.</summary>
    public static readonly ReferenceAnalystVariant Baseline = new(DockerfilePath, CapabilityPath + "/analyst-manifest.json", "1.0.0");

    /// <summary>The 1.1.0 image from <c>Dockerfile.payload-validated</c> (manifest 1.2.0), which declares a payload schema.</summary>
    public static readonly ReferenceAnalystVariant PayloadValidated = new(
        CapabilityPath + "/Dockerfile.payload-validated", CapabilityPath + "/analyst-manifest.payload-validated.json", "1.1.0");

    private static readonly HashSet<string> ExcludedDirectories = new(StringComparer.Ordinal)
    {
        ".venv",
        "__pycache__",
        ".pytest_cache",
        "dist",
        "tests",
    };

    /// <summary>
    /// Assembles a temporary build context with the repository-relative paths the Dockerfile expects, builds the image
    /// with the Dockerfile's pinned build arguments, and pushes it exactly as <see cref="TestAnalystImage"/> does.
    /// </summary>
    public static async Task<PushedAnalystImage> BuildAndPushAsync(string registryHost, string repositoryPrefix, CancellationToken ct, ReferenceAnalystVariant? variant = null)
    {
        variant ??= Baseline;
        var repositoryRoot = FindRepositoryRoot();
        var contextDirectory = Path.Combine(Path.GetTempPath(), $"socalytics-e2e-reference-analyst-{Guid.NewGuid():N}");
        Directory.CreateDirectory(contextDirectory);

        var repository = repositoryPrefix + CapabilityId;
        var localName = $"{registryHost}/{repository}:{Guid.NewGuid():N}";
        try
        {
            foreach (var relative in new[] { SdkPath, CapabilityPath })
            {
                CopyDirectory(Path.Combine(repositoryRoot, relative), Path.Combine(contextDirectory, relative));
            }

            var image = new ImageFromDockerfileBuilder()
                .WithDockerfileDirectory(contextDirectory)
                .WithDockerfile(variant.DockerfilePath)
                .WithName(new DockerImage(localName))
                .WithDeleteIfExists(false)
                .Build();
            await image.CreateAsync(ct);
        }
        finally
        {
            Directory.Delete(contextDirectory, true);
        }

        var tag = localName[(localName.LastIndexOf(':') + 1)..];
        var name = localName[..localName.LastIndexOf(':')];
        using var docker = new DockerEngine();
        var digest = await docker.PushAsync(name, tag, ct);
        return new PushedAnalystImage(repository, digest, localName);
    }

    public static Task RemoveAsync(PushedAnalystImage image) => TestAnalystImage.RemoveAsync(image);

    internal static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, DockerfilePath)))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException($"The repository root with {DockerfilePath} was not found above {AppContext.BaseDirectory}.");
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source))
        {
            if (!file.EndsWith(".pyc", StringComparison.Ordinal))
            {
                File.Copy(file, Path.Combine(destination, Path.GetFileName(file)));
            }
        }

        foreach (var directory in Directory.EnumerateDirectories(source))
        {
            var name = Path.GetFileName(directory);
            if (!ExcludedDirectories.Contains(name))
            {
                CopyDirectory(directory, Path.Combine(destination, name));
            }
        }
    }
}

/// <summary>One buildable form of the reference Analyst: its Dockerfile, the manifest it carries, and its capability version.</summary>
internal sealed record ReferenceAnalystVariant(string DockerfilePath, string ManifestPath, string CapabilityVersion);
