namespace SocAlytics.Platform.Migrator;

internal static class Program
{
    private static async Task<int> Main(string[] args) =>
        (int)await MigratorEntryPoint.RunAsync(args, CancellationToken.None);
}
