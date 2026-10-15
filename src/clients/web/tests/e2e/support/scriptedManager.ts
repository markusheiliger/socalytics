import { createHash, createPrivateKey, createPublicKey, generateKeyPairSync, randomBytes, sign, type JsonWebKey, type KeyObject } from 'node:crypto';

/** A Manager written for tests: ES256 P-256 key in memory, the registration flow of the platform's contracts. */

const b64url = (input: Buffer | string) => Buffer.from(input).toString('base64url');

export interface ProofClock {
  now(): number;
}

export interface ProofOptions {
  key: ScriptedKey;
  clock?: ProofClock;
  jti?: () => string;
}

export interface ScriptedKey {
  privateKey: KeyObject;
  publicJwk: { kty: 'EC'; crv: 'P-256'; x: string; y: string };
}

const defaultClock: ProofClock = { now: () => Date.now() };
const defaultJti = () => randomBytes(16).toString('hex');

export function generateScriptedKey(): ScriptedKey {
  return fromPrivateKey(generateKeyPairSync('ec', { namedCurve: 'P-256' }).privateKey);
}

/** Builds a key from a private JWK, for example the golden `test-key.jwk.json`. */
export function scriptedKeyFromJwk(jwk: JsonWebKey): ScriptedKey {
  return fromPrivateKey(createPrivateKey({ key: jwk, format: 'jwk' }));
}

function fromPrivateKey(privateKey: KeyObject): ScriptedKey {
  const jwk = createPublicKey(privateKey).export({ format: 'jwk' });
  return { privateKey, publicJwk: { kty: 'EC', crv: 'P-256', x: String(jwk.x), y: String(jwk.y) } };
}

function jws(header: object, payload: object, key: ScriptedKey): string {
  const signingInput = `${b64url(JSON.stringify(header))}.${b64url(JSON.stringify(payload))}`;
  const signature = sign('sha256', Buffer.from(signingInput), { key: key.privateKey, dsaEncoding: 'ieee-p1363' });
  return `${signingInput}.${b64url(signature)}`;
}

const iatOf = (clock: ProofClock | undefined) => Math.floor((clock ?? defaultClock).now() / 1000);

/** Proof of key possession for `POST …/pairings/activation`; `aud` is the activation endpoint of the stamp discovery. */
export function createActivationProof(options: ProofOptions & { aud: string; challenge: string }): string {
  return jws(
    { typ: 'analyst-manager-activation+jwt', alg: 'ES256' },
    { aud: options.aud, challenge: options.challenge, iat: iatOf(options.clock), jti: (options.jti ?? defaultJti)() },
    options.key,
  );
}

/** Client assertion for the token endpoint; `aud` is the token endpoint of the stamp discovery. */
export function createClientAssertion(options: ProofOptions & { managerId: string; aud: string }): string {
  const iat = iatOf(options.clock);
  return jws(
    { alg: 'ES256', typ: 'JWT' },
    { iss: options.managerId, sub: options.managerId, aud: options.aud, iat, exp: iat + 60, jti: (options.jti ?? defaultJti)() },
    options.key,
  );
}

/** DPoP proof; `htu` is the URI the platform knows (not the address the request is sent to). `accessToken` adds `ath`. */
export function createDpopProof(options: ProofOptions & { method: string; htu: string; accessToken?: string }): string {
  const payload: Record<string, unknown> = {
    htm: options.method,
    htu: options.htu,
    iat: iatOf(options.clock),
    jti: (options.jti ?? defaultJti)(),
  };
  if (options.accessToken !== undefined) {
    payload.ath = createHash('sha256').update(options.accessToken).digest('base64url');
  }
  return jws({ typ: 'dpop+jwt', alg: 'ES256', jwk: options.key.publicJwk }, payload, options.key);
}

export interface StampDiscovery {
  stampId: string;
  softwareKeysAllowed: boolean;
  pairingEndpoint: string;
  activationEndpoint: string;
  tokenEndpoint: string;
}

export interface PairingStart {
  pairingCode: string;
  deviceFingerprint: string;
  pollingHandle: string;
  verificationUri: string;
  expiresAt: string;
  pollingIntervalSeconds: number;
  stampId: string;
}

export interface PairingStatus {
  status: string;
  pollingIntervalSeconds: number;
  expiresAt: string;
  challenge?: string;
  challengeExpiresAt?: string;
}

export interface StatusReportResult {
  managerId: string;
  state: string;
  reportAccepted: boolean;
  reportViolations: { field: string; code: string }[];
}

export interface ScriptedManagerOptions {
  /** Origin of the Web UI, which forwards `/api`; every request is sent there. */
  origin: string;
  hostLabel: string;
  key?: ScriptedKey;
  fetch?: typeof fetch;
}

export class ScriptedManager {
  readonly key: ScriptedKey;
  readonly hostLabel: string;
  private readonly origin: string;
  private readonly fetcher: typeof fetch;
  private stamp?: StampDiscovery;
  private pairing?: PairingStart;
  private managerId?: string;
  private accessToken?: string;

  constructor(options: ScriptedManagerOptions) {
    this.origin = options.origin.replace(/\/+$/, '');
    this.hostLabel = options.hostLabel;
    this.key = options.key ?? generateScriptedKey();
    this.fetcher = options.fetch ?? fetch;
  }

  get pairingStart(): PairingStart {
    if (!this.pairing) throw new Error('Pairing has not been started.');
    return this.pairing;
  }

  get id(): string | undefined {
    return this.managerId;
  }

  /** Sends to the Web UI origin while the proofs keep the URI of the platform's own discovery. */
  private async send(uri: string, init: RequestInit & { headers?: Record<string, string> }): Promise<Response> {
    const url = new URL(uri);
    return this.fetcher(`${this.origin}${url.pathname}${url.search}`, init);
  }

  private async json<T>(response: Response, what: string): Promise<T> {
    if (!response.ok) throw new Error(`${what} failed with ${response.status}: ${await response.text()}`);
    return (await response.json()) as T;
  }

  async discover(): Promise<StampDiscovery> {
    const response = await this.fetcher(`${this.origin}/api/v1/analyst-managers/stamp`);
    this.stamp = await this.json<StampDiscovery>(response, 'Stamp discovery');
    return this.stamp;
  }

  private async discovery(): Promise<StampDiscovery> {
    return this.stamp ?? (await this.discover());
  }

  async startPairing(): Promise<PairingStart> {
    const stamp = await this.discovery();
    const response = await this.send(stamp.pairingEndpoint, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({
        stampId: stamp.stampId,
        publicKey: this.key.publicJwk,
        nonce: b64url(randomBytes(16)),
        deviceMetadata: {
          os: process.platform,
          osVersion: 'scripted',
          architecture: process.arch,
          managerVersion: '0.0.0-scripted',
          claimedKeyProtection: 'software',
          hostLabel: this.hostLabel,
        },
      }),
    });
    this.pairing = await this.json<PairingStart>(response, 'Pairing start');
    return this.pairing;
  }

  async pollStatus(): Promise<PairingStatus> {
    const stamp = await this.discovery();
    const response = await this.send(`${stamp.pairingEndpoint}/status`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ pollingHandle: this.pairingStart.pollingHandle }),
    });
    return this.json<PairingStatus>(response, 'Pairing status');
  }

  /** Polls until the status is `approved` (or `timeoutMs` passes) and returns the status with its challenge. */
  async waitForApproval(timeoutMs = 60_000): Promise<PairingStatus> {
    const deadline = Date.now() + timeoutMs;
    for (;;) {
      const status = await this.pollStatus();
      if (status.status === 'approved' && status.challenge) return status;
      if (Date.now() > deadline) throw new Error(`Pairing was not approved in time (last status: ${status.status}).`);
      await new Promise((resolve) => setTimeout(resolve, Math.min(status.pollingIntervalSeconds, 2) * 1000));
    }
  }

  async activate(challenge: string): Promise<{ managerId: string; keyThumbprint: string }> {
    const stamp = await this.discovery();
    const proof = createActivationProof({ key: this.key, aud: stamp.activationEndpoint, challenge });
    const response = await this.send(stamp.activationEndpoint, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ pollingHandle: this.pairingStart.pollingHandle, proof }),
    });
    const activated = await this.json<{ managerId: string; keyThumbprint: string }>(response, 'Activation');
    this.managerId = activated.managerId;
    return activated;
  }

  async obtainToken(): Promise<string> {
    const stamp = await this.discovery();
    if (!this.managerId) throw new Error('The Manager is not activated.');
    const form = new URLSearchParams({
      grant_type: 'client_credentials',
      client_id: this.managerId,
      client_assertion_type: 'urn:ietf:params:oauth:client-assertion-type:jwt-bearer',
      client_assertion: createClientAssertion({ key: this.key, managerId: this.managerId, aud: stamp.tokenEndpoint }),
      scope: 'analyst-manager',
    });
    const response = await this.send(stamp.tokenEndpoint, {
      method: 'POST',
      headers: {
        'Content-Type': 'application/x-www-form-urlencoded',
        DPoP: createDpopProof({ key: this.key, method: 'POST', htu: stamp.tokenEndpoint }),
      },
      body: form.toString(),
    });
    this.accessToken = (await this.json<{ access_token: string }>(response, 'Token request')).access_token;
    return this.accessToken;
  }

  /** Sends one status report through `POST …/self/status` and returns the platform's registration information. */
  async reportStatus(
    report: { operatingState?: string; activeAnalysts?: number; maxAnalysts?: number; managerVersion?: string } = {},
  ): Promise<StatusReportResult> {
    const stamp = await this.discovery();
    const token = this.accessToken ?? (await this.obtainToken());
    const htu = stamp.tokenEndpoint.replace(/\/token$/, '/self/status');
    const response = await this.send(htu, {
      method: 'POST',
      headers: {
        'Content-Type': 'application/json',
        Authorization: `DPoP ${token}`,
        DPoP: createDpopProof({ key: this.key, method: 'POST', htu, accessToken: token }),
      },
      body: JSON.stringify({
        operatingState: report.operatingState ?? 'running',
        pausedUntil: null,
        runtimeUnavailableReason: null,
        activeAnalysts: report.activeAnalysts ?? 0,
        maxAnalysts: report.maxAnalysts ?? 1,
        managerVersion: report.managerVersion ?? '0.0.0-scripted',
      }),
    });
    return this.json<StatusReportResult>(response, 'Status report');
  }
}
