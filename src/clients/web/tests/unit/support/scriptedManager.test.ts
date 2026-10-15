// @vitest-environment node
import { createPublicKey, verify } from 'node:crypto';
import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import {
  ScriptedManager,
  createActivationProof,
  createClientAssertion,
  createDpopProof,
  generateScriptedKey,
  scriptedKeyFromJwk,
} from '../../e2e/support/scriptedManager';

const golden = (name: string) =>
  JSON.parse(
    readFileSync(fileURLToPath(new URL(`../../../../../../contracts/analyst-manager/registration/v1/${name}`, import.meta.url)), 'utf8'),
  );

const fixtures = golden('fixtures.json');
const key = scriptedKeyFromJwk(golden('test-key.jwk.json'));
const clock = { now: () => Date.parse(fixtures.clock) };

const decode = (jwt: string) => {
  const [h, p, s] = jwt.split('.');
  return {
    header: JSON.parse(Buffer.from(h, 'base64url').toString()),
    claims: JSON.parse(Buffer.from(p, 'base64url').toString()),
    signingInput: `${h}.${p}`,
    signature: Buffer.from(s, 'base64url'),
  };
};

const signatureValid = (jwt: string, jwk: object) => {
  const { signingInput, signature } = decode(jwt);
  return verify(
    'sha256',
    Buffer.from(signingInput),
    { key: createPublicKey({ key: jwk as never, format: 'jwk' }), dsaEncoding: 'ieee-p1363' },
    signature,
  );
};

describe('scripted Manager proofs match the golden fixtures', () => {
  it('derives the golden public key', () => {
    const { d: _d, ...publicJwk } = golden('test-key.jwk.json');
    expect(key.publicJwk).toEqual(publicJwk);
  });

  it('builds the activation proof', () => {
    const goldenProof = golden('activation.request.json').proof;
    const expected = decode(goldenProof);
    const proof = createActivationProof({
      key,
      clock,
      jti: () => expected.claims.jti,
      aud: expected.claims.aud,
      challenge: fixtures.challenge,
    });
    const built = decode(proof);
    expect(built.header).toEqual(expected.header);
    expect(built.claims).toEqual(expected.claims);
    expect(signatureValid(proof, key.publicJwk)).toBe(true);
    expect(signatureValid(goldenProof, key.publicJwk)).toBe(true);
  });

  it('builds the client assertion', () => {
    const expected = decode(golden('token.request.json').form.client_assertion);
    const assertion = createClientAssertion({
      key,
      clock,
      jti: () => expected.claims.jti,
      managerId: fixtures.managerId,
      aud: expected.claims.aud,
    });
    const built = decode(assertion);
    expect(built.header).toEqual(expected.header);
    expect(built.claims).toEqual(expected.claims);
    expect(signatureValid(assertion, key.publicJwk)).toBe(true);
  });

  it('builds the token DPoP proof without ath', () => {
    const expected = decode(golden('token.request.json').headers.DPoP);
    const proof = createDpopProof({ key, clock, jti: () => expected.claims.jti, method: 'POST', htu: expected.claims.htu });
    const built = decode(proof);
    expect(built.header).toEqual(expected.header);
    expect(built.claims).toEqual(expected.claims);
    expect(signatureValid(proof, built.header.jwk)).toBe(true);
  });

  it('builds the self-registration DPoP proof with the access token hash', () => {
    const expected = decode(golden('self-registration.request.json').headers.DPoP);
    const proof = createDpopProof({
      key,
      clock,
      jti: () => expected.claims.jti,
      method: 'GET',
      htu: expected.claims.htu,
      accessToken: fixtures.accessToken,
    });
    const built = decode(proof);
    expect(built.header).toEqual(expected.header);
    expect(built.claims).toEqual(expected.claims);
    expect(signatureValid(proof, built.header.jwk)).toBe(true);
  });

  it('is not verified by another key', () => {
    const proof = createDpopProof({ key, method: 'POST', htu: 'https://stamp.example.test/x' });
    expect(signatureValid(proof, generateScriptedKey().publicJwk)).toBe(false);
  });
});

describe('scripted Manager flow', () => {
  it('sends every request to the Web UI origin while proofs use the discovered URIs', async () => {
    const stamp = golden('stamp-discovery.response.json');
    const requests: { url: string; init?: RequestInit }[] = [];
    const reply = (body: unknown) => new Response(JSON.stringify(body), { status: 200 });
    const fakeFetch = (async (url: string, init?: RequestInit) => {
      requests.push({ url, init });
      if (url.endsWith('/stamp')) return reply(stamp);
      if (url.endsWith('/pairings')) return reply(golden('pairing.response.json'));
      if (url.endsWith('/activation')) return reply(golden('activation.response.json'));
      if (url.endsWith('/token')) return reply(golden('token.response.json'));
      return reply({ managerId: fixtures.managerId, state: 'active', reportAccepted: true, reportViolations: [] });
    }) as typeof fetch;

    const manager = new ScriptedManager({ origin: 'http://localhost:5173/', hostLabel: 'Scripted host', key, fetch: fakeFetch });
    const pairing = await manager.startPairing();
    expect(pairing.pairingCode).toBe(fixtures.pairingCode);
    const body = JSON.parse(String(requests[1].init?.body));
    expect(body.deviceMetadata).toMatchObject({ claimedKeyProtection: 'software', hostLabel: 'Scripted host' });
    expect(body.publicKey).toEqual(key.publicJwk);

    await manager.activate(fixtures.challenge);
    const activation = JSON.parse(String(requests.at(-1)?.init?.body));
    expect(decode(activation.proof).claims).toMatchObject({ aud: stamp.activationEndpoint, challenge: fixtures.challenge });

    const result = await manager.reportStatus({ operatingState: 'running' });
    expect(result.reportAccepted).toBe(true);
    const report = requests.at(-1)!;
    const headers = report.init?.headers as Record<string, string>;
    expect(report.url).toBe('http://localhost:5173/api/v1/analyst-managers/self/status');
    expect(headers.Authorization).toBe(`DPoP ${fixtures.accessToken}`);
    const dpop = decode(headers.DPoP);
    expect(dpop.claims).toMatchObject({
      htm: 'POST',
      htu: 'https://stamp.example.test/api/v1/analyst-managers/self/status',
    });
    expect(dpop.claims.ath).toBeTruthy();
    expect(requests.every((r) => r.url.startsWith('http://localhost:5173/api/'))).toBe(true);
  });
});
