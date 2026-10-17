namespace SocAlytics.Analysts.Manager.Server;

internal static class Program
{
    public static Task<int> Main(string[] args) =>
        ServerProgram.RunAsync(args, new ServerProgramEnvironment());
}
