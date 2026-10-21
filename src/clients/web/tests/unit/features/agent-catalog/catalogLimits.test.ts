import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { describe, expect, it } from 'vitest';
import {
  CATEGORIES,
  CONTEXT_CATEGORIES,
  LIMITS,
  NAME_PATTERN,
  NAME_PATTERN_SOURCE,
  PARAMETER_RANGES,
  PROMPT_SLOTS,
  PROVIDERS,
  REASONING_EFFORTS,
  RESPONSE_CONTRACT,
  ROUTES,
  RULES,
} from '../../../../src/features/agent-catalog/catalogLimits';

type Schema = Record<string, any>;

// Vitest runs from src/clients/web; the contracts live at the repository root.
const schema = (kind: string): Schema =>
  JSON.parse(readFileSync(resolve(process.cwd(), '../../../contracts/agent-catalog', kind, 'v1', `${kind}.schema.json`), 'utf8')) as Schema;

const range = (node: Schema, min: string, max: string) => ({ min: node[min], max: node[max] });

describe('catalogLimits', () => {
  it('matches the prompt template contract', () => {
    const properties = schema('prompt-template').properties;
    expect(properties.responseContract.const).toBe(RESPONSE_CONTRACT);
    expect(properties.slots.items.enum).toEqual([...PROMPT_SLOTS]);
    expect(range(properties.text, 'minLength', 'maxLength')).toEqual(LIMITS.promptText);
  });

  it('matches the model selection contract', () => {
    const properties = schema('model-selection').properties;
    expect(properties.provider.enum).toEqual([...PROVIDERS]);
    expect(properties.model.pattern).toBe(NAME_PATTERN_SOURCE);
    expect(properties.deployment.pattern).toBe(NAME_PATTERN_SOURCE);
    expect(NAME_PATTERN.test('gpt-4.1:mini/x_1')).toBe(true);
    expect(NAME_PATTERN.test('bad name')).toBe(false);
    const parameters = properties.parameters.properties;
    expect(range(parameters.temperature, 'minimum', 'maximum')).toEqual(PARAMETER_RANGES.temperature);
    expect(range(parameters.topP, 'minimum', 'maximum')).toEqual(PARAMETER_RANGES.topP);
    expect(range(parameters.maxOutputTokens, 'minimum', 'maximum')).toEqual(PARAMETER_RANGES.maxOutputTokens);
    expect(parameters.reasoningEffort.enum).toEqual([...REASONING_EFFORTS]);
  });

  it('matches the safety policy contract', () => {
    const definition = schema('safety-policy');
    const properties = definition.properties;
    expect(properties.routes.items.enum).toEqual([...ROUTES]);
    expect(definition.$defs.category.enum).toEqual([...CATEGORIES]);
    expect(properties.rules.items.enum).toEqual([...RULES]);
    expect(range(properties.hardBoundaries, 'minItems', 'maxItems')).toEqual(LIMITS.hardBoundaries);
    expect(range(properties.hardBoundaries.items, 'minLength', 'maxLength')).toEqual(LIMITS.hardBoundaryLength);
  });

  it('matches the modifier policy contract', () => {
    const properties = schema('modifier-policy').properties;
    expect(range(properties.perDimensionBound, 'minimum', 'maximum')).toEqual(LIMITS.perDimensionBound);
    expect(range(properties.maxPositiveMovement, 'minimum', 'maximum')).toEqual(LIMITS.maxPositiveMovement);
    expect(properties.contextCategories.items.enum).toEqual([...CONTEXT_CATEGORIES]);
  });

  it('matches the philosophy profile contract', () => {
    const properties = schema('philosophy-profile').properties;
    const weight = properties.weights.additionalProperties.properties;
    expect(range(weight.weight, 'minimum', 'maximum')).toEqual(LIMITS.weight);
    expect(weight.weight.type).toBe('integer');
    expect(range(weight.rationale, 'minLength', 'maxLength')).toEqual(LIMITS.rationale);
    expect(range(weight.provenance, 'minLength', 'maxLength')).toEqual(LIMITS.provenance);
    expect(range(properties.weights, 'minProperties', 'maxProperties')).toEqual(LIMITS.dimensions);
  });

  it('matches the dimension set and agent version contracts', () => {
    expect(range(schema('dimension-set').properties.dimensions, 'minItems', 'maxItems')).toEqual(LIMITS.dimensions);
    expect(range(schema('agent-version').properties.disclosure, 'minLength', 'maxLength')).toEqual(LIMITS.disclosure);
  });
});
