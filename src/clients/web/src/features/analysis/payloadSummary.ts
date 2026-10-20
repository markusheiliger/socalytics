import type { JsonValue } from '../../platform/models/analysis';

const MAX_FIELDS = 6;
const MAX_SCALAR_LENGTH = 60;

function cut(text: string): string {
  const characters = Array.from(text);
  return characters.length > MAX_SCALAR_LENGTH ? `${characters.slice(0, MAX_SCALAR_LENGTH - 1).join('')}…` : text;
}

function summarizeValue(value: JsonValue): string {
  if (Array.isArray(value)) {
    return `[${value.length} items]`;
  }
  if (value !== null && typeof value === 'object') {
    return `{${Object.keys(value).length} fields}`;
  }
  return cut(typeof value === 'string' ? value : String(value));
}

export function summarizePayload(value: JsonValue): string {
  if (value === null || typeof value !== 'object' || Array.isArray(value)) {
    return summarizeValue(value);
  }
  const entries = Object.entries(value);
  if (entries.length === 0) {
    return summarizeValue(value);
  }
  const shown = entries.slice(0, MAX_FIELDS).map(([name, field]) => `${cut(name)}: ${summarizeValue(field)}`);
  const remainder = entries.length - shown.length;
  return remainder > 0 ? `${shown.join(', ')}, +${remainder} more` : shown.join(', ');
}

export function fullPayloadText(value: JsonValue): string {
  return JSON.stringify(value, null, 2);
}
