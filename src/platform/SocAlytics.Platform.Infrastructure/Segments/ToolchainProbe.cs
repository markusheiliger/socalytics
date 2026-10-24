using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Options;
using SocAlytics.Platform.Application.Segments;
using SocAlytics.Platform.Domain.Segments;

namespace SocAlytics.Platform.Infrastructure.Segments;

internal sealed record ToolResult(int ExitCode, string StandardOutput, string StandardError);

internal static class ToolProcess
{
    public static async Task<ToolResult> RunAsync(string path, IEnumerable<string> arguments, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo(path)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        Process process;
        try
        {
            process = Process.Start(startInfo) ?? throw new SegmentExtractionException(SegmentFailureReasons.ToolchainUnavailable, false);
        }
        catch (Exception ex) when (ex is Win32Exception or FileNotFoundException or DirectoryNotFoundException)
        {
            throw new SegmentExtractionException(SegmentFailureReasons.ToolchainUnavailable, false, ex);
        }

        using (process)
        {
            process.StandardInput.Close();
            var output = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
            var error = process.StandardError.ReadToEndAsync(CancellationToken.None);
            try
            {
                await process.WaitForExitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                }

                throw;
            }

            return new ToolResult(process.ExitCode, await output, await error);
        }
    }
}

// Verifies once per start that both executables exist and match the expected version prefix.
internal sealed class ToolchainProbe(IOptions<SegmentToolchainOptions> options)
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private string? toolchain;

    public async Task<string> ProbeAsync(CancellationToken cancellationToken)
    {
        if (toolchain is not null)
        {
            return toolchain;
        }

        await gate.WaitAsync(cancellationToken);
        try
        {
            if (toolchain is not null)
            {
                return toolchain;
            }

            var settings = options.Value;
            var ffmpeg = await ReadVersionAsync("ffmpeg", settings.FfmpegPath, settings.ExpectedVersionPrefix, cancellationToken);
            var ffprobe = await ReadVersionAsync("ffprobe", settings.FfprobePath, settings.ExpectedVersionPrefix, cancellationToken);
            var description = new StringBuilder().Append("ffmpeg ").Append(ffmpeg).Append("; ffprobe ").Append(ffprobe).ToString();
            toolchain = description.Length <= 200 ? description : description[..200];
            return toolchain;
        }
        finally
        {
            gate.Release();
        }
    }

    private static async Task<string> ReadVersionAsync(string tool, string path, string expectedPrefix, CancellationToken cancellationToken)
    {
        var result = await ToolProcess.RunAsync(path, ["-version"], cancellationToken);
        var firstLine = result.StandardOutput.Split('\n', 2)[0].Trim();
        var marker = $"{tool} version ";
        if (result.ExitCode != 0 || !firstLine.StartsWith(marker, StringComparison.Ordinal))
        {
            throw new SegmentExtractionException(SegmentFailureReasons.ToolchainUnavailable, false);
        }

        var version = firstLine[marker.Length..].Split(' ', 2)[0];
        if (expectedPrefix.Length > 0 && !version.StartsWith(expectedPrefix, StringComparison.Ordinal))
        {
            throw new SegmentExtractionException(SegmentFailureReasons.ToolchainUnavailable, false);
        }

        return version;
    }
}
