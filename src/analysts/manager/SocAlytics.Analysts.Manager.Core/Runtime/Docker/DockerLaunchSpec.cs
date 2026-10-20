using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;

namespace SocAlytics.Analysts.Manager.Runtime.Docker;

/// <summary>Builds the <c>/containers/create</c> body of an Analyst container as contracts/analyst-container.md specifies.</summary>
public static class DockerLaunchSpec
{
    public const string FixedUser = "65532:65532";
    public const string JobMount = "/socalytics/job.json";
    public const string AttemptMount = "/socalytics/attempt.json";
    public const string InputsMount = "/socalytics/inputs";
    public const string ResultMount = "/socalytics/result";
    public const string ScratchMount = "/socalytics/scratch";
    public const int PidsLimit = 512;
    public const long TmpfsBytes = 64L * 1024 * 1024;
    public const string LogMaxSize = "10m";
    public const string ManagerLabel = "io.socalytics.manager";
    public const string LogicalJobLabel = "io.socalytics.logical-job";
    public const string AttemptLabel = "io.socalytics.attempt";

    /// <summary>The Manager's own non-root uid:gid on Unix, so bind-mounted directories stay writable; otherwise the fixed user.</summary>
    public static string DefaultUser()
    {
        if (OperatingSystem.IsWindows())
        {
            return FixedUser;
        }

        var uid = GetEffectiveUserId();
        var gid = GetEffectiveGroupId();
        return uid == 0 ? FixedUser : $"{uid.ToString(CultureInfo.InvariantCulture)}:{gid.ToString(CultureInfo.InvariantCulture)}";
    }

    public static JsonObject Build(AttemptLaunch launch, string imageReference, string managerId, string user)
    {
        var binds = new List<string>();
        if (launch.JobFile is { } job)
        {
            binds.Add($"{job}:{JobMount}:ro");
        }

        if (launch.AttemptFile is { } attemptFile)
        {
            binds.Add($"{attemptFile}:{AttemptMount}:ro");
        }

        if (launch.InputsDirectory is { } inputs)
        {
            binds.Add($"{inputs}:{InputsMount}:ro");
        }

        if (launch.ResultDirectory is { } result)
        {
            binds.Add($"{result}:{ResultMount}:rw");
        }

        if (launch.ScratchDirectory is { } scratch)
        {
            binds.Add($"{scratch}:{ScratchMount}:rw");
        }

        var memory = launch.Resources.MemoryBytes;
        return new JsonObject
        {
            ["Image"] = imageReference,
            ["User"] = user,
            ["Env"] = new JsonArray(),
            ["NetworkDisabled"] = true,
            ["AttachStdin"] = false,
            ["Tty"] = false,
            ["Labels"] = new JsonObject
            {
                [ManagerLabel] = managerId,
                [LogicalJobLabel] = launch.LogicalJobId,
                [AttemptLabel] = launch.AttemptId,
            },
            ["HostConfig"] = new JsonObject
            {
                ["Binds"] = new JsonArray(binds.Select(b => (JsonNode)JsonValue.Create(b)!).ToArray()),
                ["NetworkMode"] = "none",
                ["ReadonlyRootfs"] = true,
                ["Privileged"] = false,
                ["CapDrop"] = new JsonArray("ALL"),
                ["SecurityOpt"] = new JsonArray("no-new-privileges"),
                ["PidMode"] = string.Empty,
                ["IpcMode"] = "private",
                ["Devices"] = new JsonArray(),
                ["NanoCpus"] = (long)Math.Round(launch.Resources.Cpus * 1_000_000_000d),
                ["Memory"] = memory,
                ["MemorySwap"] = memory,
                ["PidsLimit"] = PidsLimit,
                ["Tmpfs"] = new JsonObject
                {
                    ["/tmp"] = $"rw,noexec,nosuid,size={TmpfsBytes.ToString(CultureInfo.InvariantCulture)}",
                },
                ["LogConfig"] = new JsonObject
                {
                    ["Type"] = "json-file",
                    ["Config"] = new JsonObject { ["max-size"] = LogMaxSize },
                },
            },
        };
    }

    [DllImport("libc", EntryPoint = "geteuid")]
    private static extern uint GetEffectiveUserId();

    [DllImport("libc", EntryPoint = "getegid")]
    private static extern uint GetEffectiveGroupId();
}
