import type { RegistryRefusal } from '../../platform/models/registry';
import type { RegistryText } from './edgeText';

const IMAGE = new Set([
  'reference-not-digest', 'reference-not-allowed', 'image-not-found', 'registry-unavailable', 'digest-mismatch',
  'manifest-missing', 'manifest-invalid', 'labels-mismatch', 'unsupported-media-type',
]);
const PROFILE = new Set([
  'profile-contradicts-declaration', 'image-not-registered', 'profile-content-exists', 'profile-not-draft',
  'profile-in-default-workflow', 'profile-already-retired',
]);
const WORKFLOW = new Set([
  'profile-not-found', 'profile-not-approved', 'missing-upstream', 'schema-incompatible', 'cycle', 'invalid-graph',
  'requested-capability-missing', 'unknown-completion-policy', 'invalid-segmentation-policy', 'workflow-version-exists',
]);
const DEFAULT = new Set(['workflow-not-resolvable']);

const NODE_FIELD = /^nodes\.(.+)$/;

// Maps a code to its translation key; unknown codes fall back to the generic message with the raw code.
export function refusalKey(code: string): string | undefined {
  if (IMAGE.has(code)) return `refusal.image.${code}`;
  if (PROFILE.has(code)) return `refusal.profile.${code}`;
  if (WORKFLOW.has(code)) return `refusal.workflow.${code}`;
  if (DEFAULT.has(code)) return `refusal.default.${code}`;
  return undefined;
}

const WRAPPERS = new Set(['workflow-invalid', 'validation-failed']);

function single(code: string, field?: string): RegistryText {
  const key = refusalKey(code);
  if (!key) return { key: 'refusal.generic', values: { code } };
  const node = field ? NODE_FIELD.exec(field)?.[1] : undefined;
  return { key, values: { code, ...(node === undefined ? {} : { node }) } };
}

// One message per violation of a wrapped refusal (`workflow-invalid`), otherwise one message for the code.
// The field `nodes.<key>` of a detail names the node.
export function refusalMessages(refusal: Pick<RegistryRefusal, 'code' | 'details'>): RegistryText[] {
  if (WRAPPERS.has(refusal.code) && refusal.details.length > 0) {
    return refusal.details.map((detail) => single(detail.code, detail.field));
  }
  return [single(refusal.code, refusal.details.find((detail) => NODE_FIELD.test(detail.field))?.field)];
}

export function refusalMessage(refusal: Pick<RegistryRefusal, 'code' | 'details'>): RegistryText {
  return refusalMessages(refusal)[0];
}
