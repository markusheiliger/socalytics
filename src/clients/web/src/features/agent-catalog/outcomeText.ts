// A key of the `agentCatalog` namespace with its values.
export interface CatalogOutcomeText {
  key: string;
  values: Record<string, string>;
}

export const VIOLATION_CODES = [
  'schema-violation', 'unknown-tool', 'tool-outside-baseline', 'unknown-agent-id', 'group-mismatch',
  'artifact-not-found', 'artifact-kind-mismatch', 'missing-dimension', 'unknown-dimension', 'weight-out-of-range',
  'weights-total-not-100', 'rationale-missing', 'dimension-set-not-found', 'dimension-set-incompatible',
  'dimension-set-ids-changed', 'modifier-bound-exceeded', 'modifier-movement-exceeded',
  'context-category-not-permitted', 'profile-slot-missing', 'profile-slot-not-allowed', 'disclosure-missing',
  'safety-rule-missing', 'superseded-profile-invalid', 'provider-not-permitted',
] as const;

export const REFUSAL_CODES = [
  'agent-catalog-version-exists', 'agent-version-not-draft', 'differentiation-review-required',
  'agent-version-not-approved', 'agent-version-enabled', 'agent-version-not-enableable',
  'coach-agent-enablement-unsupported', 'agent-not-found',
] as const;

export const ENABLEMENT_REASONS = ['not-approved', 'retired', 'simulated-provider'] as const;

const VIOLATIONS: ReadonlySet<string> = new Set(VIOLATION_CODES);
const REFUSALS: ReadonlySet<string> = new Set(REFUSAL_CODES);
const REASONS: ReadonlySet<string> = new Set(ENABLEMENT_REASONS);

// Unknown codes fall back to a text that shows the raw code.
export function violationText(code: string, detail = ''): CatalogOutcomeText {
  return VIOLATIONS.has(code)
    ? { key: `violations.${code}`, values: { code, detail } }
    : { key: 'violations.unknown', values: { code, detail } };
}

export function refusalText(code: string): CatalogOutcomeText {
  return REFUSALS.has(code)
    ? { key: `refusals.${code}`, values: { code } }
    : { key: 'refusals.generic', values: { code } };
}

export function enablementReasonText(reason: string): CatalogOutcomeText {
  return REASONS.has(reason)
    ? { key: `enablementReasons.${reason}`, values: { reason } }
    : { key: 'enablementReasons.unknown', values: { reason } };
}
