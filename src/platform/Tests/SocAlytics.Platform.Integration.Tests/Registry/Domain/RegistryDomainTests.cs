using Shouldly;
using SocAlytics.Platform.Domain.Analysis;
using SocAlytics.Platform.Domain.Registry;
using Xunit;

namespace SocAlytics.Platform.Integration.Tests.Registry.Domain;

internal static class RegistryDomainFixtures
{
    public const string ImageDigest = "sha256:1111111111111111111111111111111111111111111111111111111111111111";
    public const string ModelDigest = "sha256:2222222222222222222222222222222222222222222222222222222222222222";
    public const string SegmentationDigest = "sha-256:3333333333333333333333333333333333333333333333333333333333333333";

    public static readonly Guid Creator = Guid.Parse("00000000-0000-0000-0000-000000000001");
    public static readonly Guid Approver = Guid.Parse("00000000-0000-0000-0000-000000000002");
    public static readonly DateTimeOffset Now = new(2026, 10, 13, 0, 0, 0, TimeSpan.Zero);

    public static CapabilityDeclaration Declaration(
        string capabilityId = "tracking",
        string version = "1.0.0",
        ExecutionScope scope = ExecutionScope.Segment,
        string videoAccess = "required",
        string outputSchema = "tracking-v1",
        params DeclaredUpstream[] upstream) => new(
            ImageDigest,
            capabilityId,
            version,
            scope == ExecutionScope.Segment ? CapabilityTier.LowLevel : CapabilityTier.HighLevel,
            scope,
            ModelDigest,
            videoAccess,
            outputSchema,
            [],
            ["cuda"],
            ["gpu"],
            new Dictionary<string, string> { ["memory"] = "8Gi" },
            upstream,
            new DeclaredRequirements(2, 60, 120, 300));

    public static AnalystProfileContent Content(CapabilityDeclaration d) => new(
        d.CapabilityId,
        d.CapabilityVersion,
        d.ImageDigest,
        d.ModelDigest,
        d.RuntimeConstraints,
        d.Accelerators,
        d.Resources,
        3,
        90,
        180,
        600,
        d.VideoAccess,
        d.Upstream.ToDictionary(u => u.CapabilityId, u => u.SchemaVersion),
        d.OutputSchemaVersion);

    public static AnalystProfile Profile(CapabilityDeclaration d, string id, bool approved = true)
    {
        var profile = AnalystProfile.CreateDraft(id, 1, Content(d), Creator, Now);
        return approved ? profile.Approve(Approver, Now) : profile;
    }
}

public sealed class AnalystProfileTests
{
    private static AnalystProfile Draft() =>
        AnalystProfile.CreateDraft("p", 1, RegistryDomainFixtures.Content(RegistryDomainFixtures.Declaration()), RegistryDomainFixtures.Creator, RegistryDomainFixtures.Now);

    [Fact]
    public void Equal_content_gives_an_equal_digest_and_different_content_a_different_one()
    {
        var a = RegistryDomainFixtures.Content(RegistryDomainFixtures.Declaration());
        var b = a with { RuntimeConstraints = ["cuda", "cuda"] };

        a.ComputeDigest().ShouldBe(b.ComputeDigest());
        (a with { LeaseSeconds = 91 }).ComputeDigest().ShouldNotBe(a.ComputeDigest());
    }

    [Fact]
    public void Draft_can_be_approved_and_retired()
    {
        var approved = Draft().Approve(RegistryDomainFixtures.Approver, RegistryDomainFixtures.Now);
        approved.State.ShouldBe(ProfileState.Approved);
        approved.IsSelfApproval.ShouldBeFalse();
        approved.Retire(RegistryDomainFixtures.Approver, RegistryDomainFixtures.Now).State.ShouldBe(ProfileState.Retired);
        Draft().Retire(RegistryDomainFixtures.Creator, RegistryDomainFixtures.Now).State.ShouldBe(ProfileState.Retired);
    }

    [Fact]
    public void Self_approval_is_marked() =>
        Draft().Approve(RegistryDomainFixtures.Creator, RegistryDomainFixtures.Now).IsSelfApproval.ShouldBeTrue();

    [Fact]
    public void Invalid_transitions_are_refused()
    {
        var approved = Draft().Approve(RegistryDomainFixtures.Approver, RegistryDomainFixtures.Now);
        var retired = approved.Retire(RegistryDomainFixtures.Approver, RegistryDomainFixtures.Now);

        Should.Throw<InvalidProfileTransitionException>(() => approved.Approve(RegistryDomainFixtures.Approver, RegistryDomainFixtures.Now));
        Should.Throw<InvalidProfileTransitionException>(() => retired.Approve(RegistryDomainFixtures.Approver, RegistryDomainFixtures.Now));
        Should.Throw<InvalidProfileTransitionException>(() => retired.Retire(RegistryDomainFixtures.Approver, RegistryDomainFixtures.Now));
    }

    [Theory]
    [InlineData(ProfileState.Approved, ProfileState.Draft)]
    [InlineData(ProfileState.Retired, ProfileState.Draft)]
    [InlineData(ProfileState.Retired, ProfileState.Approved)]
    [InlineData(ProfileState.Draft, ProfileState.Draft)]
    public void Backward_and_identity_transitions_are_not_allowed(ProfileState from, ProfileState to) =>
        from.CanTransitionTo(to).ShouldBeFalse();
}

public sealed class ProfileValidationTests
{
    private static readonly CapabilityDeclaration Declaration = RegistryDomainFixtures.Declaration(
        upstream: new DeclaredUpstream("detection", "1.0.0", "detection-v1", DependencyKind.Required));

    [Fact]
    public void Consistent_profile_has_no_violations() =>
        ProfileValidation.Validate("p", RegistryDomainFixtures.Content(Declaration), Declaration).ShouldBeEmpty();

    [Fact]
    public void Contradicting_bindings_are_reported()
    {
        var content = RegistryDomainFixtures.Content(Declaration) with
        {
            CapabilityId = "other",
            CapabilityVersion = "2.0.0",
            ImageDigest = "sha256:" + new string('a', 64),
            ModelDigest = "sha256:" + new string('b', 64),
            OutputSchemaVersion = "tracking-v2",
        };

        ProfileValidation.Validate("p", content, Declaration).Select(v => v.Field).ShouldBe(
        [
            "content.capabilityId",
            "content.capabilityVersion",
            "content.imageDigest",
            "content.modelDigest",
            "content.outputSchemaVersion",
        ]);
    }

    [Fact]
    public void Values_below_the_requirement_minimums_are_reported()
    {
        var content = RegistryDomainFixtures.Content(Declaration) with
        {
            MaxAttempts = 1,
            LeaseSeconds = 59,
            StaleTimeoutSeconds = 119,
            ExecutionTimeoutSeconds = 299,
        };

        ProfileValidation.Validate("p", content, Declaration).Select(v => v.Field).ShouldBe(
        [
            "content.maxAttempts",
            "content.leaseSeconds",
            "content.staleTimeoutSeconds",
            "content.executionTimeoutSeconds",
        ]);
    }

    [Fact]
    public void Undeclared_runtime_accelerator_video_and_schema_values_are_reported()
    {
        var content = RegistryDomainFixtures.Content(Declaration) with
        {
            RuntimeConstraints = ["rocm"],
            Accelerators = ["tpu"],
            VideoPolicy = "forbidden",
            InputSchemaVersions = new Dictionary<string, string> { ["detection"] = "detection-v2", ["extra"] = "x" },
        };

        ProfileValidation.Validate("p", content, Declaration).Select(v => v.Field).ShouldBe(
        [
            "content.runtimeConstraints",
            "content.accelerators",
            "content.videoPolicy",
            "content.inputSchemaVersions.detection",
            "content.inputSchemaVersions.extra",
        ]);
    }

    [Fact]
    public void Missing_input_schema_version_is_reported()
    {
        var content = RegistryDomainFixtures.Content(Declaration) with { InputSchemaVersions = new Dictionary<string, string>() };

        ProfileValidation.Validate("p", content, Declaration).Select(v => v.Field).ShouldBe(["content.inputSchemaVersions.detection"]);
    }

    [Fact]
    public void Profile_id_must_be_compatible_when_the_declaration_lists_profiles()
    {
        var restricted = Declaration with { CompatibleProfiles = ["allowed"] };

        ProfileValidation.Validate("allowed", RegistryDomainFixtures.Content(restricted), restricted).ShouldBeEmpty();
        ProfileValidation.Validate("other", RegistryDomainFixtures.Content(restricted), restricted).Select(v => v.Field).ShouldBe(["profileId"]);
    }
}

public sealed class WorkflowValidationTests
{
    private static readonly CapabilityDeclaration Detection = RegistryDomainFixtures.Declaration("detection", outputSchema: "detection-v1");

    private static readonly CapabilityDeclaration Tracking = RegistryDomainFixtures.Declaration(
        "tracking",
        upstream: new DeclaredUpstream("detection", "1.0.0", "detection-v1", DependencyKind.Required));

    private static WorkflowNodeInput Node(string key, CapabilityDeclaration d, bool approved = true) =>
        new(key, RegistryDomainFixtures.Profile(d, "p-" + key, approved), d);

    private static WorkflowInput Input(
        WorkflowNodeInput[] nodes,
        WorkflowEdgeDefinition[] edges,
        string[]? requested = null,
        string completion = "all-requested-capabilities",
        int duration = 60,
        string? digest = RegistryDomainFixtures.SegmentationDigest) =>
        new(requested ?? ["tracking"], completion, new SegmentationPolicyInput(duration, digest), nodes, edges);

    private static string[] Codes(WorkflowInput input) => WorkflowValidation.Validate(input).Select(v => v.Code).ToArray();

    private static WorkflowEdgeDefinition Edge(string up, string down, string kind = "required", ConditionalPredicate? predicate = null) =>
        new(up, down, kind, predicate);

    [Fact]
    public void Valid_workflow_has_no_violations() =>
        Codes(Input([Node("det", Detection), Node("trk", Tracking)], [Edge("det", "trk")])).ShouldBeEmpty();

    [Fact]
    public void Non_approved_profiles_are_refused() =>
        Codes(Input([Node("det", Detection, approved: false), Node("trk", Tracking)], [Edge("det", "trk")])).ShouldBe([WorkflowValidation.ProfileNotApproved]);

    [Fact]
    public void Retired_profiles_are_refused()
    {
        var det = Node("det", Detection);
        det = det with { Profile = det.Profile.Retire(RegistryDomainFixtures.Approver, RegistryDomainFixtures.Now) };

        Codes(Input([det, Node("trk", Tracking)], [Edge("det", "trk")])).ShouldBe([WorkflowValidation.ProfileNotApproved]);
    }

    [Fact]
    public void Missing_required_upstream_is_refused() =>
        Codes(Input([Node("trk", Tracking)], [])).ShouldBe([WorkflowValidation.MissingUpstream]);

    [Fact]
    public void Required_upstream_through_an_optional_edge_is_refused() =>
        Codes(Input([Node("det", Detection), Node("trk", Tracking)], [Edge("det", "trk", "optional")])).ShouldBe([WorkflowValidation.MissingUpstream]);

    [Fact]
    public void Optional_upstream_may_be_absent()
    {
        var optional = RegistryDomainFixtures.Declaration(
            "tracking",
            upstream: new DeclaredUpstream("detection", "1.0.0", "detection-v1", DependencyKind.Optional));

        Codes(Input([Node("trk", optional)], [])).ShouldBeEmpty();
    }

    [Fact]
    public void Incompatible_schema_version_is_refused()
    {
        var detectionV2 = RegistryDomainFixtures.Declaration("detection", outputSchema: "detection-v2");

        Codes(Input([Node("det", detectionV2), Node("trk", Tracking)], [Edge("det", "trk")])).ShouldBe([WorkflowValidation.SchemaIncompatible]);
    }

    [Fact]
    public void Incompatible_capability_version_is_refused()
    {
        var detectionV2 = RegistryDomainFixtures.Declaration("detection", version: "2.0.0", outputSchema: "detection-v1");

        Codes(Input([Node("det", detectionV2), Node("trk", Tracking)], [Edge("det", "trk")])).ShouldBe([WorkflowValidation.SchemaIncompatible]);
    }

    [Fact]
    public void Cycles_are_refused()
    {
        var a = RegistryDomainFixtures.Declaration("a");
        var b = RegistryDomainFixtures.Declaration("b");

        Codes(Input([Node("a", a), Node("b", b)], [Edge("a", "b"), Edge("b", "a")], ["a"])).ShouldBe([WorkflowValidation.Cycle]);
    }

    [Fact]
    public void Conditional_edges_need_a_valid_predicate()
    {
        var nodes = new[] { Node("det", Detection), Node("trk", Tracking) };

        Codes(Input(nodes, [Edge("det", "trk", "conditional")])).ShouldBe([WorkflowValidation.InvalidGraph]);
        Codes(Input(nodes, [Edge("det", "trk", "required", ConditionalPredicate.NodeAccepted("det"))])).ShouldBe([WorkflowValidation.InvalidGraph]);
    }

    [Fact]
    public void Conditional_edge_with_a_predicate_is_accepted()
    {
        var conditional = RegistryDomainFixtures.Declaration(
            "tracking",
            upstream: new DeclaredUpstream("detection", "1.0.0", "detection-v1", DependencyKind.Conditional));

        Codes(Input(
            [Node("det", Detection), Node("trk", conditional)],
            [Edge("det", "trk", "conditional", ConditionalPredicate.CapabilityRequested("tracking"))])).ShouldBeEmpty();
    }

    [Fact]
    public void Requested_capabilities_must_exist_as_nodes()
    {
        Codes(Input([Node("det", Detection)], [], ["detection", "tracking"])).ShouldBe([WorkflowValidation.RequestedCapabilityMissing]);
        Codes(Input([Node("det", Detection)], [], [])).ShouldBe([WorkflowValidation.InvalidGraph]);
    }

    [Fact]
    public void Unknown_completion_policy_is_refused() =>
        Codes(Input([Node("det", Detection)], [], ["detection"], completion: "first-wins")).ShouldBe([WorkflowValidation.UnknownCompletionPolicy]);

    [Theory]
    [InlineData(0, RegistryDomainFixtures.SegmentationDigest)]
    [InlineData(-5, RegistryDomainFixtures.SegmentationDigest)]
    [InlineData(60, null)]
    [InlineData(60, "not-a-digest")]
    public void Invalid_segmentation_policy_is_refused(int duration, string? digest) =>
        Codes(Input([Node("det", Detection)], [], ["detection"], duration: duration, digest: digest)).ShouldBe([WorkflowValidation.InvalidSegmentationPolicy]);
}
