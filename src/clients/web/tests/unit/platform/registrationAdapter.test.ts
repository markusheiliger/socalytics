import { createRegistrationAdapter, toRegistration } from '../../../src/platform/adapters/registrationAdapter';
import { createPlatformApi } from '../../../src/platform/client';
import { PlatformFailure, toPlatformFailure, type PlatformFailureKind } from '../../../src/platform/failure';
import { isCurrent, isEnded, isOwnSubmission } from '../../../src/platform/models/registrations';

const ID = '11111111-1111-4111-8111-111111111111';
const MEMBER = '22222222-2222-4222-8222-222222222222';

const dto = (over: Record<string, unknown> = {}) => ({
  registrationId: ID,
  managerId: null,
  version: 3,
  state: 'pending_approval',
  deviceFingerprint: 'THNN-PKJR',
  deviceMetadata: { os: 'linux', osVersion: '6', architecture: 'x64', managerVersion: '1.0', claimedKeyProtection: 'software', hostLabel: 'Lab' },
  pairingOrigin: { sourceAddress: '10.0.0.1', requestedAt: '2026-10-13T10:00:00Z' },
  approvalNotice: 'Check the device.',
  submittedBy: MEMBER,
  submittedAt: '2026-10-13T10:00:00Z',
  createdAt: '2026-10-13T09:00:00Z',
  ...over,
});

type Responder = (url: string, init: RequestInit) => Response;
const ok = (body: unknown, status = 200) =>
  new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });
const problem = (status: number, body: Record<string, unknown>, headers: Record<string, string> = {}) =>
  new Response(JSON.stringify({ status, ...body }), {
    status,
    headers: { 'Content-Type': 'application/problem+json', ...headers },
  });

function setup(respond: Responder) {
  const calls: { url: string; init: RequestInit }[] = [];
  const api = createPlatformApi({
    getAntiforgeryToken: () => 'csrf',
    onSessionOutcome: () => undefined,
    onNetworkFailure: () => undefined,
    fetch: async (url, init) => {
      calls.push({ url, init });
      return respond(url, init);
    },
  });
  return { adapter: createRegistrationAdapter(api), calls };
}

const bodyOf = (init: RequestInit) => JSON.parse(new TextDecoder().decode(init.body as ArrayBuffer));
const methodOf = (init: RequestInit) => String(init.method).toUpperCase();

describe('registrationAdapter operations', () => {
  it('submitPairingCode posts the code and fingerprint and maps the registration', async () => {
    const { adapter, calls } = setup(() => ok(dto(), 201));
    const registration = await adapter.submitPairingCode('ABC', 'THNN-PKJR');
    expect(calls[0].url).toBe(`${window.location.origin}/api/v1/analyst-manager-registrations`);
    expect(methodOf(calls[0].init)).toBe('POST');
    expect(bodyOf(calls[0].init)).toEqual({ pairingCode: 'ABC', deviceFingerprint: 'THNN-PKJR' });
    expect(registration).toMatchObject({
      registrationId: ID,
      version: 3,
      state: 'pendingApproval',
      deviceFingerprint: 'THNN-PKJR',
      submittedBy: MEMBER,
      approvalNotice: 'Check the device.',
    });
    expect(registration.deviceMetadata).toMatchObject({ claimedKeyProtection: 'software', hostLabel: 'Lab' });
    expect(registration.pairingOrigin?.sourceAddress).toBe('10.0.0.1');
    expect(registration.status).toBeUndefined();
    expect(registration.connectivity).toBeUndefined();
  });

  it('list sends the state and continuation token with the wire state name', async () => {
    const { adapter, calls } = setup(() => ok({ items: [dto()], continuationToken: 'next' }));
    const page = await adapter.list({ state: 'pendingApproval', continuationToken: 'tok' });
    const url = new URL(calls[0].url);
    expect(url.pathname).toBe('/api/v1/analyst-manager-registrations');
    expect(url.searchParams.get('state')).toBe('pending_approval');
    expect(url.searchParams.get('continuationToken')).toBe('tok');
    expect(page.items).toHaveLength(1);
    expect(page.continuationToken).toBe('next');
  });

  it('list without filters sends no query', async () => {
    const { adapter, calls } = setup(() => ok({ items: [] }));
    const page = await adapter.list();
    expect(new URL(calls[0].url).search).toBe('');
    expect(page).toEqual({ items: [], continuationToken: undefined });
  });

  it('list refuses the unknown state as a programming error', async () => {
    const { adapter, calls } = setup(() => ok({ items: [] }));
    await expect(adapter.list({ state: 'unknown' })).rejects.toMatchObject({ kind: 'programmingError' });
    expect(calls).toHaveLength(0);
  });

  it('listAll follows the continuation token until done', async () => {
    const other = '33333333-3333-4333-8333-333333333333';
    const { adapter, calls } = setup((url) => {
      const token = new URL(url).searchParams.get('continuationToken');
      return token
        ? ok({ items: [dto({ registrationId: other })] })
        : ok({ items: [dto()], continuationToken: 'p2' });
    });
    const all = await adapter.listAll({ state: 'active' });
    expect(all.map((r) => r.registrationId)).toEqual([ID, other]);
    expect(calls).toHaveLength(2);
    expect(new URL(calls[1].url).searchParams.get('state')).toBe('active');
  });

  it('get reads one registration', async () => {
    const { adapter, calls } = setup(() => ok(dto({ state: 'active' })));
    const registration = await adapter.get(ID);
    expect(calls[0].url).toBe(`${window.location.origin}/api/v1/analyst-manager-registrations/${ID}`);
    expect(methodOf(calls[0].init)).toBe('GET');
    expect(registration.state).toBe('active');
  });

  it('approve posts to the approval resource', async () => {
    const { adapter, calls } = setup(() => ok(dto({ state: 'approved' })));
    expect((await adapter.approve(ID)).state).toBe('approved');
    expect(calls[0].url).toBe(`${window.location.origin}/api/v1/analyst-manager-registrations/${ID}/approval`);
    expect(methodOf(calls[0].init)).toBe('POST');
  });

  it('reject and revoke send the reason', async () => {
    const { adapter, calls } = setup(() => ok(dto({ state: 'rejected' })));
    await adapter.reject(ID, 'wrong device');
    await adapter.revoke(ID, 'lost');
    expect(calls[0].url).toMatch(/\/analyst-manager-registrations\/[^/]+\/rejection$/);
    expect(bodyOf(calls[0].init)).toEqual({ reason: 'wrong device' });
    expect(calls[1].url).toMatch(/\/analyst-manager-registrations\/[^/]+\/revocation$/);
    expect(bodyOf(calls[1].init)).toEqual({ reason: 'lost' });
  });
});

describe('registrationAdapter mapping', () => {
  it.each([
    ['pairing', 'pairing'],
    ['pending_approval', 'pendingApproval'],
    ['approved', 'approved'],
    ['active', 'active'],
    ['unregistering', 'unregistering'],
    ['unregistered', 'unregistered'],
    ['rejected', 'rejected'],
    ['expired', 'expired'],
    ['revoked', 'revoked'],
    ['something-new', 'unknown'],
    [null, 'unknown'],
  ])('maps state %s to %s', async (wire, state) => {
    const { adapter } = setup(() => ok(dto({ state: wire })));
    expect((await adapter.get(ID)).state).toBe(state);
  });

  it('maps unknown key protection, operating state, and connectivity to unknown', async () => {
    const { adapter } = setup(() =>
      ok(
        dto({
          deviceMetadata: { claimedKeyProtection: 'quantum' },
          status: { operatingState: 'melting', activeAnalysts: 1, maxAnalysts: 2, reportedAt: '2026-10-13T11:00:00Z' },
          connectivity: 'sideways',
        }),
      ),
    );
    const registration = await adapter.get(ID);
    expect(registration.deviceMetadata.claimedKeyProtection).toBe('unknown');
    expect(registration.status).toMatchObject({ operatingState: 'unknown', activeAnalysts: 1, maxAnalysts: 2 });
    expect(registration.connectivity).toBe('unknown');
  });

  it('maps the known operating states, key protections, and connectivity values', async () => {
    const { adapter } = setup(() =>
      ok(
        dto({
          deviceMetadata: { claimedKeyProtection: 'secure-enclave' },
          status: { operatingState: 'runtime-unavailable', runtimeUnavailableReason: 'no runtime' },
          connectivity: 'not-applicable',
        }),
      ),
    );
    const registration = await adapter.get(ID);
    expect(registration.deviceMetadata.claimedKeyProtection).toBe('secure-enclave');
    expect(registration.status).toMatchObject({ operatingState: 'runtimeUnavailable', runtimeUnavailableReason: 'no runtime' });
    expect(registration.connectivity).toBe('notApplicable');
  });

  it('treats a response without identity as incompatible', () => {
    expect(() => toRegistration(undefined)).toThrow(PlatformFailure);
    expect(() => toRegistration({ deviceFingerprint: 'X', version: 1 })).toThrow(PlatformFailure);
  });

  it('derives isCurrent, isEnded, and isOwnSubmission', () => {
    expect(isCurrent({ state: 'active' })).toBe(true);
    expect(isCurrent({ state: 'revoked' })).toBe(false);
    expect(isEnded({ state: 'expired' })).toBe(true);
    expect(isEnded({ state: 'unknown' })).toBe(false);
    expect(isCurrent({ state: 'unknown' })).toBe(false);
    expect(isOwnSubmission({ submittedBy: MEMBER }, MEMBER)).toBe(true);
    expect(isOwnSubmission({ submittedBy: MEMBER }, 'other')).toBe(false);
    expect(isOwnSubmission({}, MEMBER)).toBe(false);
  });
});

describe('registration error mapping', () => {
  const rows: [string, number, Record<string, unknown>, PlatformFailureKind][] = [
    ['400 pairing code invalid', 400, { code: 'analyst-manager-pairing-code-invalid' }, 'pairingRefused'],
    ['400 reason field', 400, { code: 'validation-failed', errors: [{ field: 'reason', code: 'required' }] }, 'validation'],
    ['400 state field', 400, { code: 'validation-failed', errors: [{ field: 'state', code: 'invalid' }] }, 'programmingError'],
    [
      '400 continuationToken field',
      400,
      { code: 'validation-failed', errors: [{ field: 'continuationToken', code: 'invalid' }] },
      'programmingError',
    ],
    ['409 invalid-state-transition', 409, { code: 'invalid-state-transition' }, 'decisionConflict'],
    ['409 registration expired', 409, { code: 'analyst-manager-registration-expired' }, 'decisionConflict'],
    ['409 other', 409, { code: 'conflict' }, 'conflict'],
    ['403', 403, { code: 'forbidden' }, 'notPermitted'],
    ['401', 401, { code: 'unauthenticated' }, 'sessionEnded'],
    ['500', 500, {}, 'unexpected'],
  ];

  it.each(rows)('maps %s', async (_name, status, body, kind) => {
    const { adapter } = setup(() => problem(status, body));
    await expect(adapter.approve(ID)).rejects.toMatchObject({ kind, status });
  });

  it('maps a 404 on submission to pairingRefused', async () => {
    const { adapter } = setup(() => problem(404, { code: 'not-found' }));
    await expect(adapter.submitPairingCode('a', 'b')).rejects.toMatchObject({ kind: 'pairingRefused', status: 404 });
  });

  it('keeps a 404 on other operations as incompatible', async () => {
    const { adapter } = setup(() => problem(404, { code: 'not-found' }));
    await expect(adapter.get(ID)).rejects.toMatchObject({ kind: 'incompatible' });
  });

  it('maps 429 to rateLimited with retryAfterSeconds from Retry-After', async () => {
    const { adapter } = setup(() => problem(429, { code: 'analyst-manager-rate-limited' }, { 'Retry-After': '42' }));
    const failure = await adapter.submitPairingCode('a', 'b').catch((e: unknown) => e);
    expect(failure).toMatchObject({ kind: 'rateLimited', status: 429, retryAfterSeconds: 42 });
  });

  it('maps 429 without a usable Retry-After to rateLimited without a wait time', async () => {
    const { adapter } = setup(() => problem(429, { code: 'analyst-manager-rate-limited' }, { 'Retry-After': 'soon' }));
    const failure = await adapter.submitPairingCode('a', 'b').catch((e: unknown) => e);
    expect(failure).toMatchObject({ kind: 'rateLimited' });
    expect((failure as PlatformFailure).retryAfterSeconds).toBeUndefined();
  });

  it('maps rate limiting from an error shape with response headers', () => {
    const error = Object.assign(new Error('api'), {
      responseStatusCode: 429,
      responseHeaders: { 'retry-after': ['7'] },
      additionalData: { code: 'analyst-manager-rate-limited' },
    });
    expect(toPlatformFailure(error)).toMatchObject({ kind: 'rateLimited', retryAfterSeconds: 7 });
  });
});
