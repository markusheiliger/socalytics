using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Shouldly;
using SocAlytics.Platform.Integration.Tests.IdentityAccess.Support;
using Xunit;

namespace SocAlytics.Platform.Integration.Tests.IdentityAccess;

public sealed class IdentityAccessOptionsTests
{
	private static WebApplicationFactory<Program> CreateFactory(IEnumerable<KeyValuePair<string, string?>> settings) =>
		new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
		{
			builder.UseEnvironment("Production");
			builder.ConfigureAppConfiguration((_, configuration) =>
			{
				configuration.Sources.Clear();
				configuration.AddInMemoryCollection(settings);
			});
		});

	[Fact]
	public void MissingIdleTimeoutFailsStartupNamingTheKey()
	{
		var settings = TestIdentityAccessSettings.Values
			.Where(pair => pair.Key != "IdentityAccess:Session:IdleTimeout");
		using var factory = CreateFactory(settings);

		var exception = Should.Throw<OptionsValidationException>(() => factory.CreateClient());
		exception.Message.ShouldContain("IdentityAccess:Session:IdleTimeout");
	}

	[Fact]
	public void CompleteSettingsStartSuccessfully()
	{
		using var factory = CreateFactory(TestIdentityAccessSettings.Values);

		Should.NotThrow(() => factory.CreateClient().Dispose());
	}
}
