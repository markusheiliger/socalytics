using Microsoft.Extensions.Hosting;

namespace SocAlytics.Analysts.Manager.Hosting;

public static class ManagerHost
{
    public static HostApplicationBuilder CreateBuilder(string[]? args = null)
    {
        var builder = Host.CreateApplicationBuilder(args ?? []);
        builder.Services.AddManagerCore(builder.Configuration);
        return builder;
    }
}
