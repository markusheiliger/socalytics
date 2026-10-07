using System.Text.RegularExpressions;
using Shouldly;
using SocAlytics.Platform.Infrastructure.Persistence.Migrations;
using Xunit;

namespace SocAlytics.Platform.Integration.Tests.Migrations;

public sealed class MigrationChecksumTests
{
    [Fact]
    public void LineEndingAndBomVariantsHashEqually()
    {
        var expected = MigrationChecksum.Compute("SELECT 1;\nSELECT 2;\n");

        MigrationChecksum.Compute("SELECT 1;\r\nSELECT 2;\r\n").ShouldBe(expected);
        MigrationChecksum.Compute("SELECT 1;\rSELECT 2;\r").ShouldBe(expected);
        MigrationChecksum.Compute("\uFEFFSELECT 1;\nSELECT 2;\n").ShouldBe(expected);
    }

    [Fact]
    public void ContentEditChangesHash()
    {
        MigrationChecksum.Compute("SELECT 1;").ShouldNotBe(MigrationChecksum.Compute("SELECT 2;"));
    }

    [Fact]
    public void FormatMatchesContract()
    {
        Regex.IsMatch(MigrationChecksum.Compute("SELECT 1;"), "^sha-256:[0-9a-f]{64}$").ShouldBeTrue();
    }
}
