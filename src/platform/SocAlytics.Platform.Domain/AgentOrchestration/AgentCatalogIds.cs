namespace SocAlytics.Platform.Domain.AgentOrchestration;

// Identifiers and domains of docs/architecture/intelligence-agent-catalog.md.
public static class AgentCatalogIds
{
	public const string SpecialistPrefix = "specialist-";

	public const string SpecialistFitness = "specialist-fitness";
	public const string SpecialistGoalkeeping = "specialist-goalkeeping";
	public const string SpecialistTactics = "specialist-tactics";
	public const string SpecialistVideoAnalysis = "specialist-video-analysis";
	public const string SpecialistScouting = "specialist-scouting";
	public const string SpecialistPlayerDevelopment = "specialist-player-development";

	public const string CoachJurgenKlopp = "coach-jurgen-klopp";
	public const string CoachPepGuardiola = "coach-pep-guardiola";
	public const string CoachJoseMourinho = "coach-jose-mourinho";

	public static IReadOnlyDictionary<string, string> SpecialistDomains { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
	{
		[SpecialistFitness] = "Football workload, fatigue, recovery, and conditioning",
		[SpecialistGoalkeeping] = "Goalkeeper positioning, shot context, distribution, sweeping, crosses, and set plays",
		[SpecialistTactics] = "Team phases, possession, pressing, transitions, formations, spacing, and set plays",
		[SpecialistVideoAnalysis] = "Evidence retrieval, sequence comparison, and clip curation",
		[SpecialistScouting] = "Role fit, opponent tendencies, and repeated observable behavior",
		[SpecialistPlayerDevelopment] = "Individual learning objectives, exercises, progression, and review",
	};

	public static IReadOnlyList<string> Specialists { get; } = [.. SpecialistDomains.Keys];

	public static IReadOnlyList<string> Coaches { get; } = [CoachJurgenKlopp, CoachPepGuardiola, CoachJoseMourinho];

	public static bool IsSpecialist(string? agentId) =>
		agentId is not null && SpecialistDomains.ContainsKey(agentId);

	public static bool IsCoach(string? agentId) =>
		agentId is not null && Coaches.Contains(agentId, StringComparer.Ordinal);
}
