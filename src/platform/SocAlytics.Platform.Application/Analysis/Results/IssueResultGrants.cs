using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using SocAlytics.Platform.Application.Abstractions;
using SocAlytics.Platform.Application.Abstractions.ObjectStorage;
using SocAlytics.Platform.Application.Abstractions.Persistence;
using SocAlytics.Platform.Application.Analysis.Stores;
using SocAlytics.Platform.Application.Telemetry;
using SocAlytics.Platform.Domain.Analysis;
using SocAlytics.Platform.Domain.Recordings;
using FieldViolation = SocAlytics.Platform.Application.Abstractions.FieldViolation;

namespace SocAlytics.Platform.Application.Analysis.Results;

public sealed record ResultObjectRequest(string? Name, long SizeBytes, string? Digest);

public sealed record IssueResultGrantsCommand(Guid AttemptId, long FencingToken, IReadOnlyList<ResultObjectRequest>? Objects);

public sealed record ResultGrant(string Name, string Method, Uri Url, IReadOnlyDictionary<string, string> RequiredHeaders, DateTimeOffset ExpiresAt);

public sealed record IssueResultGrantsResult(Guid RunId, Guid LogicalJobId, string ResultLocation, IReadOnlyList<ResultGrant> Grants);

public sealed partial class IssueResultGrantsHandler(
    IUnitOfWork unitOfWork,
    ILogicalJobStore jobs,
    IExecutionAttemptStore attempts,
    IResultDeclarationStore declarations,
    IObjectStorage storage,
    IAuditTrail audit,
    ICurrentAnalystManager manager,
    IOptions<AnalysisResultOptions> options,
    TimeProvider time)
{
    public const string ManifestName = "manifest.json";
    public const string ResultObjectsInvalidCode = "result-objects-invalid";
    public const string ResultObjectRedeclaredCode = "result-object-redeclared";
    public const string AttemptObsoleteCode = "attempt-obsolete";

    [GeneratedRegex("^[a-z0-9][a-z0-9._-]{0,127}$")]
    private static partial Regex NamePattern();

    public static string ResultLocation(Guid runId, Guid logicalJobId, Guid attemptId) =>
        string.Create(CultureInfo.InvariantCulture, $"results/{runId}/{logicalJobId}/{attemptId}/");

    public async Task<OperationResult<IssueResultGrantsResult>> HandleAsync(IssueResultGrantsCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var settings = options.Value;

        Guid? foreignRunId = null;
        Guid foreignJobId = default;
        OperationResult<IssueResultGrantsResult>? outcome = null;

        await using (var scope = await unitOfWork.BeginAsync(cancellationToken))
        {
            var attempt = await attempts.LockAsync(command.AttemptId, cancellationToken);
            if (attempt is null)
            {
                return OperationFailure.NotFound();
            }

            var job = await jobs.LockJobAsync(attempt.LogicalJobId, cancellationToken);
            if (job is null)
            {
                return OperationFailure.NotFound();
            }

            if (attempt.ManagerId != manager.ManagerId)
            {
                foreignRunId = job.RunId;
                foreignJobId = job.Id;
            }
            else
            {
                var now = time.GetUtcNow();
                if (attempt.State != ExecutionAttemptState.Active
                    || attempt.LeaseExpiresAt <= now
                    || attempt.FencingToken != command.FencingToken
                    || job.CurrentAttemptId != attempt.Id)
                {
                    return OperationFailure.Conflict(AttemptObsoleteCode);
                }

                var parsed = Validate(command, settings, out var violations);
                if (violations.Count > 0)
                {
                    return OperationFailure.Validation(ResultObjectsInvalidCode, violations.ToArray());
                }

                var existing = (await declarations.ListByAttemptAsync(attempt.Id, cancellationToken))
                    .ToDictionary(d => d.Name, StringComparer.Ordinal);
                var totalObjects = existing.Count;
                var totalBytes = existing.Values.Sum(d => d.SizeBytes);
                foreach (var item in parsed)
                {
                    if (existing.TryGetValue(item.Name, out var known))
                    {
                        if (!string.Equals(known.Digest, item.Digest.ToString(), StringComparison.Ordinal) || known.SizeBytes != item.SizeBytes)
                        {
                            return OperationFailure.Conflict(ResultObjectRedeclaredCode);
                        }
                    }
                    else
                    {
                        totalObjects++;
                        totalBytes += item.SizeBytes;
                    }
                }

                if (totalObjects > settings.MaxObjectsPerAttempt)
                {
                    return OperationFailure.Validation(ResultObjectsInvalidCode, new FieldViolation("/objects", "maxItems"));
                }

                if (totalBytes > settings.MaxAttemptBytes)
                {
                    return OperationFailure.Validation(ResultObjectsInvalidCode, new FieldViolation("/objects", "maxBytes"));
                }

                var location = ResultLocation(job.RunId, job.Id, attempt.Id);
                var lifetimeEnd = now + settings.GrantLifetime;
                var expires = lifetimeEnd <= attempt.LeaseExpiresAt ? lifetimeEnd : attempt.LeaseExpiresAt;

                var grants = new List<ResultGrant>(parsed.Count);
                foreach (var item in parsed)
                {
                    var grant = storage.PresignPutObject(location + item.Name, item.SizeBytes, item.Digest, expires);
                    grants.Add(new ResultGrant(item.Name, grant.Method, grant.Url, grant.RequiredHeaders, grant.ExpiresAt));
                }

                foreach (var item in parsed)
                {
                    var declared = await declarations.DeclareAsync(
                        new ResultObjectDeclaration(attempt.Id, item.Name, item.SizeBytes, item.Digest.ToString(), now), cancellationToken);
                    if (declared == ResultDeclarationOutcome.DigestConflict)
                    {
                        return OperationFailure.Conflict(ResultObjectRedeclaredCode);
                    }
                }

                await scope.CommitAsync(cancellationToken);
                AnalysisMetrics.ResultGrantsIssued(grants.Count);
                outcome = new IssueResultGrantsResult(job.RunId, job.Id, location, grants);
            }
        }

        if (outcome is not null)
        {
            return outcome;
        }

        await audit.RecordIndependentAsync(
            AnalysisAuditEvents.ResultGrantRefused(
                command.AttemptId, foreignJobId, foreignRunId!.Value, manager.ManagerId, AnalysisAuditEvents.ForeignAttemptReason),
            cancellationToken);
        return OperationFailure.Forbidden();
    }

    private sealed record ParsedObject(string Name, long SizeBytes, Sha256Digest Digest);

    private static List<ParsedObject> Validate(IssueResultGrantsCommand command, AnalysisResultOptions settings, out List<FieldViolation> violations)
    {
        violations = [];
        var parsed = new List<ParsedObject>();
        var objects = command.Objects;
        if (objects is null || objects.Count == 0)
        {
            violations.Add(new FieldViolation("/objects", "minItems"));
            return parsed;
        }

        if (objects.Count > settings.MaxObjectsPerAttempt)
        {
            violations.Add(new FieldViolation("/objects", "maxItems"));
        }

        var names = new HashSet<string>(StringComparer.Ordinal);
        long requested = 0;
        for (var i = 0; i < objects.Count; i++)
        {
            var o = objects[i];
            var path = "/objects/" + i.ToString(CultureInfo.InvariantCulture);
            if (o is null || o.Name is null || !NamePattern().IsMatch(o.Name))
            {
                violations.Add(new FieldViolation(path + "/name", "pattern"));
                continue;
            }

            if (!names.Add(o.Name))
            {
                violations.Add(new FieldViolation(path + "/name", "uniqueItems"));
                continue;
            }

            var limit = o.Name == ManifestName ? Math.Min(settings.MaxManifestBytes, settings.MaxObjectBytes) : settings.MaxObjectBytes;
            if (o.SizeBytes < 1 || o.SizeBytes > limit)
            {
                violations.Add(new FieldViolation(path + "/sizeBytes", o.SizeBytes < 1 ? "minimum" : "maximum"));
                continue;
            }

            if (!Sha256Digest.TryParse(o.Digest, out var digest))
            {
                violations.Add(new FieldViolation(path + "/digest", "pattern"));
                continue;
            }

            requested += o.SizeBytes;
            parsed.Add(new ParsedObject(o.Name, o.SizeBytes, digest));
        }

        if (requested > settings.MaxAttemptBytes)
        {
            violations.Add(new FieldViolation("/objects", "maxBytes"));
        }

        return parsed;
    }
}
