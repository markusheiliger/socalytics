using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace SocAlytics.Platform.Integration.Tests;

/// <summary>Supplies the <see cref="IHostEnvironment"/> that hosts built from a bare <see cref="ServiceCollection"/> lack.</summary>
internal static class TestHostEnvironment
{
    public static IServiceCollection AddTestHostEnvironment(this IServiceCollection services, string environmentName = "Development")
    {
        services.AddSingleton<IHostEnvironment>(new Environment(environmentName));
        return services;
    }

    private sealed class Environment(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;

        public string ApplicationName { get; set; } = "SocAlytics.Platform.Integration.Tests";

        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
