using System.Text.Json.Nodes;
using Shouldly;
using Xunit;
using SocAlytics.Analysts.Manager.Runtime;
using SocAlytics.Analysts.Manager.Runtime.Docker;

namespace SocAlytics.Analysts.Manager.Tests.Runtime.Docker;

public class DockerLaunchSpecTests
{
    private const string Image = "registry.test/socalytics/test-analyst@sha256:" + "a1b2c3d4e5f60718293a4b5c6d7e8f90a1b2c3d4e5f60718293a4b5c6d7e8f90";

    private static AttemptLaunch Launch(string? inputs = "/attempts/a/inputs") => new(
        "attempt-1", "job-1", "cap", "cap:1", "scope", "sha-256:p", "sha-256:i", string.Empty,
        new AttemptResources(1.5, 512L * 1024 * 1024, []),
        ResultDirectory: "/attempts/a/result", InputsDirectory: inputs, JobFile: "/attempts/a/job.json", ScratchDirectory: "/attempts/a/scratch", AttemptFile: "/attempts/a/attempt.json");

    private static JsonObject Build(AttemptLaunch? launch = null, string user = DockerLaunchSpec.FixedUser) =>
        DockerLaunchSpec.Build(launch ?? Launch(), Image, "manager-1", user);

    [Fact]
    public void Applies_every_restriction()
    {
        var body = Build();
        var host = body["HostConfig"]!;

        body["Image"]!.GetValue<string>().ShouldBe(Image);
        body["User"]!.GetValue<string>().ShouldBe("65532:65532");
        body["Env"]!.AsArray().Count.ShouldBe(0);
        body["NetworkDisabled"]!.GetValue<bool>().ShouldBeTrue();
        host["NetworkMode"]!.GetValue<string>().ShouldBe("none");
        host["ReadonlyRootfs"]!.GetValue<bool>().ShouldBeTrue();
        host["Privileged"]!.GetValue<bool>().ShouldBeFalse();
        host["CapDrop"]!.AsArray().Select(n => n!.GetValue<string>()).ShouldBe(["ALL"]);
        host["SecurityOpt"]!.AsArray().Select(n => n!.GetValue<string>()).ShouldBe(["no-new-privileges"]);
        host["Devices"]!.AsArray().Count.ShouldBe(0);
        host["PidMode"]!.GetValue<string>().ShouldBe(string.Empty);
        host["IpcMode"]!.GetValue<string>().ShouldBe("private");
        host["NanoCpus"]!.GetValue<long>().ShouldBe(1_500_000_000L);
        host["Memory"]!.GetValue<long>().ShouldBe(512L * 1024 * 1024);
        host["MemorySwap"]!.GetValue<long>().ShouldBe(512L * 1024 * 1024);
        host["PidsLimit"]!.GetValue<int>().ShouldBe(512);
        host["Tmpfs"]!["/tmp"]!.GetValue<string>().ShouldContain($"size={64 * 1024 * 1024}");
        host["LogConfig"]!["Type"]!.GetValue<string>().ShouldBe("json-file");
        host["LogConfig"]!["Config"]!["max-size"]!.GetValue<string>().ShouldBe("10m");
    }

    [Fact]
    public void Sets_the_labels()
    {
        var labels = Build()["Labels"]!.AsObject();

        labels["io.socalytics.manager"]!.GetValue<string>().ShouldBe("manager-1");
        labels["io.socalytics.logical-job"]!.GetValue<string>().ShouldBe("job-1");
        labels["io.socalytics.attempt"]!.GetValue<string>().ShouldBe("attempt-1");
    }

    [Fact]
    public void Binds_inputs_and_job_read_only_and_result_and_scratch_read_write()
    {
        var binds = Build()["HostConfig"]!["Binds"]!.AsArray().Select(n => n!.GetValue<string>()).ToList();

        binds.ShouldBe(
        [
            "/attempts/a/job.json:/socalytics/job.json:ro",
            "/attempts/a/attempt.json:/socalytics/attempt.json:ro",
            "/attempts/a/inputs:/socalytics/inputs:ro",
            "/attempts/a/result:/socalytics/result:rw",
            "/attempts/a/scratch:/socalytics/scratch:rw",
        ]);
    }

    [Fact]
    public void Omits_the_inputs_bind_for_jobs_without_upstream()
    {
        var binds = Build(Launch(null))["HostConfig"]!["Binds"]!.AsArray().Select(n => n!.GetValue<string>()).ToList();

        binds.Count.ShouldBe(4);
        binds.ShouldNotContain(b => b.Contains("/socalytics/inputs", StringComparison.Ordinal));
    }

    [Fact]
    public void Binds_the_media_directory_read_only_only_when_the_job_has_media()
    {
        var withMedia = Build(Launch() with { MediaDirectory = "/attempts/a/media" });
        var binds = withMedia["HostConfig"]!["Binds"]!.AsArray().Select(n => n!.GetValue<string>()).ToList();

        binds.ShouldBe(
        [
            "/attempts/a/job.json:/socalytics/job.json:ro",
            "/attempts/a/attempt.json:/socalytics/attempt.json:ro",
            "/attempts/a/inputs:/socalytics/inputs:ro",
            "/attempts/a/media:/socalytics/media:ro",
            "/attempts/a/result:/socalytics/result:rw",
            "/attempts/a/scratch:/socalytics/scratch:rw",
        ]);
        withMedia["NetworkDisabled"]!.GetValue<bool>().ShouldBeTrue();
        withMedia["HostConfig"]!["NetworkMode"]!.GetValue<string>().ShouldBe("none");

        Build()["HostConfig"]!["Binds"]!.AsArray()
            .Select(n => n!.GetValue<string>()).ShouldNotContain(b => b.Contains("/socalytics/media", StringComparison.Ordinal));
    }

    [Fact]
    public void Binds_the_attempt_file_read_only_and_adds_no_environment_variable()
    {
        var body = Build();
        var binds = body["HostConfig"]!["Binds"]!.AsArray().Select(n => n!.GetValue<string>()).ToList();

        binds.Where(b => b.Contains("attempt.json", StringComparison.Ordinal)).ShouldBe(["/attempts/a/attempt.json:/socalytics/attempt.json:ro"]);
        body["Env"]!.AsArray().Count.ShouldBe(0);
    }

    [Fact]
    public void Omits_the_attempt_bind_when_there_is_no_attempt_file()
    {
        var binds = Build(Launch() with { AttemptFile = null })["HostConfig"]!["Binds"]!.AsArray().Select(n => n!.GetValue<string>()).ToList();

        binds.ShouldNotContain(b => b.Contains("attempt.json", StringComparison.Ordinal));
    }

    [Fact]
    public void Uses_the_given_user()
    {
        Build(user: "1000:1000")["User"]!.GetValue<string>().ShouldBe("1000:1000");
    }

    [Fact]
    public void Default_user_is_never_root()
    {
        DockerLaunchSpec.DefaultUser().ShouldNotStartWith("0:");
    }
}
