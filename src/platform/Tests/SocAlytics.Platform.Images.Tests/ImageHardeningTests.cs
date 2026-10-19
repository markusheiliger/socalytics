using System.Text.Json;
using System.Text.RegularExpressions;
using Shouldly;
using SocAlytics.Platform.Images.Tests.Support;
using Xunit;

namespace SocAlytics.Platform.Images.Tests;

/// <summary>Provenance and the absence of secret material in every image (spec US4; contracts/container-images.md sections 1 to 3).</summary>
[Collection(ImageBuildCollection.Name)]
public sealed partial class ImageHardeningTests(LayerScanFixture scans) : IClassFixture<LayerScanFixture>
{
    private const string ApiDockerfile = "src/platform/SocAlytics.Platform.Api/Dockerfile";
    private const string MigratorDockerfile = "src/platform/SocAlytics.Platform.Migrator/Dockerfile";
    private const string WebDockerfile = "src/clients/web/Dockerfile";
    private const string ShellManifestPath = "/usr/share/nginx/html/shell-manifest.json";

    // The base images' system CA trust store is the only place certificate material may appear.
    private static readonly string[] TrustStorePrefixes =
    [
        "/etc/ssl/", "/usr/share/ca-certificates/", "/etc/ca-certificates/", "/usr/local/share/ca-certificates/", "/usr/lib/ssl/",
    ];

    public static TheoryData<string, string, string, string> Images => new()
    {
        { ApiDockerfile, "socalytics-api", "1654", "Serves the SocAlytics platform API" },
        { MigratorDockerfile, "socalytics-migrator", "1654", "SocAlytics" },
        { WebDockerfile, "socalytics-web", "101", "SocAlytics Web UI" },
    };

    public static TheoryData<string> Dockerfiles => new() { ApiDockerfile, MigratorDockerfile, WebDockerfile };

    [Theory]
    [MemberData(nameof(Images))]
    public async Task Image_carries_the_provenance_labels_and_runs_as_a_non_root_numeric_user_without_a_healthcheck(
        string dockerfile, string title, string user, string descriptionFragment)
    {
        var image = await scans.ScanAsync(dockerfile);
        var configuration = image.Configuration;

        configuration.Labels["org.opencontainers.image.revision"].ShouldBe(ImageBuildFixture.TestRevision);
        configuration.Labels["org.opencontainers.image.version"].ShouldBe(ImageBuildFixture.TestVersion);
        configuration.Labels["org.opencontainers.image.source"].ShouldBe("https://github.com/markusheiliger/socalytics");
        configuration.Labels["org.opencontainers.image.title"].ShouldBe(title);
        configuration.Labels["org.opencontainers.image.description"].ShouldNotBeNullOrWhiteSpace();
        configuration.Labels["org.opencontainers.image.description"].ShouldContain(descriptionFragment);

        configuration.HasHealthcheck.ShouldBeFalse("the images have no in-container health probe");
        configuration.User.ShouldBe(user);
        NumericUserRegex().IsMatch(configuration.User).ShouldBeTrue();
        configuration.User.ShouldNotBe("0");
    }

    [Fact]
    public async Task The_three_images_of_one_release_carry_the_same_revision_and_version()
    {
        var all = new[] { await scans.ScanAsync(ApiDockerfile), await scans.ScanAsync(MigratorDockerfile), await scans.ScanAsync(WebDockerfile) };

        all.Select(i => i.Configuration.Labels["org.opencontainers.image.revision"]).Distinct().ShouldHaveSingleItem();
        all.Select(i => i.Configuration.Labels["org.opencontainers.image.version"]).Distinct().ShouldHaveSingleItem();
    }

    [Fact]
    public async Task Web_ui_shell_manifest_version_equals_the_version_label()
    {
        var image = await scans.ScanAsync(WebDockerfile);

        var bytes = image.ReadFile(ShellManifestPath);
        bytes.ShouldNotBeNull($"{ShellManifestPath} must be in the Web UI image");
        var version = JsonDocument.Parse(bytes).RootElement.GetProperty("version").GetString();

        version.ShouldBe(image.Configuration.Labels["org.opencontainers.image.version"]);
        version.ShouldBe(ImageBuildFixture.TestVersion);
    }

    [Theory]
    [MemberData(nameof(Dockerfiles))]
    public async Task No_layer_contains_secret_material_development_files_or_test_assets(string dockerfile)
    {
        var image = await scans.ScanAsync(dockerfile);
        var isWebUi = dockerfile == WebDockerfile;

        var findings = new List<string>();
        foreach (var layer in image.Layers)
        {
            foreach (var file in layer.Files.Where(file => !IsTrustStore(file)))
            {
                if (ForbiddenReason(file, isWebUi) is { } reason)
                {
                    findings.Add($"layer {layer.Index}: {file} ({reason})");
                }
            }

            findings.AddRange(layer.PrivateKeyFiles.Where(file => !IsTrustStore(file)).Select(file => $"layer {layer.Index}: {file} (private-key PEM block)"));
        }

        findings.ShouldBeEmpty(string.Join(Environment.NewLine, findings));
    }

    [Theory]
    [MemberData(nameof(Dockerfiles))]
    public async Task Image_configuration_environment_labels_and_history_contain_no_credential_like_values(string dockerfile)
    {
        var image = await scans.ScanAsync(dockerfile);
        var configuration = image.Configuration;

        var texts = configuration.Environment
            .Concat(configuration.Labels.Select(pair => $"{pair.Key}={pair.Value}"))
            .Concat(configuration.History)
            .ToList();

        var findings = texts.Where(text => CredentialRegex().IsMatch(text) || PemBlockRegex().IsMatch(text)).ToList();
        findings.ShouldBeEmpty(string.Join(Environment.NewLine, findings));
        PemBlockRegex().IsMatch(image.ConfigurationJson).ShouldBeFalse();
    }

    [Theory]
    [MemberData(nameof(Dockerfiles))]
    public async Task Build_without_the_revision_fails_naming_the_argument(string dockerfile)
    {
        var result = await BuildAsync(dockerfile, ("SOCALYTICS_VERSION", ImageBuildFixture.TestVersion));

        result.ExitCode.ShouldNotBe(0);
        result.Output.ShouldContain("SOCALYTICS_REVISION");
    }

    [Theory]
    [MemberData(nameof(Dockerfiles))]
    public async Task Build_without_the_version_fails_naming_the_argument(string dockerfile)
    {
        var result = await BuildAsync(dockerfile, ("SOCALYTICS_REVISION", ImageBuildFixture.TestRevision));

        result.ExitCode.ShouldNotBe(0);
        result.Output.ShouldContain("SOCALYTICS_VERSION");
    }

    [Theory]
    [MemberData(nameof(Dockerfiles))]
    public async Task Build_with_a_malformed_version_fails_naming_the_argument(string dockerfile)
    {
        var result = await BuildAsync(
            dockerfile,
            ("SOCALYTICS_REVISION", ImageBuildFixture.TestRevision),
            ("SOCALYTICS_VERSION", "2026-10-18"));

        result.ExitCode.ShouldNotBe(0);
        result.Output.ShouldContain("SOCALYTICS_VERSION");
    }

    private async Task<(int ExitCode, string Output)> BuildAsync(string dockerfile, params (string Name, string Value)[] arguments)
    {
        var command = new List<string> { "build", "-f", dockerfile, "--progress", "plain" };
        foreach (var (name, value) in arguments)
        {
            command.Add("--build-arg");
            command.Add($"{name}={value}");
        }

        command.Add(".");
        return await ImageBuildFixture.RunDockerAsync(scans.Images.RepositoryRoot, [.. command]);
    }

    private static bool IsTrustStore(string path) => TrustStorePrefixes.Any(prefix => path.StartsWith(prefix, StringComparison.Ordinal));

    private static string? ForbiddenReason(string path, bool isWebUi)
    {
        var name = path[(path.LastIndexOf('/') + 1)..];
        var lower = path.ToLowerInvariant();
        var segments = lower.Split('/', StringSplitOptions.RemoveEmptyEntries);

        if (name.EndsWith(".pfx", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".p12", StringComparison.OrdinalIgnoreCase))
        {
            return "certificate bundle with a private key";
        }

        if (name.EndsWith(".key", StringComparison.OrdinalIgnoreCase))
        {
            return "key file";
        }

        if (lower.Contains("aspnet/https") || lower.Contains("aspnetcore-dev") || lower.Contains("dotnet-devcerts"))
        {
            return "ASP.NET Core development certificate";
        }

        if (name.Equals("secrets.json", StringComparison.OrdinalIgnoreCase) || segments.Contains("usersecrets") || segments.Contains(".microsoft"))
        {
            return "user secrets";
        }

        if (name.Equals("appsettings.Development.json", StringComparison.OrdinalIgnoreCase))
        {
            return "development settings";
        }

        if (name.StartsWith(".env", StringComparison.OrdinalIgnoreCase))
        {
            return "environment file";
        }

        if (segments.Contains(".git") || name.Equals(".gitignore", StringComparison.OrdinalIgnoreCase) || name.Equals(".gitattributes", StringComparison.OrdinalIgnoreCase))
        {
            return "repository metadata";
        }

        if (name.EndsWith(".Tests.dll", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".Tests.pdb", StringComparison.OrdinalIgnoreCase))
        {
            return "test assembly";
        }

        if (segments.Contains("fixtures") || segments.Contains("testdata") || segments.Contains("test-fixtures"))
        {
            return "test fixture";
        }

        if (isWebUi && segments.Contains("node_modules"))
        {
            return "node_modules";
        }

        return null;
    }

    [GeneratedRegex(@"^[1-9][0-9]*$")]
    private static partial Regex NumericUserRegex();

    [GeneratedRegex(@"-----BEGIN [A-Z ]*PRIVATE KEY-----")]
    private static partial Regex PemBlockRegex();

    [GeneratedRegex(
        @"(password|passwd|pwd|secret|token|api[_-]?key|access[_-]?key|credential|private[_-]?key|connectionstring)[A-Za-z0-9_\-]*\s*[=:]\s*[^\s=:;]+|\bAKIA[0-9A-Z]{16}\b|\bgh[pousr]_[A-Za-z0-9]{20,}|://[^/\s:@]+:[^/\s@]+@",
        RegexOptions.IgnoreCase)]
    private static partial Regex CredentialRegex();
}
