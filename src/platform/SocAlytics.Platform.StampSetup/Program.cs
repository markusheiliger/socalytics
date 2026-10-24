namespace SocAlytics.Platform.StampSetup;

public static class Program
{
    public static int Main(string[] args) => Run(args, Console.Out, Console.Error);

    public static int Run(string[] args, TextWriter output, TextWriter error)
    {
        if (args.Length != 2 || args[0] != "--output" || string.IsNullOrWhiteSpace(args[1]))
        {
            error.WriteLine("Usage: StampSetup --output <stamp dir>");
            return 2;
        }

        var directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(args[1]));
        if (Directory.Exists(directory) || File.Exists(directory))
        {
            error.WriteLine($"The stamp directory '{directory}' already exists; nothing was written. Choose a new directory.");
            return 1;
        }

        if (FindGitWorkingTree(directory) is { } root)
        {
            error.WriteLine($"The stamp directory '{directory}' lies inside the Git working tree '{root}'; nothing was written. Choose a directory outside any working tree so secrets stay out of the repository.");
            return 1;
        }

        var trust = new StampNatsTrust();
        var environment = StampEnvironmentFile.Render(directory, trust);
        var natsConfig = trust.BuildServerConfig();

        Directory.CreateDirectory(directory);
        StampEnvironmentFile.Write(Path.Combine(directory, StampEnvironmentFile.FileName), environment);
        File.WriteAllText(Path.Combine(directory, "nats.conf"), natsConfig);

        output.WriteLine($"Stamp directory written: {directory}");
        output.WriteLine("  stamp.env  secrets and setup values filled; operator values are still empty");
        output.WriteLine("  nats.conf  NATS server configuration trusting this stamp's operator and accounts");
        output.WriteLine();
        output.WriteLine("Next steps:");
        output.WriteLine($"  1. Fill every empty operator value in {Path.Combine(directory, StampEnvironmentFile.FileName)} (each has a comment).");
        output.WriteLine("  2. Start the stamp: docker compose -f src/platform/deploy/compose/compose.yaml -p <stamp> --env-file <stamp dir>/stamp.env up -d");
        output.WriteLine("  3. Keep the directory private: stamp.env holds secrets. This is a self-hosted evaluation stamp, not a production profile.");
        return 0;
    }

    private static string? FindGitWorkingTree(string path)
    {
        for (var current = Path.GetDirectoryName(path); current is not null; current = Path.GetDirectoryName(current))
        {
            var git = Path.Combine(current, ".git");
            if (Directory.Exists(git) || File.Exists(git))
            {
                return current;
            }
        }

        return null;
    }
}
