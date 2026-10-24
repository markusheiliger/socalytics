import agentCatalog from '../../../../src/i18n/en/agentCatalog.json';
import {
  ENABLEMENT_REASONS,
  REFUSAL_CODES,
  VIOLATION_CODES,
  enablementReasonText,
  refusalText,
  violationText,
} from '../../../../src/features/agent-catalog/outcomeText';

const lookup = (key: string): unknown =>
  key.split('.').reduce<unknown>((node, part) => (node as Record<string, unknown> | undefined)?.[part], agentCatalog);

describe('violationText', () => {
  it('knows the 24 violation codes of the registry', () => {
    expect(VIOLATION_CODES).toHaveLength(24);
  });

  it.each(VIOLATION_CODES)('maps %s to an existing text', (code) => {
    const text = violationText(code, 'some detail');
    expect(text.key).toBe(`violations.${code}`);
    expect(typeof lookup(text.key)).toBe('string');
    expect(text.values).toEqual({ code, detail: 'some detail' });
  });

  it('uses a distinct key for every code', () => {
    expect(new Set(VIOLATION_CODES.map((code) => violationText(code).key)).size).toBe(VIOLATION_CODES.length);
  });

  it('shows the raw code and detail for an unknown code', () => {
    const text = violationText('something-new', 'why');
    expect(text).toEqual({ key: 'violations.unknown', values: { code: 'something-new', detail: 'why' } });
    expect(lookup(text.key)).toEqual(expect.stringContaining('{{code}}'));
  });
});

describe('refusalText', () => {
  it.each(REFUSAL_CODES)('maps %s to an existing text', (code) => {
    const text = refusalText(code);
    expect(text.key).toBe(`refusals.${code}`);
    expect(typeof lookup(text.key)).toBe('string');
  });

  it('uses a distinct key for every code', () => {
    expect(new Set(REFUSAL_CODES.map((code) => refusalText(code).key)).size).toBe(REFUSAL_CODES.length);
  });

  it('falls back to the generic text with the raw code', () => {
    const text = refusalText('brand-new');
    expect(text).toEqual({ key: 'refusals.generic', values: { code: 'brand-new' } });
    expect(lookup(text.key)).toEqual(expect.stringContaining('{{code}}'));
  });
});

describe('enablementReasonText', () => {
  it.each(ENABLEMENT_REASONS)('maps %s to an existing text', (reason) => {
    const text = enablementReasonText(reason);
    expect(text.key).toBe(`enablementReasons.${reason}`);
    expect(typeof lookup(text.key)).toBe('string');
  });

  it('falls back to the raw reason', () => {
    const text = enablementReasonText('brand-new');
    expect(text).toEqual({ key: 'enablementReasons.unknown', values: { reason: 'brand-new' } });
    expect(lookup(text.key)).toEqual(expect.stringContaining('{{reason}}'));
  });
});
