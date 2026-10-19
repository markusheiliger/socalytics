using System.Text.Json;
using System.Text.RegularExpressions;
using Shouldly;
using SocAlytics.Platform.Compose.Tests.Support;
using Xunit;

namespace SocAlytics.Platform.Compose.Tests;

/// <summary>Checks the committed stamp definition through <c>docker compose config</c> alone; no container is started.</summary>
public sealed partial class ComposeDefinitionTests(ComposeDefinitionTests.ConfigFixture fixture) : IClassFixture<ComposeDefinitionTests.ConfigFixture>
{
    private static readonly string[] DevelopmentOnlyOptions =
    [
        "Registry__SoftwareKeys",
        "Registry__Images__UseHttp",
    ];

    private JsonElement Services => fixture.Config.GetProperty("services");

    [Fact]
    public void Only_the_web_service_publishes_a_port_and_it_is_bound_to_loopback()
    {
        var published = Services.EnumerateObject()
            .SelectMany(service => service.Value.TryGetProperty("ports", out var ports)
                ? ports.EnumerateArray().Select(port => (Service: service.Name, Port: port))
                : [])
            .ToList();

        published.Count.ShouldBe(1);
        published[0].Service.ShouldBe("web");
        published[0].Port.GetProperty("host_ip").GetString().ShouldBe("127.0.0.1");
        published[0].Port.GetProperty("published").GetString().ShouldBe(fixture.WebPort);
    }

    [Theory]
    [InlineData("postgres")]
    [InlineData("nats")]
    [InlineData("storage")]
    public void Third_party_images_are_pinned_by_digest(string service) =>
        Services.GetProperty(service).GetProperty("image").GetString()!.ShouldContain("@sha256:");

    [Fact]
    public void No_service_has_container_name_build_or_deploy()
    {
        foreach (var service in Services.EnumerateObject())
        {
            service.Value.TryGetProperty("container_name", out _).ShouldBeFalse(service.Name + " container_name");
            service.Value.TryGetProperty("build", out _).ShouldBeFalse(service.Name + " build");
            service.Value.TryGetProperty("deploy", out _).ShouldBeFalse(service.Name + " deploy");
        }
    }

    [Fact]
    public void Networks_and_volumes_have_no_fixed_name_and_data_and_app_are_internal()
    {
        // Compose reports the generated name <project>_<key> when the definition sets no name.
        foreach (var section in new[] { "networks", "volumes" })
        {
            foreach (var entry in fixture.Config.GetProperty(section).EnumerateObject())
            {
                entry.Value.GetProperty("name").GetString().ShouldBe($"{fixture.ProjectName}_{entry.Name}", $"{section}.{entry.Name}");
            }
        }

        var networks = fixture.Config.GetProperty("networks");
        IsInternal(networks.GetProperty("data")).ShouldBeTrue();
        IsInternal(networks.GetProperty("app")).ShouldBeTrue();
        IsInternal(networks.GetProperty("edge")).ShouldBeFalse();
        IsInternal(networks.GetProperty("egress")).ShouldBeFalse();
    }

    [Theory]
    [InlineData("migrator")]
    [InlineData("api")]
    [InlineData("web")]
    public void Application_services_are_read_only_with_a_tmp_tmpfs(string service)
    {
        var definition = Services.GetProperty(service);
        definition.GetProperty("read_only").GetBoolean().ShouldBeTrue();
        definition.GetProperty("tmpfs").EnumerateArray().Select(item => item.GetString()).ShouldContain("/tmp");
    }

    [Fact]
    public void The_migrator_does_not_restart_and_the_api_waits_for_its_completion()
    {
        Services.GetProperty("migrator").GetProperty("restart").GetString().ShouldBe("no");
        Services.GetProperty("api").GetProperty("depends_on").GetProperty("migrator").GetProperty("condition").GetString()
            .ShouldBe("service_completed_successfully");
    }

    [Fact]
    public void The_api_uses_production_authentication_and_no_development_only_option()
    {
        var environment = Services.GetProperty("api").GetProperty("environment");
        environment.TryGetProperty("ASPNETCORE_ENVIRONMENT", out _).ShouldBeFalse();
        environment.GetProperty("Nats__Authentication__Mode").GetString().ShouldBe("Jwt");

        foreach (var name in DevelopmentOnlyOptions)
        {
            environment.EnumerateObject().ShouldNotContain(property => property.Name.StartsWith(name, StringComparison.Ordinal), name);
        }
    }

    [Fact]
    public void The_web_service_has_no_object_storage_origin()
    {
        var environment = Services.GetProperty("web").GetProperty("environment");
        environment.TryGetProperty("SOCALYTICS_OBJECT_STORAGE_ORIGIN", out _).ShouldBeFalse();
    }

    [Fact]
    public void The_stamp_template_lists_exactly_the_interpolated_variables_and_assigns_no_value()
    {
        var composeVariables = VariablePattern().Matches(File.ReadAllText(Path.Combine(fixture.RepositoryRoot, ComposeCli.DefinitionPath)))
            .Select(match => match.Groups[1].Value)
            .ToHashSet();

        var assignments = File.ReadAllLines(Path.Combine(fixture.RepositoryRoot, "src", "platform", "deploy", "compose", "stamp.env.template"))
            .Where(line => !line.TrimStart().StartsWith('#') && line.Contains('='))
            .Select(line => line.Split('=', 2))
            .ToList();

        assignments.ShouldAllBe(parts => parts[1].Length == 0, "the template assigns no value");
        assignments.Select(parts => parts[0]).ToHashSet().ShouldBe(composeVariables, ignoreOrder: true);
    }

    [Fact]
    public async Task A_missing_required_value_makes_config_fail_naming_it()
    {
        var directory = fixture.NewStampDirectory();
        var envFile = Path.Combine(directory, "stamp.env");
        var lines = File.ReadAllLines(envFile).Where(line => !line.StartsWith("SOCALYTICS_APP_DB_PASSWORD=", StringComparison.Ordinal)).ToArray();
        File.WriteAllLines(envFile, lines);

        var (exitCode, output) = await new ComposeCli(fixture.ProjectName, envFile).RunAsync("config", "--format", "json");

        exitCode.ShouldNotBe(0);
        output.ShouldContain("SOCALYTICS_APP_DB_PASSWORD");
    }

    private static bool IsInternal(JsonElement network) =>
        network.TryGetProperty("internal", out var value) && value.GetBoolean();

    [GeneratedRegex(@"\$\{([A-Z][A-Z0-9_]*)[:}\-?]")]
    private static partial Regex VariablePattern();

    public sealed class ConfigFixture : IAsyncLifetime
    {
        private readonly List<string> _directories = [];

        public string ProjectName { get; } = "socalytics-definition-" + Guid.NewGuid().ToString("N")[..8];

        public string RepositoryRoot { get; } = SocAlytics.Platform.Images.Tests.Support.ImageBuildFixture.LocateRepositoryRoot();

        public string WebPort { get; } = "18080";

        public JsonElement Config { get; private set; }

        public async ValueTask InitializeAsync()
        {
            await ComposeCli.EnsureAvailableAsync();

            var directory = NewStampDirectory();
            var (exitCode, output) = await new ComposeCli(ProjectName, Path.Combine(directory, "stamp.env")).RunAsync("config", "--format", "json");
            if (exitCode != 0)
            {
                throw new InvalidOperationException($"docker compose config failed with exit code {exitCode}:\n{output}");
            }

            var json = output[output.IndexOf('{')..];
            Config = JsonDocument.Parse(json).RootElement.Clone();
        }

        /// <summary>Creates a complete disposable stamp directory with the setup console and disposable operator values.</summary>
        public string NewStampDirectory()
        {
            var directory = Path.Combine(Path.GetTempPath(), "socalytics-definition-" + Guid.NewGuid().ToString("N")[..12]);
            _directories.Add(directory);

            var exitCode = StampSetup.Program.Run(["--output", directory], TextWriter.Null, TextWriter.Null);
            if (exitCode != 0)
            {
                throw new InvalidOperationException($"The setup console failed with exit code {exitCode} for '{directory}'.");
            }

            var envFile = Path.Combine(directory, "stamp.env");
            var values = new Dictionary<string, string>
            {
                ["SOCALYTICS_API_IMAGE"] = "stamp.invalid/api:1",
                ["SOCALYTICS_MIGRATOR_IMAGE"] = "stamp.invalid/migrator:1",
                ["SOCALYTICS_WEB_IMAGE"] = "stamp.invalid/web:1",
                ["SOCALYTICS_RELEASE_VERSION"] = "2026.1019.000000",
                ["SOCALYTICS_WEB_PORT"] = WebPort,
                ["SOCALYTICS_STAMP_ID"] = "stamp-definition",
                ["SOCALYTICS_PUBLIC_BASE_URI"] = "https://stamp.invalid",
                ["SOCALYTICS_CLUB_DISPLAY_NAME"] = "Definition Club",
                ["SOCALYTICS_FIRST_CLUB_ADMIN_ACCOUNT_NAME"] = "club.admin",
                ["SOCALYTICS_SMTP_HOST"] = "smtp.invalid",
                ["SOCALYTICS_SMTP_PORT"] = "587",
                ["SOCALYTICS_SMTP_SECURITY"] = "StartTls",
                ["SOCALYTICS_SENDER_ADDRESS"] = "noreply@stamp.invalid",
                ["SOCALYTICS_SENDER_NAME"] = "SocAlytics Stamp",
            };

            var lines = File.ReadAllLines(envFile).ToList();
            foreach (var (name, value) in values)
            {
                var index = lines.FindIndex(line => line.StartsWith(name + "=", StringComparison.Ordinal));
                if (index >= 0)
                {
                    lines[index] = $"{name}={value}";
                }
                else
                {
                    lines.Add($"{name}={value}");
                }
            }

            File.WriteAllLines(envFile, lines);
            return directory;
        }

        public ValueTask DisposeAsync()
        {
            foreach (var directory in _directories.Where(Directory.Exists))
            {
                Directory.Delete(directory, recursive: true);
            }

            return ValueTask.CompletedTask;
        }
    }
}
