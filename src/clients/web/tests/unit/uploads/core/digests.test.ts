import { compositeDigest, digestToBase64, sha256Digest } from '../../../../src/uploads/digests';

const hex64 = 'a'.repeat(64);

// Mirrors Content() of the platform's CompositeDigestTests.
function content(length: number, seed: number): Uint8Array<ArrayBuffer> {
  const data = new Uint8Array(length);
  for (let i = 0; i < length; i++) {
    data[i] = (i * 31 + seed) % 251;
  }
  return data;
}

describe('digests', () => {
  it('formats a part digest as sha-256 and lowercase hex', async () => {
    expect(await sha256Digest(new Uint8Array(0))).toBe(
      'sha-256:e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855',
    );
  });

  it('converts a digest to base64 like the platform', () => {
    expect(digestToBase64(`sha-256:${hex64}`)).toBe('qqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqo=');
  });

  it('rejects malformed digests', () => {
    expect(() => digestToBase64('sha-256:abcd')).toThrow(TypeError);
    expect(() => digestToBase64(`sha-256:${'A'.repeat(64)}`)).toThrow(TypeError);
    expect(() => digestToBase64(`sha-512:${hex64}`)).toThrow(TypeError);
  });

  it('matches the platform spike vector', async () => {
    const partSize = 5_242_880;
    const digests = await Promise.all(
      [content(partSize, 1), content(partSize, 2), content(1_234_567, 3)].map((part) => sha256Digest(part)),
    );
    expect(await compositeDigest(partSize, digests)).toBe(
      'sha-256-parts:5242880:3:a1d27e61e08fff218f3eadfc57a5c60794263ebae381b3d819c9df4cd3312ac2',
    );
  });

  it('hashes a single part twice and differs from the plain digest', async () => {
    const plain = await sha256Digest(content(1000, 7));
    expect(plain).toBe('sha-256:008549d94fa71e7a0a483d84380d05a923a4b18e79ba1f8a8ddac923956d32ef');
    const composite = await compositeDigest(1000, [plain]);
    expect(composite).toBe('sha-256-parts:1000:1:a5f998fc390196f6d0c72a3494f38548e21d312ec821426b73b31f5e192ba8eb');
    expect(composite).not.toContain(plain.slice('sha-256:'.length));
  });

  it('combines two digests in order', async () => {
    expect(await compositeDigest(5_242_880, [`sha-256:${hex64}`, `sha-256:${'b'.repeat(64)}`])).toBe(
      'sha-256-parts:5242880:2:e2d80f78d79027556d6619a1400605abbdca6bb6eb24e0831e33ecd5466fa5f6',
    );
  });

  it('refuses an empty list', async () => {
    await expect(compositeDigest(10, [])).rejects.toThrow(RangeError);
  });
});
