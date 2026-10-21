using System.Diagnostics;
using System.Text;
using SocAlytics.Platform.Images.Tests.Support;

namespace SocAlytics.Platform.Compose.Tests.Support;

/// <summary>Runs <c>docker compose</c> against the committed stamp definition from the repository root.</summary>
public sealed class ComposeCli(string projectName, string envFile, string? overrideFile = null)
{
    public const string DefinitionPath = "src/platform/deploy/compose/compose.yaml";

    public string ProjectName { get; } = projectName;

    public string RepositoryRoot { get; } = ImageBuildFixture.LocateRepositoryRoot();

    /// <summary>Fails with a message naming the missing Docker or Compose support when <c>docker compose version</c> fails.</summary>
    public static async Task EnsureAvailableAsync()
    {
        var result = await RunProcessAsync(ImageBuildFixture.LocateRepositoryRoot(), ["compose", "version"]);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                "Docker with the Compose plugin ('docker compose') is required for the Compose stamp evidence; 'docker compose version' failed:\n" + result.Output);
        }
    }

    /// <summary>Runs <c>docker compose</c> with the definition, the optional override (repository-relative), the project, and the environment file.</summary>
    public Task<(int ExitCode, string Output)> RunAsync(params string[] arguments)
    {
        var all = new List<string> { "compose", "-f", DefinitionPath };
        if (overrideFile is not null)
        {
            all.Add("-f");
            all.Add(overrideFile);
        }

        all.AddRange(["-p", ProjectName, "--env-file", envFile]);
        all.AddRange(arguments);
        return RunProcessAsync(RepositoryRoot, all);
    }

    private static async Task<(int ExitCode, string Output)> RunProcessAsync(string workingDirectory, IReadOnlyList<string> arguments)
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
}
