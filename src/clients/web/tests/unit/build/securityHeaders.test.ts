import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { describe, expect, it } from 'vitest';
import { commonHeaders, contentSecurityPolicy } from '../../../build/securityHeaders';

const placeholder = '${SOCALYTICS_OBJECT_STORAGE_ORIGIN}';
const template = readFileSync(resolve(process.cwd(), 'container/default.conf.template'), 'utf8');

// Every add_header directive of the template, in order, with its quoted value.
const directives = [...template.matchAll(/add_header\s+(\S+)\s+"([^"]*)"\s+always;/g)].map((match) => ({
  name: match[1],
  value: match[2],
}));

const policies = directives.filter((directive) => directive.name === 'Content-Security-Policy').map((directive) => directive.value);

describe('container/default.conf.template', () => {
  it('sets the policy on the shell and on the API unavailable response', () => {
    expect(policies).toHaveLength(2);
  });

  it.each([0, 1])('policy %i without an origin equals the module output', (index) => {
    expect(policies[index].replaceAll(` ${placeholder}`, '')).toBe(contentSecurityPolicy());
  });

  it.each([0, 1])('policy %i with an origin equals the module output', (index) => {
    const origin = 'https://storage.example.test';
    expect(policies[index].replaceAll(placeholder, origin)).toBe(contentSecurityPolicy(origin));
  });

  it.each(Object.entries(commonHeaders))('sets %s with the module value', (name, value) => {
    const values = directives.filter((directive) => directive.name === name).map((directive) => directive.value);
    expect(values).toHaveLength(2);
    expect(values.every((candidate) => candidate === value)).toBe(true);
  });
});

describe('contentSecurityPolicy', () => {
  const origin = 'https://storage.example.test';

  it('keeps connect-src to self without an origin', () => {
    expect(contentSecurityPolicy()).toContain("connect-src 'self';");
    expect(contentSecurityPolicy()).toContain('media-src blob:;');
    expect(contentSecurityPolicy()).not.toContain('http');
  });

  it('adds the origin to connect-src and media-src only', () => {
    const without = contentSecurityPolicy().split('; ');
    const withOrigin = contentSecurityPolicy(origin).split('; ');
    expect(withOrigin).toHaveLength(without.length);
    const changed = withOrigin.filter((directive, index) => directive !== without[index]);
    expect(changed).toEqual([`media-src blob: ${origin}`, `connect-src 'self' ${origin}`]);
  });
});
