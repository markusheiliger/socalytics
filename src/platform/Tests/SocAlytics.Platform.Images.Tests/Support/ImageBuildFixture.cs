using System.Diagnostics;
using System.Text;
using Xunit;

namespace SocAlytics.Platform.Images.Tests.Support;

[CollectionDefinition(Name)]
public sealed class ImageBuildCollection : ICollectionFixture<ImageBuildFixture>
{
    public const string Name = "OCI images";
}

/// <summary>Builds each requested image once per run with the documented <c>docker build</c> command and removes the images afterwards.</summary>
public sealed class ImageBuildFixture : IAsyncLifetime
{
    public const string TestRevision = "0123456789abcdef0123456789abcdef01234567";
    public const string TestVersion = "2026.1018.092503";

    private readonly Dictionary<string, Lazy<Task<string>>> _builds = [];
    private readonly List<string> _tags = [];
    private readonly object _gate = new();

    public string RepositoryRoot { get; private set; } = string.Empty;

    public ValueTask InitializeAsync()
    {
        RepositoryRoot = LocateRepositoryRoot();
        // Fails visibly when no container runtime or docker CLI is available; there is no fallback.
        var result = RunDockerAsync(RepositoryRoot, "version", "--format", "{{.Server.Version}}").GetAwaiter().GetResult();
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                "A container runtime and the docker CLI are required for the image evidence:\n" + result.Output);
        }

        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        string[] tags;
        lock (_gate)
        {
            tags = [.. _tags];
        }

        foreach (var tag in tags)
        {
            await RunDockerAsync(RepositoryRoot, "rmi", "--force", tag);
        }
    }

    /// <summary>Builds the image of <paramref name="dockerfile"/> (repository-relative) once and returns its unique tag.</summary>
    public Task<string> BuildAsync(string dockerfile)
    {
        Lazy<Task<string>> build;
        lock (_gate)
        {
            if (!_builds.TryGetValue(dockerfile, out build!))
            {
                build = new Lazy<Task<string>>(() => BuildCoreAsync(dockerfile));
                _builds[dockerfile] = build;
            }
        }

        return build.Value;
    }

    private async Task<string> BuildCoreAsync(string dockerfile)
    {
        var tag = $"socalytics-image-evidence/{Path.GetFileName(Path.GetDirectoryName(dockerfile))!.ToLowerInvariant()}:{Guid.NewGuid():N}";
        lock (_gate)
        {
            _tags.Add(tag);
        }

        var result = await RunDockerAsync(
            RepositoryRoot,
            "build",
            "-f", dockerfile,
            "--build-arg", $"SOCALYTICS_REVISION={TestRevision}",
            "--build-arg", $"SOCALYTICS_VERSION={TestVersion}",
            "-t", tag,
            ".");

        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException($"docker build of {dockerfile} failed with exit code {result.ExitCode}:\n{result.Output}");
        }

        return tag;
    }

    internal static async Task<(int ExitCode, string Output)> RunDockerAsync(string workingDirectory, params string[] arguments)
    {
        var info = new ProcessStartInfo("docker")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        info.Environment["DOCKER_BUILDKIT"] = "1";

        Process process;
        try
        {
            process = Process.Start(info) ?? throw new InvalidOperationException("The docker CLI could not be started.");
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return (127, "The docker CLI is not available: " + exception.Message);
        }

        using (process)
        {
            var output = new StringBuilder();
            var gate = new object();
            void Append(string? line)
            {
                if (line is not null)
                {
                    lock (gate)
                    {
                        output.AppendLine(line);
                    }
                }
            }

            process.OutputDataReceived += (_, e) => Append(e.Data);
            process.ErrorDataReceived += (_, e) => Append(e.Data);
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            await process.WaitForExitAsync();
            process.WaitForExit();
            return (process.ExitCode, output.ToString());
        }
    }

    private static string LocateRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "src", "platform", "SocAlytics.Platform.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("The repository root was not found above " + AppContext.BaseDirectory);
    }
}
