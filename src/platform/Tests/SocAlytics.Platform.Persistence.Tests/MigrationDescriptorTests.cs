using System.Security.Cryptography;
using System.Text;
using Shouldly;
using SocAlytics.Platform.Persistence;
using Xunit;

namespace SocAlytics.Platform.Persistence.Tests;

public sealed class MigrationDescriptorTests
{
    [Fact]
    public void ChecksumIsDeterministicForIdenticalContent()
    {
        var content = Encoding.UTF8.GetBytes("CREATE SCHEMA club;");

        var first = new MigrationDescriptor(ModuleKey.Club, 1, "0001_initial", content);
        var second = new MigrationDescriptor(ModuleKey.Club, 1, "0001_initial", content);

        first.Checksum.ShouldBe(second.Checksum);
    }

    [Fact]
    public void ChecksumChangesWhenContentChanges()
    {
        var first = new MigrationDescriptor(ModuleKey.Club, 1, "0001_initial", Encoding.UTF8.GetBytes("CREATE SCHEMA club;"));
        var second = new MigrationDescriptor(ModuleKey.Club, 1, "0001_initial", Encoding.UTF8.GetBytes("CREATE SCHEMA club_v2;"));

        first.Checksum.ShouldNotBe(second.Checksum);
    }

    [Fact]
    public void ChecksumMatchesTheContentsSha256Hash()
    {
        var content = Encoding.UTF8.GetBytes("socalytics");

        var descriptor = new MigrationDescriptor(ModuleKey.Club, 1, "0001_initial", content);

        descriptor.Checksum.ShouldBe(Convert.ToHexStringLower(SHA256.HashData(content)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void SequenceMustBeAPositiveOneBasedOrdinal(int sequence)
    {
        Should.Throw<ArgumentOutOfRangeException>(() => new MigrationDescriptor(ModuleKey.Club, sequence, "0001_initial", [1]));
    }

    [Fact]
    public void ScriptIdentityMustNotBeEmpty()
    {
        Should.Throw<ArgumentException>(() => new MigrationDescriptor(ModuleKey.Club, 1, string.Empty, [1]));
    }

    [Fact]
    public void ContentMustNotBeEmpty()
    {
        Should.Throw<ArgumentException>(() => new MigrationDescriptor(ModuleKey.Club, 1, "0001_initial", []));
    }

    [Fact]
    public void FromEmbeddedResourceReadsContentAndComputesItsChecksum()
    {
        var descriptor = MigrationDescriptor.FromEmbeddedResource(
            typeof(MigrationDescriptorTests).Assembly,
            "SocAlytics.Platform.Persistence.Tests.Fixtures.0001_initial.sql",
            ModuleKey.Club,
            1,
            "0001_initial");

        descriptor.Content.ShouldNotBeEmpty();
        descriptor.Checksum.ShouldBe(MigrationChecksum.Compute(descriptor.Content));
    }

    [Fact]
    public void FromEmbeddedResourceFailsForAMissingResource()
    {
        Should.Throw<InvalidOperationException>(() => MigrationDescriptor.FromEmbeddedResource(
            typeof(MigrationDescriptorTests).Assembly,
            "SocAlytics.Platform.Persistence.Tests.Fixtures.does_not_exist.sql",
            ModuleKey.Club,
            1,
            "0001_initial"));
    }
}
