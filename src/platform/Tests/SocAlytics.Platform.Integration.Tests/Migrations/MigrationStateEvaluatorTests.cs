using Shouldly;
using SocAlytics.Platform.Infrastructure.Persistence.MigrationHistory;
using SocAlytics.Platform.Infrastructure.Persistence.Migrations;
using Xunit;

namespace SocAlytics.Platform.Integration.Tests.Migrations;

public sealed class MigrationStateEvaluatorTests
{
    private static readonly MigrationScript S1 = new("0001_foundation_one", "SELECT 1;");
    private static readonly MigrationScript S2 = new("0002_foundation_two", "SELECT 2;");
    private static readonly MigrationScript S3 = new("0003_foundation_three", "SELECT 3;");

    private static MigrationCatalog Catalog(params MigrationScript[] scripts) => MigrationCatalog.Create(scripts);

    private static MigrationHistoryEntry Applied(MigrationScript s) => new(s.Sequence, s.Identity, s.Checksum);

    [Fact]
    public void AllAppliedIsCurrent()
    {
        var state = MigrationStateEvaluator.Evaluate(Catalog(S1, S2), [Applied(S1), Applied(S2)]);

        state.Kind.ShouldBe(MigrationStateKind.Current);
        state.UnknownAppliedIdentities.ShouldBeEmpty();
    }

    [Fact]
    public void UnappliedTailIsPendingInOrder()
    {
        var state = MigrationStateEvaluator.Evaluate(Catalog(S3, S1, S2), [Applied(S1)]);

        state.Kind.ShouldBe(MigrationStateKind.Pending);
        state.PendingScripts.Select(s => s.Identity).ShouldBe([S2.Identity, S3.Identity]);
    }

    [Fact]
    public void MissingHistoryMeansAllPending()
    {
        var state = MigrationStateEvaluator.Evaluate(Catalog(S1, S2), []);

        state.Kind.ShouldBe(MigrationStateKind.Pending);
        state.PendingScripts.Count.ShouldBe(2);
    }

    [Fact]
    public void ChecksumMismatchNamesIdentity()
    {
        var state = MigrationStateEvaluator.Evaluate(
            Catalog(S1), [new MigrationHistoryEntry(1, S1.Identity, "sha-256:" + new string('0', 64))]);

        state.Kind.ShouldBe(MigrationStateKind.ChecksumMismatch);
        state.Identity.ShouldBe(S1.Identity);
    }

    [Fact]
    public void PendingBelowHighestAppliedIsSequenceConflict()
    {
        var state = MigrationStateEvaluator.Evaluate(Catalog(S1, S2), [Applied(S2)]);

        state.Kind.ShouldBe(MigrationStateKind.SequenceConflict);
        state.Identity.ShouldBe(S1.Identity);
    }

    [Fact]
    public void UnknownRowCountsForSequenceConflict()
    {
        var unknown = new MigrationHistoryEntry(5, "0005_other_thing", "sha-256:" + new string('a', 64));

        var state = MigrationStateEvaluator.Evaluate(Catalog(S1, S2), [Applied(S1), unknown]);

        state.Kind.ShouldBe(MigrationStateKind.SequenceConflict);
        state.Identity.ShouldBe(S2.Identity);
        state.UnknownAppliedIdentities.ShouldBe([unknown.Identity]);
    }

    [Fact]
    public void UnknownAppliedIsReportedWithoutBlocking()
    {
        var unknown = new MigrationHistoryEntry(9, "0009_other_thing", "sha-256:" + new string('a', 64));

        var state = MigrationStateEvaluator.Evaluate(Catalog(S1), [Applied(S1), unknown]);

        state.Kind.ShouldBe(MigrationStateKind.Current);
        state.UnknownAppliedIdentities.ShouldBe([unknown.Identity]);
    }

    [Fact]
    public void ChecksumMismatchWinsOverSequenceConflict()
    {
        var bad = new MigrationHistoryEntry(3, S3.Identity, "sha-256:" + new string('0', 64));

        var state = MigrationStateEvaluator.Evaluate(Catalog(S1, S3), [bad]);

        state.Kind.ShouldBe(MigrationStateKind.ChecksumMismatch);
        state.Identity.ShouldBe(S3.Identity);
    }
}
