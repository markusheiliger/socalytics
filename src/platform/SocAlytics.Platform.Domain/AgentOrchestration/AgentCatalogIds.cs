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

	public const string BaselineVersion = "1.0.0";

	public const string GroupCoach = "coach";
	public const string GroupSpecialist = "specialist";

	// The MCP Tool Policy allowlist; identifiers are written without "()".
	public static IReadOnlyList<string> ToolAllowlist { get; } =
	[
		"get_match",
		"get_player_stats",
		"find_counter_attacks",
		"find_turnovers",
		"get_heatmap",
		"find_ball_actions",
		"find_set_plays",
		"get_formations",
		"find_pressing_phases",
		"get_tactical_phases",
		"get_video_clip",
	];

	// The "All CAs" baseline of the MCP Tool Policy table.
	public static IReadOnlyList<string> CoachPermittedTools => ToolAllowlist;

	public static IReadOnlyDictionary<string, string> CoachDomains { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
	{
		[CoachJurgenKlopp] = "Publicly documented Jürgen Klopp-inspired coaching philosophy",
		[CoachPepGuardiola] = "Publicly documented Pep Guardiola-inspired coaching philosophy",
		[CoachJoseMourinho] = "Publicly documented José Mourinho-inspired coaching philosophy",
	};

	public static IReadOnlyDictionary<string, string> DisplayNames { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
	{
		[CoachJurgenKlopp] = "Jürgen Klopp-inspired Coach",
		[CoachPepGuardiola] = "Pep Guardiola-inspired Coach",
		[CoachJoseMourinho] = "José Mourinho-inspired Coach",
		[SpecialistFitness] = "Fitness Specialist",
		[SpecialistGoalkeeping] = "Goalkeeping Specialist",
		[SpecialistTactics] = "Tactics Specialist",
		[SpecialistVideoAnalysis] = "Video Analysis Specialist",
		[SpecialistScouting] = "Scouting Specialist",
		[SpecialistPlayerDevelopment] = "Player Development Specialist",
	};

	public static IReadOnlyDictionary<string, IReadOnlyList<string>> SpecialistPermittedTools { get; } = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
	{
		[SpecialistFitness] = ["get_match", "get_player_stats", "find_counter_attacks", "find_turnovers", "find_pressing_phases", "get_tactical_phases", "get_video_clip"],
		[SpecialistGoalkeeping] = ["get_match", "get_player_stats", "find_ball_actions", "find_set_plays", "get_formations", "get_tactical_phases", "get_video_clip"],
		[SpecialistTactics] = ToolAllowlist,
		[SpecialistVideoAnalysis] = ["get_match", "find_counter_attacks", "find_turnovers", "get_heatmap", "find_ball_actions", "find_set_plays", "get_formations", "find_pressing_phases", "get_tactical_phases", "get_video_clip"],
		[SpecialistScouting] = ToolAllowlist,
		[SpecialistPlayerDevelopment] = ["get_match", "get_player_stats", "find_counter_attacks", "find_turnovers", "get_heatmap", "find_ball_actions", "get_formations", "find_pressing_phases", "get_tactical_phases", "get_video_clip"],
	};

	// Coach Agents first, then Specialist Agents, as in the catalog tables.
	public static IReadOnlyList<string> All { get; } = [.. Coaches, .. Specialists];

	public static bool IsCatalogAgent(string? agentId) => IsCoach(agentId) || IsSpecialist(agentId);

	public static bool IsAllowlistedTool(string? tool) =>
		tool is not null && ToolAllowlist.Contains(tool, StringComparer.Ordinal);

	public static string? GroupOf(string? agentId) =>
		IsCoach(agentId) ? GroupCoach : IsSpecialist(agentId) ? GroupSpecialist : null;

	public static string? DomainOf(string? agentId) =>
		agentId is null ? null : SpecialistDomains.TryGetValue(agentId, out var specialist) ? specialist
			: CoachDomains.TryGetValue(agentId, out var coach) ? coach : null;

	public static IReadOnlyList<string>? PermittedToolsOf(string? agentId) =>
		IsCoach(agentId) ? CoachPermittedTools
			: agentId is not null && SpecialistPermittedTools.TryGetValue(agentId, out var tools) ? tools : null;
}

