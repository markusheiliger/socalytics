using System.Security.Cryptography;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Images;

namespace SocAlytics.Platform.EndToEnd.Tests.Support;

/// <summary>A pushed Test Analyst image: the registry repository and its manifest digest (<c>sha256:</c> form).</summary>
internal sealed record PushedAnalystImage(string Repository, string Digest, string LocalName)
{
    public string Reference(string registryHost) => $"{registryHost}/{Repository}@{Digest}";
}

/// <summary>Builds the Test Analyst image with Testcontainers and pushes it to a registry through the Docker Engine API.</summary>
internal static class TestAnalystImage
{
    public const string CapabilityId = "e2e-docker-analyst";
    public const string ProfileId = "e2e-docker-profile";
    public const string ModelDigest = "sha-256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    public static string CapabilityFor(string mode) => $"{CapabilityId}-{mode}";

    public static string ProfileFor(string mode) => $"{ProfileId}-{mode}";

    /// <summary>
    /// Builds the image for <paramref name="mode"/>. The default mode <c>succeed</c> is the capability <see cref="CapabilityId"/>;
    /// every other mode is imported as its own capability with a manifest that allows the short limits of the failure tests.
    /// </summary>
    public static async Task<PushedAnalystImage> BuildAndPushAsync(string registryHost, string repositoryPrefix, CancellationToken ct, string mode = "succeed")
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "TestAnalyst");
        var capabilityId = CapabilityId;
        string? contextDirectory = null;
        var manifestVersion = "1.0.0";
        if (mode != "succeed")
        {
            capabilityId = CapabilityFor(mode);
            contextDirectory = Path.Combine(Path.GetTempPath(), $"socalytics-e2e-analyst-{Guid.NewGuid():N}");
            Directory.CreateDirectory(contextDirectory);
            foreach (var file in new[] { "Dockerfile", "analyst.py" })
            {
                File.Copy(Path.Combine(directory, file), Path.Combine(contextDirectory, file));
            }

            var manifest = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(directory, "analyst-manifest.json"), ct))!;
            manifest["capability"]!["id"] = capabilityId;
            manifest["compatibleProfiles"] = new System.Text.Json.Nodes.JsonArray(ProfileFor(mode));
            var requirements = manifest["requirements"]!;
            requirements["minMaxAttempts"] = 1;
            requirements["minLeaseSeconds"] = 1;
            requirements["minStaleTimeoutSeconds"] = 1;
            if (mode is "media" or "payload-invalid")
            {
                manifest["videoAccess"] = "required";
                manifest["capability"]!["tier"] = "low-level";
                manifest["capability"]!["executionScope"] = "segment";
            }

            if (mode == "payload-invalid")
            {
                // The registered schema is stricter than the facts the Analyst writes (a media observation has a positive size).
                manifestVersion = "1.2.0";
                manifest["manifestVersion"] = manifestVersion;
                manifest["output"]!["factPayloadSchemas"] = System.Text.Json.Nodes.JsonNode.Parse("""
                    {
                      "e2e-media-observation": {
                        "type": "object",
                        "required": ["sizeBytes"],
                        "properties": { "sizeBytes": { "type": "integer", "maximum": 0 } }
                      }
                    }
                    """);
            }

            await File.WriteAllTextAsync(Path.Combine(contextDirectory, "analyst-manifest.json"), manifest.ToJsonString(), ct);
            directory = contextDirectory;
        }

        var manifestHash = Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(Path.Combine(directory, "analyst-manifest.json"), ct)));
        var repository = repositoryPrefix + capabilityId;
        var localName = $"{registryHost}/{repository}:{Guid.NewGuid():N}";

        var image = new ImageFromDockerfileBuilder()
            .WithDockerfileDirectory(directory)
            .WithDockerfile("Dockerfile")
            .WithBuildArgument("MODE", mode)
            .WithBuildArgument("MANIFEST_SHA256", manifestHash)
            .WithBuildArgument("CAPABILITY_ID", capabilityId)
            .WithBuildArgument("MANIFEST_VERSION", manifestVersion)
            .WithName(new DockerImage(localName))
            .WithDeleteIfExists(false)
            .Build();
        try
        {
            await image.CreateAsync(ct);
        }
        finally
        {
            if (contextDirectory is not null)
            {
                Directory.Delete(contextDirectory, true);
            }
        }

        var tag = localName[(localName.LastIndexOf(':') + 1)..];
        var name = localName[..localName.LastIndexOf(':')];
        using var docker = new DockerEngine();
        var digest = await docker.PushAsync(name, tag, ct);
        return new PushedAnalystImage(repository, digest, localName);
    }

    public static async Task RemoveAsync(PushedAnalystImage image)
    {
        using var docker = new DockerEngine();
        await docker.DeleteImageAsync(image.LocalName);
    }
}
