using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using SocAlytics.Platform.Application.Abstractions;
using SocAlytics.Platform.Integration.Tests.IdentityAccess.Support;
using SocAlytics.Platform.Integration.Tests.Infrastructure;
using Xunit;

namespace SocAlytics.Platform.Integration.Tests.IdentityAccess;

public sealed class HttpRequestContextTests(PostgresContainerFixture postgres)
{
    [Fact]
    public async Task ApiStartsAndContextWithoutHttpContextIsSystem()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await postgres.CreateDatabaseAsync(ct);

        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            foreach (var (key, value) in TestIdentityAccessSettings.Values)
            {
                builder.UseSetting(key, value);
            }

            builder.UseSetting("ConnectionStrings:socalytics", db.AppConnectionString);
        });

        using var scope = factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<IRequestContext>();

        context.ActorKind.ShouldBe(AuditActorKind.System);
        context.MemberAccountId.ShouldBeNull();
        context.SessionId.ShouldBeNull();
        context.CorrelationId.ShouldNotBeNullOrWhiteSpace();
    }
}
