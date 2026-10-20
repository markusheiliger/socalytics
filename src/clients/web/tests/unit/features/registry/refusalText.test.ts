import registry from '../../../../src/i18n/en/registry.json';
import { refusalMessage, refusalMessages } from '../../../../src/features/registry/refusalText';
import type { RegistryRefusal } from '../../../../src/platform/models/registry';

const lookup = (key: string): unknown => key.split('.').reduce<unknown>((node, part) => (node as Record<string, unknown> | undefined)?.[part], registry);
const refusal = (code: string, details: RegistryRefusal['details'] = []): RegistryRefusal => ({ operation: 'imageRefused', code, details, retryable: false });

const CODES = [
  'reference-not-digest', 'reference-not-allowed', 'image-not-found', 'registry-unavailable', 'digest-mismatch',
  'manifest-missing', 'manifest-invalid', 'labels-mismatch', 'unsupported-media-type',
  'profile-contradicts-declaration', 'image-not-registered', 'profile-content-exists', 'profile-not-draft',
  'profile-in-default-workflow', 'profile-already-retired',
  'profile-not-found', 'profile-not-approved', 'missing-upstream', 'schema-incompatible', 'cycle', 'invalid-graph',
  'requested-capability-missing', 'unknown-completion-policy', 'invalid-segmentation-policy', 'workflow-version-exists',
  'workflow-not-resolvable',
];

describe('refusalMessage', () => {
  it.each(CODES)('maps %s to an existing text', (code) => {
    const text = refusalMessage(refusal(code));
    expect(text.key).toMatch(/^refusal\.(image|profile|workflow|default)\./);
    expect(text.key.endsWith(code)).toBe(true);
    expect(typeof lookup(text.key)).toBe('string');
  });

  it('names the node of a nodes.<key> field', () => {
    const text = refusalMessage(refusal('profile-not-found', [{ field: 'nodes.detect', code: 'profile-not-found' }]));
    expect(text).toEqual({ key: 'refusal.workflow.profile-not-found', values: { code: 'profile-not-found', node: 'detect' } });
  });

  it('falls back to the generic text with the raw code for unknown codes', () => {
    const text = refusalMessage(refusal('something-new'));
    expect(text).toEqual({ key: 'refusal.generic', values: { code: 'something-new' } });
    expect(lookup(text.key)).toEqual(expect.stringContaining('{{code}}'));
  });
});

describe('refusalMessages', () => {
  it('gives one message per violation of workflow-invalid', () => {
    const texts = refusalMessages(
      refusal('workflow-invalid', [
        { field: 'workflow', code: 'cycle' },
        { field: 'nodes.track', code: 'profile-not-approved' },
        { field: 'workflow', code: 'brand-new' },
      ]),
    );
    expect(texts.map((text) => text.key)).toEqual(['refusal.workflow.cycle', 'refusal.workflow.profile-not-approved', 'refusal.generic']);
    expect(texts[1].values.node).toBe('track');
    expect(texts[2].values.code).toBe('brand-new');
  });

  it('falls back to the generic text for workflow-invalid without details', () => {
    expect(refusalMessages(refusal('workflow-invalid'))).toEqual([{ key: 'refusal.generic', values: { code: 'workflow-invalid' } }]);
  });

  it('gives one message for a plain code', () => {
    expect(refusalMessages(refusal('cycle'))).toHaveLength(1);
  });
});
