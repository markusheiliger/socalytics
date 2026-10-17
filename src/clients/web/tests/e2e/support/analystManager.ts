import { generateKeyPairSync, randomBytes } from 'node:crypto';

const PLATFORM_URL = 'http://127.0.0.1:4174';

export type ScriptedPairing = {
  pairingCode: string;
  deviceFingerprint: string;
  hostLabel: string;
};

/**
 * A scripted Analyst Manager for tests: it generates a P-256 key pair and starts a pairing with the platform's
 * pairing API (contracts/analyst-manager/registration/v1). The AppHost in e2e mode serves the API over http and
 * trusts forwarded headers, so the request declares the https scheme the pairing API requires.
 */
export async function startPairing(baseURL: string = PLATFORM_URL): Promise<ScriptedPairing> {
  const { publicKey } = generateKeyPairSync('ec', { namedCurve: 'P-256' });
  const { kty, crv, x, y } = publicKey.export({ format: 'jwk' });
  const headers = { 'Content-Type': 'application/json', 'X-Forwarded-Proto': 'https' };

  const discovery = await fetch(`${baseURL}/api/v1/analyst-managers/stamp`, { headers });
  if (!discovery.ok) throw new Error(`Stamp discovery answered ${discovery.status}: ${await discovery.text()}`);
  const { stampId } = (await discovery.json()) as { stampId: string };

  const hostLabel = `e2e-host-${randomBytes(4).toString('hex')}`;
  const response = await fetch(`${baseURL}/api/v1/analyst-managers/pairings`, {
    method: 'POST',
    headers,
    body: JSON.stringify({
      stampId,
      publicKey: { kty, crv, x, y },
      nonce: randomBytes(24).toString('base64url'),
      deviceMetadata: {
        os: 'linux',
        osVersion: 'e2e-os',
        architecture: 'x64',
        managerVersion: '1.0.0',
        claimedKeyProtection: 'software',
        hostLabel,
      },
    }),
  });
  if (!response.ok) throw new Error(`Starting the pairing answered ${response.status}: ${await response.text()}`);
  const { pairingCode, deviceFingerprint } = (await response.json()) as { pairingCode: string; deviceFingerprint: string };
  return { pairingCode, deviceFingerprint, hostLabel };
}
