using Shouldly;
using SocAlytics.Platform.Api.Registry;
using SocAlytics.Platform.Application.Registry;
using Xunit;

namespace SocAlytics.Platform.Integration.Tests.Registry.Unit;

public sealed class StampOptionsValidationTests
{
    [Theory]
    [InlineData("https://stamp.example", false, true)]
    [InlineData("http://localhost:5000", false, false)]
    [InlineData("http://localhost:5000", true, true)]
    [InlineData("/relative", true, false)]
    [InlineData("", true, false)]
    public void ValidatesPublicBaseUri(string uri, bool development, bool valid) =>
        new StampOptionsValidator(development)
            .Validate(null, new StampOptions { Id = "s", PublicBaseUri = uri }).Succeeded.ShouldBe(valid);

    [Fact]
    public void RequiresStampId() =>
        new StampOptionsValidator(true)
            .Validate(null, new StampOptions { Id = "", PublicBaseUri = "https://x" }).Succeeded.ShouldBeFalse();
}
