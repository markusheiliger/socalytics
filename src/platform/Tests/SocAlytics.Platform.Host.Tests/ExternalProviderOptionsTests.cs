using Shouldly;
using SocAlytics.Platform.Application.IdentityAccess;
using SocAlytics.Platform.Application.IdentityAccess.ExternalSignIn;
using Xunit;

namespace SocAlytics.Platform.Host.Tests;

public sealed class ExternalProviderOptionsTests
{
    private static IdentityAccessOptions Valid(Action<IdentityAccessOptions>? change = null)
    {
        var options = new IdentityAccessOptions();
        options.ExternalSignIn.WebUiBaseUrl = "https://club.example";
        options.ExternalProviders["corp"] = new IdentityAccessOptions.ExternalProviderOptions
        {
            DisplayName = "Corporate",
            Authority = "https://idp.example/tenant",
            ClientId = "client",
            ClientSecret = "secret",
        };
        change?.Invoke(options);
        return options;
    }

    private static string[] Failures(IdentityAccessOptions options, bool insecureAllowed = false)
    {
        var result = new ExternalProviderOptionsValidator(insecureAllowed).Validate(null, options);
        return result.Failed ? [.. result.Failures] : [];
    }

    [Fact]
    public void NoProvidersAndDefaultsAreValid()
    {
        Failures(new IdentityAccessOptions()).ShouldBeEmpty();
        var options = Valid();
        Failures(options).ShouldBeEmpty();
        options.ExternalProviders["corp"].Scopes.ShouldBe("openid");
        options.ExternalProviders["corp"].TokenEndpointAuthMethod.ShouldBe("client_secret_basic");
        options.ExternalSignIn.PendingLifetime.ShouldBe(TimeSpan.FromMinutes(5));
    }

    [Theory]
    [InlineData("Corp")]
    [InlineData("has space")]
    [InlineData("a/b")]
    [InlineData("")]
    [InlineData("this-identifier-is-way-longer-than-32")]
    public void NonUrlSafeIdentifiersAreRejected(string id)
    {
        var options = Valid(o => o.ExternalProviders[id] = o.ExternalProviders["corp"]);
        Failures(options).ShouldContain(f => f.Contains("provider identifier"));
    }

    [Fact]
    public void HttpAuthorityIsRejectedUnlessDevelopmentOrTest()
    {
        var options = Valid(o => o.ExternalProviders["corp"].Authority = "http://idp.example");
        Failures(options).ShouldContain(f => f.Contains(":Authority"));
        Failures(options, insecureAllowed: true).ShouldBeEmpty();
    }

    [Theory]
    [InlineData("DisplayName")]
    [InlineData("Authority")]
    [InlineData("ClientId")]
    [InlineData("ClientSecret")]
    public void MissingRequiredValuesAreRejectedNamingTheKey(string key)
    {
        var options = Valid(o =>
        {
            var p = o.ExternalProviders["corp"];
            switch (key)
            {
                case "DisplayName": p.DisplayName = ""; break;
                case "Authority": p.Authority = ""; break;
                case "ClientId": p.ClientId = " "; break;
                default: p.ClientSecret = ""; break;
            }
        });
        Failures(options).ShouldContain(f => f.Contains("ExternalProviders:corp:" + key));
    }

    [Fact]
    public void UnknownAuthMethodAndMissingWebUiBaseUrlAreRejected()
    {
        Failures(Valid(o => o.ExternalProviders["corp"].TokenEndpointAuthMethod = "private_key_jwt"))
            .ShouldContain(f => f.Contains("TokenEndpointAuthMethod"));
        Failures(Valid(o => o.ExternalSignIn.WebUiBaseUrl = ""))
            .ShouldContain(f => f.Contains("WebUiBaseUrl"));
    }

    [Fact]
    public void FailuresDoNotEchoTheSecret()
    {
        var options = Valid(o => o.ExternalProviders["corp"].Authority = "");
        string.Join('\n', Failures(options)).ShouldNotContain("secret");
    }
}
