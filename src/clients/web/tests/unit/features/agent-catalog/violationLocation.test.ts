import { describe, expect, it } from 'vitest';
import { SUMMARY_LOCATION, violationLocation } from '../../../../src/features/agent-catalog/violationLocation';

describe('violationLocation', () => {
  it.each([
    ['/model', 'field:model'],
    ['/text', 'field:text'],
    ['/disclosure', 'field:disclosure'],
    ['/perDimensionBound', 'field:perDimensionBound'],
    ['/provider', 'field:provider'],
    ['/kind', 'field:kind'],
    ['/agentId', 'field:agentId'],
    ['/group', 'field:group'],
    ['/dimensionSet', 'field:dimensionSet'],
    ['/supersedes/version', 'field:supersedes'],
    ['/tools', 'field:tools'],
    ['/rules', 'field:rules'],
    ['/slots', 'field:slots'],
    ['/dimensions', 'field:dimensions'],
  ])('maps the top-level field %s', (pointer, expected) => {
    expect(violationLocation(pointer)).toBe(expected);
  });

  it.each([
    ['/tools/2', 'entry:tools:2'],
    ['/hardBoundaries/0', 'entry:hardBoundaries:0'],
    ['/slots/1', 'entry:slots:1'],
    ['/contextCategories/3', 'entry:contextCategories:3'],
    ['/dimensions/3/acceptedEvidence/1', 'entry:dimensions.3.acceptedEvidence:1'],
    ['/dimensions/4/id', 'entry:dimensions:4'],
  ])('maps the list entry %s', (pointer, expected) => {
    expect(violationLocation(pointer)).toBe(expected);
  });

  it.each([
    ['/weights', 'weights'],
    ['/weights/pressing', 'row:pressing'],
    ['/weights/pressing/weight', 'cell:pressing:weight'],
    ['/weights/pressing/rationale', 'cell:pressing:rationale'],
    ['/weights/pressing/provenance', 'cell:pressing:provenance'],
    ['/weights/pressing/other', 'row:pressing'],
  ])('maps the profile pointer %s', (pointer, expected) => {
    expect(violationLocation(pointer)).toBe(expected);
  });

  it('falls back to the top-level field for a deeper path without a list index', () => {
    expect(violationLocation('/dimensions/x/id/y')).toBe('field:dimensions');
  });

  it('unescapes dimension ids in profile pointers', () => {
    expect(violationLocation('/weights/a~1b~0c/weight')).toBe('cell:a/b~c:weight');
    expect(violationLocation('/weights/a~1b')).toBe('row:a/b');
  });

  it.each([
    ['/artifacts/promptTemplate', 'reference:promptTemplate'],
    ['/artifacts/modelSelection', 'reference:modelSelection'],
    ['/artifacts/toolPolicy', 'reference:toolPolicy'],
    ['/artifacts/toolPolicy/tools/2', 'reference:toolPolicy'],
    ['/artifacts/safetyPolicy', 'reference:safetyPolicy'],
    ['/artifacts/philosophyProfile', 'reference:philosophyProfile'],
    ['/artifacts/modifierPolicy', 'reference:modifierPolicy'],
  ])('maps the agent version reference %s', (pointer, expected) => {
    expect(violationLocation(pointer)).toBe(expected);
  });

  it.each([
    [''],
    [undefined],
    [null],
    ['model'],
    ['/'],
    ['//x'],
    ['/artifacts'],
    ['/artifacts/unknown'],
    ['/9'],
    ['/bad~2token'],
  ])('sends the unknown or empty pointer %s to the summary', (pointer) => {
    expect(violationLocation(pointer as string | null | undefined)).toBe(SUMMARY_LOCATION);
  });
});
