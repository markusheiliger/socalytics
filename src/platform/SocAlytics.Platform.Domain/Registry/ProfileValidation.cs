using SocAlytics.Platform.Domain.Recordings;

namespace SocAlytics.Platform.Domain.Registry;

public static class ProfileValidation
{
    /// <summary>Returns the violations of a profile against its declaration; empty when the profile is consistent.</summary>
    public static IReadOnlyList<FieldViolation> Validate(string profileId, AnalystProfileContent content, CapabilityDeclaration declaration)
    {
        var violations = new List<FieldViolation>();

        if (string.IsNullOrWhiteSpace(profileId))
        {
            violations.Add(new("profileId", "The profile ID is required."));
        }
        else if (declaration.CompatibleProfiles.Count > 0 && !declaration.CompatibleProfiles.Contains(profileId, StringComparer.Ordinal))
        {
            violations.Add(new("profileId", "The declaration does not list this profile ID as compatible."));
        }

        RequireEqual(violations, "content.capabilityId", content.CapabilityId, declaration.CapabilityId);
        RequireEqual(violations, "content.capabilityVersion", content.CapabilityVersion, declaration.CapabilityVersion);
        RequireEqual(violations, "content.imageDigest", content.ImageDigest, declaration.ImageDigest);
        RequireEqual(violations, "content.modelDigest", content.ModelDigest, declaration.ModelDigest);
        RequireEqual(violations, "content.outputSchemaVersion", content.OutputSchemaVersion, declaration.OutputSchemaVersion);

        RequireMinimum(violations, "content.maxAttempts", content.MaxAttempts, declaration.Requirements.MinMaxAttempts);
        RequireMinimum(violations, "content.leaseSeconds", content.LeaseSeconds, declaration.Requirements.MinLeaseSeconds);
        RequireMinimum(violations, "content.staleTimeoutSeconds", content.StaleTimeoutSeconds, declaration.Requirements.MinStaleTimeoutSeconds);
        RequireMinimum(violations, "content.executionTimeoutSeconds", content.ExecutionTimeoutSeconds, declaration.Requirements.MinExecutionTimeoutSeconds);

        RequireSubset(violations, "content.runtimeConstraints", content.RuntimeConstraints, declaration.RuntimeConstraints);
        RequireSubset(violations, "content.accelerators", content.Accelerators, declaration.Accelerators);

        ValidateVideoPolicy(violations, content.VideoPolicy, declaration.VideoAccess);
        ValidateInputSchemaVersions(violations, content, declaration);

        return violations;
    }

    private static void ValidateVideoPolicy(List<FieldViolation> violations, string policy, string declaredAccess)
    {
        if (!VideoPolicies.IsKnown(policy))
        {
            violations.Add(new("content.videoPolicy", "The video policy must be required, optional, or forbidden."));
            return;
        }

        if (declaredAccess is VideoPolicies.Required or VideoPolicies.Forbidden && policy != declaredAccess)
        {
            violations.Add(new("content.videoPolicy", $"The declaration sets video access to '{declaredAccess}'."));
        }
    }

    private static void ValidateInputSchemaVersions(List<FieldViolation> violations, AnalystProfileContent content, CapabilityDeclaration declaration)
    {
        foreach (var upstream in declaration.Upstream)
        {
            if (!content.InputSchemaVersions.TryGetValue(upstream.CapabilityId, out var version))
            {
                violations.Add(new($"content.inputSchemaVersions.{upstream.CapabilityId}", "The input schema version of a declared upstream capability is required."));
            }
            else if (!string.Equals(version, upstream.SchemaVersion, StringComparison.Ordinal))
            {
                violations.Add(new($"content.inputSchemaVersions.{upstream.CapabilityId}", $"The declaration expects schema version '{upstream.SchemaVersion}'."));
            }
        }

        foreach (var capabilityId in content.InputSchemaVersions.Keys)
        {
            if (!declaration.Upstream.Any(u => string.Equals(u.CapabilityId, capabilityId, StringComparison.Ordinal)))
            {
                violations.Add(new($"content.inputSchemaVersions.{capabilityId}", "The declaration has no such upstream capability."));
            }
        }
    }

    private static void RequireEqual(List<FieldViolation> violations, string field, string actual, string expected)
    {
        if (!string.Equals(actual, expected, StringComparison.Ordinal))
        {
            violations.Add(new(field, $"The declaration requires '{expected}'."));
        }
    }

    private static void RequireMinimum(List<FieldViolation> violations, string field, int actual, int minimum)
    {
        if (actual <= 0 || actual < minimum)
        {
            violations.Add(new(field, $"The value must be positive and at least {minimum}."));
        }
    }

    private static void RequireSubset(List<FieldViolation> violations, string field, IReadOnlyList<string> actual, IReadOnlyList<string> declared)
    {
        foreach (var value in actual.Where(v => !declared.Contains(v, StringComparer.Ordinal)))
        {
            violations.Add(new(field, $"'{value}' is not declared by the manifest."));
        }
    }
}
