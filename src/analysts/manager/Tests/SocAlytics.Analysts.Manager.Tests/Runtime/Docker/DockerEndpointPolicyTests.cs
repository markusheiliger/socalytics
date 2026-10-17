using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Xunit;
using SocAlytics.Analysts.Manager.Runtime.Docker;

namespace SocAlytics.Analysts.Manager.Tests.Runtime.Docker;

public class DockerEndpointPolicyTests
{
    private sealed class Env(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "test";
        public string ContentRootPath { get; set; } = "/";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    [Theory]
    [InlineData("Development", true)]
    [InlineData("Test", true)]
    [InlineData("Production", true)]
    [InlineData("Development", false)]
    [InlineData("Production", false)]
    public void Named_pipe_is_allowed_on_Windows_only(string environment, bool windows)
    {
        var decision = DockerEndpointPolicy.Evaluate(DockerRuntimeOptions.DefaultEndpoint, new Env(environment), windows);

        decision.Allowed.ShouldBe(windows);
        if (windows)
        {
            decision.Kind.ShouldBe(DockerEndpointKind.NamedPipe);
            decision.Path.ShouldBe("docker_engine");
        }
        else
        {
            decision.Reason.ShouldNotBeNullOrEmpty();
            decision.Remediation.ShouldNotBeNullOrEmpty();
        }
    }

    [Theory]
    [InlineData("Development", true, true)]
    [InlineData("Test", true, true)]
    [InlineData("Development", false, true)]
    [InlineData("Production", true, false)]
    [InlineData("Production", false, false)]
    [InlineData("Staging", false, false)]
    public void Unix_socket_is_allowed_in_Development_and_Test_only(string environment, bool windows, bool allowed)
    {
        var decision = DockerEndpointPolicy.Evaluate("unix:///var/run/docker.sock", new Env(environment), windows);

        decision.Allowed.ShouldBe(allowed);
        if (allowed)
        {
            decision.Kind.ShouldBe(DockerEndpointKind.UnixSocket);
            decision.Path.ShouldBe("/var/run/docker.sock");
        }
        else
        {
            decision.Reason.ShouldNotBeNullOrEmpty();
            decision.Remediation.ShouldNotBeNullOrEmpty();
        }
    }

    [Fact]
    public void Unix_socket_without_environment_is_refused()
    {
        DockerEndpointPolicy.Evaluate("unix:///var/run/docker.sock", null, false).Allowed.ShouldBeFalse();
    }

    [Theory]
    [InlineData("tcp://127.0.0.1:2375")]
    [InlineData("http://localhost:2375")]
    [InlineData("https://docker.example.test")]
    [InlineData("ssh://user@host")]
    [InlineData("npipe://remote/pipe/docker_engine")]
    [InlineData("")]
    public void Every_other_endpoint_is_refused_with_remediation(string endpoint)
    {
        var decision = DockerEndpointPolicy.Evaluate(endpoint, new Env("Development"), true);

        decision.Allowed.ShouldBeFalse();
        decision.Reason.ShouldNotBeNullOrEmpty();
        decision.Remediation.ShouldNotBeNullOrEmpty();
    }

    [Theory]
    [InlineData("tcp://127.0.0.1:2375")]
    [InlineData("https://docker.example.test")]
    public void A_refused_endpoint_never_creates_a_connection(string endpoint)
    {
        var decision = DockerEndpointPolicy.Evaluate(endpoint, new Env("Test"), true);

        var ex = Should.Throw<DockerEngineException>(() => DockerEngineClient.Create(decision));
        ex.Kind.ShouldBe(DockerErrorKind.EndpointRefused);
    }

    [Fact]
    public void Options_default_to_the_documented_values()
    {
        var options = new DockerRuntimeOptions();

        options.Endpoint.ShouldBe("npipe://./pipe/docker_engine");
        options.EffectiveCpus.ShouldBe(Environment.ProcessorCount);
        options.EffectiveMemoryBytes(8_000_000_000).ShouldBe(6_000_000_000);
        options.ImageCacheBudgetBytes.ShouldBe(20L * 1024 * 1024 * 1024);
        options.ImageCacheMaxIdle.ShouldBe(TimeSpan.FromDays(14));
        Should.NotThrow(options.Validate);
    }

    [Fact]
    public void Options_reject_invalid_values()
    {
        Should.Throw<InvalidOperationException>(new DockerRuntimeOptions { Cpus = 0 }.Validate);
        Should.Throw<InvalidOperationException>(new DockerRuntimeOptions { MemoryBytes = 0 }.Validate);
        Should.Throw<InvalidOperationException>(new DockerRuntimeOptions { ImageCacheBudgetBytes = 0 }.Validate);
        Should.Throw<InvalidOperationException>(new DockerRuntimeOptions { ImageCacheMaxIdle = TimeSpan.Zero }.Validate);
        Should.Throw<InvalidOperationException>(new DockerRuntimeOptions { CapabilityAllowlist = ["no-version"] }.Validate);
    }
}
