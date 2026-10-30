using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Images;

namespace SocAlytics.Platform.EndToEnd.Tests.Support;

/// <summary>Builds the shot-boundary Analyst image from the repository sources with Testcontainers and pushes it to a registry.</summary>
internal static class ShotBoundaryAnalystImage
{
    public const string CapabilityId = "media-context-classification";
    public const string CapabilityVersion = "1.0.0";
    public const string ProfileId = "media-context-classification-dev";
    public const string FactKind = "shot-boundary";
    public const string ModelDigest = "sha-256:0000000000000000000000000000000000000000000000000000000000000000";

    public const string ManifestPath = CapabilityPath + "/analyst-manifest.json";

    private const string CapabilityPath = "src/analysts/capabilities/media-context-classification";
    private const string SdkPath = "src/analysts/sdk";
    private const string DockerfilePath = CapabilityPath + "/Dockerfile";

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
    public static async Task<PushedAnalystImage> BuildAndPushAsync(string registryHost, string repositoryPrefix, CancellationToken ct)
    {
        // The one place of this helper that takes the gate: the build and the push of the image run together under it.
        using var lease = await ImageBuildGate.Shared.AcquireAsync(ct);
        var repositoryRoot = FindRepositoryRoot();
        var contextDirectory = Path.Combine(Path.GetTempPath(), $"socalytics-e2e-shot-boundary-analyst-{Guid.NewGuid():N}");
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
                .WithDockerfile(DockerfilePath)
                .WithName(new DockerImage(localName))
                .WithDeleteIfExists(false)
                .Build();
            // The Testcontainers build client has no request timeout of its own, so BuildLimit and the test's token end the build.
            await EngineCallPolicy.ExecuteAsync(
                $"Building the shot-boundary Analyst image {localName}",
                nameof(EngineCallPolicy.BuildLimit),
                EngineCallPolicy.BuildLimit,
                async buildToken =>
                {
                    await image.CreateAsync(buildToken);
                    return true;
                },
                ct);
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

    /// <summary>The cache key of the image built for <paramref name="repositoryPrefix"/>.</summary>
    public static AnalystImageKey CacheKey(string repositoryPrefix) =>
        new(nameof(ShotBoundaryAnalystImage), "default", repositoryPrefix);

    /// <summary>Returns the image from the class's <paramref name="cache"/>, building and pushing it once to the cache's registry.</summary>
    public static Task<PushedAnalystImage> GetCachedAsync(AnalystImageCache cache, string repositoryPrefix, CancellationToken ct) =>
        cache.GetOrBuildAsync(
            CacheKey(repositoryPrefix),
            (registryHost, buildToken) => BuildAndPushAsync(registryHost, repositoryPrefix, buildToken),
            ct);

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
