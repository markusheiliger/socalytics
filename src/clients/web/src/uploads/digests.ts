const partDigestPattern = /^sha-256:([0-9a-f]{64})$/;

function toHex(bytes: Uint8Array): string {
  return Array.from(bytes, (byte) => byte.toString(16).padStart(2, '0')).join('');
}

function fromHex(hex: string): Uint8Array<ArrayBuffer> {
  const bytes = new Uint8Array(hex.length / 2);
  for (let i = 0; i < bytes.length; i++) {
    bytes[i] = Number.parseInt(hex.slice(i * 2, i * 2 + 2), 16);
  }
  return bytes;
}

function parsePartDigest(digest: string): string {
  const match = partDigestPattern.exec(digest);
  if (!match) {
    throw new TypeError('Not a sha-256 part digest.');
  }
  return match[1];
}

export async function sha256Digest(bytes: BufferSource): Promise<string> {
  const hash = new Uint8Array(await crypto.subtle.digest('SHA-256', bytes));
  return `sha-256:${toHex(hash)}`;
}

// The value of the `x-amz-checksum-sha256` header.
export function digestToBase64(digest: string): string {
  const bytes = fromHex(parsePartDigest(digest));
  let binary = '';
  for (const byte of bytes) {
    binary += String.fromCharCode(byte);
  }
  return btoa(binary);
}

// SHA-256 over the concatenated raw part digests, as the platform's CompositeContentDigest does.
export async function compositeDigest(partSizeBytes: number, partDigests: readonly string[]): Promise<string> {
  if (partDigests.length === 0) {
    throw new RangeError('A composite digest needs at least one part digest.');
  }
  const concatenated = new Uint8Array(partDigests.length * 32);
  partDigests.forEach((digest, index) => concatenated.set(fromHex(parsePartDigest(digest)), index * 32));
  const hash = new Uint8Array(await crypto.subtle.digest('SHA-256', concatenated));
  return `sha-256-parts:${partSizeBytes}:${partDigests.length}:${toHex(hash)}`;
}
