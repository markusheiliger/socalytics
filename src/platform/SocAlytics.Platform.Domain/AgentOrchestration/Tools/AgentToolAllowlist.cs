using System.Collections.Frozen;

namespace SocAlytics.Platform.Domain.AgentOrchestration.Tools;

public enum AgentToolAnswerKind
{
	AcceptedEvidence,
	AnalysisUnavailable,
}

public sealed record AgentToolEntry(
	string ToolId,
	string OperationId,
	string PathSegment,
	AgentToolAnswerKind Answers,
	string? ProducingCapability)
{
	public string PathTemplate => $"/api/v1/agent-tools/attempts/{{attemptId}}/{PathSegment}";

	public string AnswersWireName => Answers switch
	{
		AgentToolAnswerKind.AcceptedEvidence => "accepted-evidence",
		AgentToolAnswerKind.AnalysisUnavailable => "analysis-unavailable",
		_ => throw new InvalidOperationException("Unknown answer kind."),
	};
}

public static class AgentToolAllowlist
{
	public const string Version = "1.0.0";

	public static IReadOnlyList<AgentToolEntry> Entries { get; } =
	[
		new("get_match", "agentToolGetMatch", "get-match", AgentToolAnswerKind.AcceptedEvidence, null),
		new("get_video_clip", "agentToolGetVideoClip", "get-video-clip", AgentToolAnswerKind.AcceptedEvidence, null),
		new("get_player_stats", "agentToolGetPlayerStats", "get-player-stats", AgentToolAnswerKind.AnalysisUnavailable, null),
		new("find_counter_attacks", "agentToolFindCounterAttacks", "find-counter-attacks", AgentToolAnswerKind.AnalysisUnavailable, "counter-attack-analysis"),
		new("find_turnovers", "agentToolFindTurnovers", "find-turnovers", AgentToolAnswerKind.AnalysisUnavailable, "possession-and-transitions"),
		new("get_heatmap", "agentToolGetHeatmap", "get-heatmap", AgentToolAnswerKind.AnalysisUnavailable, "spatial-aggregation"),
		new("find_ball_actions", "agentToolFindBallActions", "find-ball-actions", AgentToolAnswerKind.AnalysisUnavailable, "ball-action-analysis"),
		new("find_set_plays", "agentToolFindSetPlays", "find-set-plays", AgentToolAnswerKind.AnalysisUnavailable, "set-play-analysis"),
		new("get_formations", "agentToolGetFormations", "get-formations", AgentToolAnswerKind.AnalysisUnavailable, "formation-analysis"),
		new("find_pressing_phases", "agentToolFindPressingPhases", "find-pressing-phases", AgentToolAnswerKind.AnalysisUnavailable, "pressing-analysis"),
		new("get_tactical_phases", "agentToolGetTacticalPhases", "get-tactical-phases", AgentToolAnswerKind.AnalysisUnavailable, "tactical-phase-aggregation"),
	];

	private static readonly FrozenDictionary<string, AgentToolEntry> ByToolId =
		Entries.ToFrozenDictionary(entry => entry.ToolId, StringComparer.Ordinal);

	public static bool TryGet(string? toolId, out AgentToolEntry entry)
	{
		if (toolId is not null && ByToolId.TryGetValue(toolId, out var found))
		{
			entry = found;
			return true;
		}

		entry = null!;
		return false;
	}

	public static bool Contains(string? toolId) => toolId is not null && ByToolId.ContainsKey(toolId);
}
