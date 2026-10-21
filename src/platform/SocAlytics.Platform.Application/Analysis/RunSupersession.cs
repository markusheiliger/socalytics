using SocAlytics.Platform.Application.Abstractions;
using SocAlytics.Platform.Application.Analysis.Results;
using SocAlytics.Platform.Application.Analysis.Stores;
using SocAlytics.Platform.Application.Telemetry;
using SocAlytics.Platform.Domain.Analysis;

namespace SocAlytics.Platform.Application.Analysis;

// Supersedes every run of a match whose recording set version is older than the current run's version. Callers own
// the unit of work and take LockMatchRunsAsync first; the current run, runs of its version, and runs that already
// have a supersession row are never touched.
public sealed class RunSupersession(
    IRunSupersessionStore supersessions,
    IAnalysisRunStore runs,
    RunCancellationEffects effects,
    IAuditTrail audit,
    TimeProvider time)
{
    public async Task ApplyAsync(Guid matchId, CancellationToken cancellationToken)
    {
        var entries = await supersessions.ListMatchRunsAsync(matchId, cancellationToken);
        var current = RecordingSetVersionOrder.SelectCurrent(entries, entry => entry.OrderKey);
        if (current is null)
        {
            return;
        }

        var now = time.GetUtcNow();
        var older = RecordingSetVersionOrder.SelectOlderThanCurrent(entries, entry => entry.OrderKey, entry => entry.RunId);
        foreach (var entry in older.Where(entry => !entry.HasSupersession))
        {
            var run = await runs.LockRunAsync(entry.RunId, cancellationToken)
                ?? throw new InvalidOperationException("The run to supersede cannot be locked.");
            var previousState = run.State;
            var active = previousState is AnalysisRunState.Requested or AnalysisRunState.Running;
            if (active)
            {
                await effects.ApplyAsync(run, CancellationCause.Superseded, null, null, cancellationToken);
                await audit.RecordAsync(
                    AnalysisAuditEvents.RunSuperseded(run.Id, run.MatchId, run.TeamId, current.RunId, previousState.ToString()),
                    cancellationToken);
            }

            await supersessions.InsertAsync(new NewRunSupersession(run.Id, run.MatchId, current.RunId, now, previousState), cancellationToken);
            AnalysisMetrics.RunSuperseded(active ? "active" : "ended");
        }
    }
}
