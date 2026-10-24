// Enumerations and limits the Agent Catalog forms hint at. The contracts under contracts/agent-catalog/ are
// authoritative; catalogLimits.test.ts keeps these values equal to them. Hints never replace platform validation.

export const RESPONSE_CONTRACT = 'agent-response/1';

export const PROMPT_SLOTS = ['agent_id', 'display_name', 'philosophy_profile', 'tool_policy', 'evidence_context'] as const;

export const PROVIDERS = ['openai', 'github-models', 'simulated'] as const;

export const NAME_PATTERN_SOURCE = '^[A-Za-z0-9._/:-]{1,128}$';
export const NAME_PATTERN = new RegExp(NAME_PATTERN_SOURCE);

export const REASONING_EFFORTS = ['low', 'medium', 'high'] as const;

export const PARAMETER_RANGES = {
  temperature: { min: 0, max: 2 },
  topP: { min: 0, max: 1 },
  maxOutputTokens: { min: 1, max: 32768 },
} as const;

export const ROUTES = ['user-to-coach', 'user-to-specialist', 'coach-to-specialist'] as const;

export const CATEGORIES = [
  'medical',
  'safeguarding',
  'welfare',
  'privacy',
  'sensitive-traits',
  'raw-data-access',
  'unauthorized-data',
  'autonomous-decision',
] as const;

export const RULES = [
  'simulation-disclosure',
  'non-impersonation',
  'non-fabrication',
  'evidence-citation',
  'tool-allowlist-only',
  'no-raw-data-access',
] as const;

export const CONTEXT_CATEGORIES = [
  'score-state',
  'match-period',
  'player-number-state',
  'opponent-phase-behavior',
  'evidence-availability',
] as const;

export const LIMITS = {
  promptText: { min: 1, max: 32000 },
  rationale: { min: 1, max: 1000 },
  provenance: { min: 1, max: 1000 },
  disclosure: { min: 20, max: 1000 },
  hardBoundaries: { min: 1, max: 20 },
  hardBoundaryLength: { min: 1, max: 500 },
  dimensions: { min: 1, max: 32 },
  weight: { min: 0, max: 100 },
  weightTotal: 100,
  perDimensionBound: { min: 1, max: 3 },
  maxPositiveMovement: { min: 1, max: 10 },
} as const;
