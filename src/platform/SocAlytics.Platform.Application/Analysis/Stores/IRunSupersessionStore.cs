using SocAlytics.Platform.Domain.Analysis;

namespace SocAlytics.Platform.Application.Analysis.Stores;

public sealed record MatchRunEntry(
    Guid RunId,
    Guid TeamId,
    AnalysisRunState State,
    RecordingSetVersionKey Version,
    DateTimeOffset CreatedAt,
    Guid? SupersededByRunId)
{
    public AnalysisRunOrderKey OrderKey => new(Version, CreatedAt, RunId);

    public bool HasSupersession => SupersededByRunId is not null;
}

public sealed record NewRunSupersession(
    Guid RunId,
    Guid MatchId,
    Guid SupersedingRunId,
    DateTimeOffset SupersededAt,
    AnalysisRunState PreviousState);

public interface IRunSupersessionStore
{
    // Every run of the match with its version key and supersession row (if any), in id order.
    Task<IReadOnlyList<MatchRunEntry>> ListMatchRunsAsync(Guid matchId, CancellationToken cancellationToken);

    Task InsertAsync(NewRunSupersession supersession, CancellationToken cancellationToken);

    // Matches after the given match id (id order) with a run that has no supersession row and an older version than the match's current run.
    Task<IReadOnlyList<Guid>> ListMatchesNeedingSupersessionAsync(Guid? after, int batch, CancellationToken cancellationToken);
}
